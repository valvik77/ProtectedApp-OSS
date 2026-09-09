using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProtectedApp.Service;

internal enum TamperEventCode { TamperDetected, RecoveryFailed }

internal sealed class TamperWebhookNotifier(ILogger<TamperWebhookNotifier> logger) : BackgroundService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProtectedApp.Guardian.Webhook.v1");
    private static readonly byte[] InstallationEntropy = Encoding.UTF8.GetBytes("ProtectedApp.Guardian.Webhook.Installation.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim SendGate = new(1, 1);
    private static readonly object QueueSync = new();
    private static readonly object InstallationSync = new();
    // A SYSTEM service must never let a configured URL become a route to local
    // administration endpoints. Redirects and ambient proxies bypass a simple
    // "https://" check, so every connection is resolved and filtered here.
    private static readonly HttpClient Client = CreateHttpClient();

    public static WebhookStatus GetStatus()
    {
        var config = LoadConfig();
        return new(config?.Enabled == true, config?.Url, config?.UseHmac == true, GetOrCreateInstallationId());
    }

    public static void Configure(bool enabled, string? url, bool useHmac, string? secret)
    {
        if (enabled)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSafeWebhookUri(uri))
                throw new InvalidDataException("El webhook debe ser una URL HTTPS pública válida, sin redirecciones.");
        }
        Directory.CreateDirectory(GuardianConstants.PolicyFolder);
        if (!enabled)
        {
            File.Delete(GuardianConstants.WebhookConfigPath);
            File.Delete(GuardianConstants.WebhookQueuePath);
            return;
        }
        var existing = LoadConfig();
        var effectiveSecret = useHmac
            ? (string.IsNullOrWhiteSpace(secret) && existing?.UseHmac == true ? existing.Secret : secret)
            : null;
        if (useHmac && string.IsNullOrWhiteSpace(effectiveSecret))
            throw new InvalidDataException("Indica un secreto HMAC la primera vez que actives la firma.");
        _ = GetOrCreateInstallationId();
        var json = JsonSerializer.SerializeToUtf8Bytes(new WebhookConfig(true, url!, useHmac, effectiveSecret), JsonOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.LocalMachine);
        WriteBytesAtomically(GuardianConstants.WebhookConfigPath, encrypted);
    }

    public static void Enqueue(TamperEventCode code)
    {
        if (LoadConfig()?.Enabled != true) return;
        lock (QueueSync)
        {
            var queue = LoadQueue();
            queue.Add(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, code.ToString(), 0, DateTimeOffset.UtcNow));
            if (queue.Count > 20) queue.RemoveRange(0, queue.Count - 20);
            SaveQueue(queue);
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR")))
            _ = Task.Run(() => SendPendingOnceAsync(null, CancellationToken.None));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SendPendingOnceAsync(logger, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo procesar la cola de alertas remotas."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    internal static async Task SendPendingOnceAsync(ILogger? log, CancellationToken token)
    {
        if (!await SendGate.WaitAsync(0, token)) return;
        try
        {
            var config = LoadConfig();
            if (config?.Enabled != true || !Uri.TryCreate(config.Url, UriKind.Absolute, out var uri)
                || !IsSafeWebhookUri(uri)) return;
            WebhookEvent? item;
            lock (QueueSync) item = LoadQueue().Where(x => x.NextAttemptUtc <= DateTimeOffset.UtcNow)
                .OrderBy(x => x.OccurredUtc).FirstOrDefault();
            if (item is null) return;
            var body = CreatePayload(item.Id, GetOrCreateInstallationId(), item.OccurredUtc, item.Code);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (config.UseHmac && !string.IsNullOrWhiteSpace(config.Secret))
                request.Headers.TryAddWithoutValidation("X-ProtectedApp-Signature", ComputeSignature(body, config.Secret));
            try
            {
                using var response = await Client.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                UpdateQueue(item.Id, remove: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                log?.LogWarning(ex, "No se pudo enviar la alerta remota de manipulación.");
                var discard = item.Attempts >= 4;
                UpdateQueue(item.Id, remove: discard);
                if (discard) log?.LogWarning("Se descartó la alerta remota {EventId} tras agotar los reintentos.", item.Id);
            }
        }
        finally { SendGate.Release(); }
    }

    private static void UpdateQueue(string id, bool remove)
    {
        lock (QueueSync)
        {
            var queue = LoadQueue();
            var index = queue.FindIndex(x => x.Id == id);
            if (index < 0) return;
            if (remove) queue.RemoveAt(index);
            else
            {
                var current = queue[index];
                var delays = new[] { 5, 15, 30, 60 };
                var attempts = current.Attempts + 1;
                queue[index] = current with { Attempts = attempts, NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(delays[Math.Min(attempts - 1, delays.Length - 1)]) };
            }
            SaveQueue(queue);
        }
    }

    private static WebhookConfig? LoadConfig()
    {
        if (!File.Exists(GuardianConstants.WebhookConfigPath)) return null;
        var json = ProtectedData.Unprotect(File.ReadAllBytes(GuardianConstants.WebhookConfigPath), Entropy, DataProtectionScope.LocalMachine);
        return JsonSerializer.Deserialize<WebhookConfig>(json, JsonOptions);
    }
    internal static string ComputeSignature(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
    internal static string CreatePayload(string eventId, string installationId, DateTimeOffset occurredUtc, string eventCode) =>
        JsonSerializer.Serialize(new { eventId, installationId, occurredUtc, @event = eventCode }, JsonOptions);

    private static string GetOrCreateInstallationId()
    {
        lock (InstallationSync)
        {
            if (File.Exists(GuardianConstants.WebhookInstallationIdPath))
            {
                var protectedBytes = File.ReadAllBytes(GuardianConstants.WebhookInstallationIdPath);
                var bytes = ProtectedData.Unprotect(protectedBytes, InstallationEntropy, DataProtectionScope.LocalMachine);
                var persisted = Encoding.UTF8.GetString(bytes);
                if (Guid.TryParse(persisted, out var installationId)) return installationId.ToString("D");
                throw new InvalidDataException("El identificador de instalación del webhook no es válido.");
            }

            var created = Guid.NewGuid().ToString("D");
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(created), InstallationEntropy, DataProtectionScope.LocalMachine);
            WriteBytesAtomically(GuardianConstants.WebhookInstallationIdPath, encrypted);
            return created;
        }
    }
    private static List<WebhookEvent> LoadQueue()
    {
        if (!File.Exists(GuardianConstants.WebhookQueuePath)) return [];
        return JsonSerializer.Deserialize<List<WebhookEvent>>(File.ReadAllText(GuardianConstants.WebhookQueuePath), JsonOptions) ?? [];
    }
    private static void SaveQueue(List<WebhookEvent> queue) => WriteTextAtomically(GuardianConstants.WebhookQueuePath, JsonSerializer.Serialize(queue, JsonOptions));
    private static void WriteTextAtomically(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllText(temp, content); File.Move(temp, path, true); }
    private static void WriteBytesAtomically(string path, byte[] content) { var temp = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllBytes(temp, content); File.Move(temp, path, true); }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = ConnectToPublicEndpointAsync
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static async ValueTask<Stream> ConnectToPublicEndpointAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        var allowed = addresses.Where(IsPublicAddress).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("El destino del webhook no resuelve a una dirección pública.");

        Exception? lastError = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException("No se pudo conectar al destino público del webhook.", lastError);
    }

    private static bool IsSafeWebhookUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port is <= 0 or > 65535)
            return false;
        // DNS is deliberately checked again inside ConnectCallback. Resolving
        // here would make configuring an endpoint depend on transient network
        // availability and would not protect against later DNS rebinding.
        return !IPAddress.TryParse(uri.DnsSafeHost, out var address) || IsPublicAddress(address);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ipv6Bytes = address.GetAddressBytes();
            return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal
                && !address.IsIPv6Multicast && (ipv6Bytes[0] & 0xFE) != 0xFC; // Unique-local fc00::/7
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 when bytes[1] is >= 64 and <= 127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 168 => false,
            _ => true
        };
    }
    private sealed record WebhookConfig(bool Enabled, string Url, bool UseHmac, string? Secret);
    private sealed record WebhookEvent(string Id, DateTimeOffset OccurredUtc, string Code, int Attempts, DateTimeOffset NextAttemptUtc);
}

internal sealed record WebhookStatus(bool Enabled, string? Url, bool UseHmac, string InstallationId);
