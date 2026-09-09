using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProtectedApp.Shared;

// IFEO starts this process instead of the protected image. Do not launch the
// original executable here: only Guardian may do that after authentication.
var targetMarker = Array.FindIndex(args, value => value.Equals("--target", StringComparison.OrdinalIgnoreCase));
var hostMarker = Array.FindIndex(args, value => value.Equals("--host", StringComparison.OrdinalIgnoreCase));
var marker = targetMarker >= 0 ? targetMarker : hostMarker;
if (marker < 0 || marker + 1 >= args.Length) return;

string interceptedPath;
try { interceptedPath = Path.GetFullPath(args[marker + 1]); }
catch { return; }

var hostArguments = string.Empty;
if (hostMarker >= 0)
{
    var forwarded = args.Skip(hostMarker + 2).ToList();
    if (forwarded.Count > 0 && PathsEqual(forwarded[0], interceptedPath)) forwarded.RemoveAt(0);
    hostArguments = string.Join(" ", forwarded.Select(QuoteArgument));
}

try
{
    await using var pipe = new NamedPipeClientStream(".", GuardianProtocol.PipeName,
        PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await pipe.ConnectAsync(timeout.Token);

    var request = new GuardianRequest
    {
        Type = hostMarker >= 0 ? GuardianProtocol.RegisterHostAttempt : GuardianProtocol.RegisterBlockedAttempt,
        UserSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty,
        SessionId = Process.GetCurrentProcess().SessionId,
        TargetPath = targetMarker >= 0 ? interceptedPath : null,
        HostPath = hostMarker >= 0 ? interceptedPath : null,
        HostArguments = hostMarker >= 0 ? hostArguments : null,
        WorkingDirectory = Environment.CurrentDirectory
    };
    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        { AutoFlush = true };
    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    await writer.WriteLineAsync(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var responseLine = await reader.ReadLineAsync(timeout.Token);
    var response = string.IsNullOrWhiteSpace(responseLine)
        ? null
        : JsonSerializer.Deserialize<GuardianResponse>(responseLine,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (response?.Success != true)
        ShowGateNotice(interceptedPath, response?.Error
            ?? "Guardian no devolvió una respuesta válida. La ejecución se bloqueó de forma segura.");
}
catch
{
    // Fail closed: if Guardian cannot be reached, the original image remains
    // unexecuted. Its next attempt will be handled when the service recovers.
    ShowGateNotice(interceptedPath,
        "El servicio Guardian no responde. La ejecución se bloqueó de forma segura. Abre ProtectedApp y utiliza Diagnóstico para repararlo.");
}

static void ShowGateNotice(string targetPath, string message)
{
    try
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetPath)))[..16];
        using var mutex = new Mutex(true, $@"Local\ProtectedApp.GateNotice.{fingerprint}", out var isFirst);
        if (!isFirst) return;
        GateNative.MessageBox(IntPtr.Zero,
            $"{Path.GetFileNameWithoutExtension(targetPath)} no se ha iniciado.\n\n{message}",
            "ProtectedApp", GateNative.MbOk | GateNative.MbIconWarning | GateNative.MbSetForeground | GateNative.MbTopMost);
    }
    catch { }
}

static bool PathsEqual(string left, string right)
{
    try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
    catch { return false; }
}

static string QuoteArgument(string value)
{
    if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
    var result = new StringBuilder("\"");
    var slashes = 0;
    foreach (var character in value)
    {
        if (character == '\\') { slashes++; continue; }
        if (character == '"')
        {
            result.Append('\\', slashes * 2 + 1).Append('"');
            slashes = 0;
            continue;
        }
        result.Append('\\', slashes).Append(character);
        slashes = 0;
    }
    result.Append('\\', slashes * 2).Append('"');
    return result.ToString();
}

internal static class GateNative
{
    public const uint MbOk = 0x00000000;
    public const uint MbIconWarning = 0x00000030;
    public const uint MbSetForeground = 0x00010000;
    public const uint MbTopMost = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    public static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
