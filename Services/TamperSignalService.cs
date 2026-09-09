using System.Text.Json;

namespace ProtectedApp.Services;

public static class TamperSignalService
{
    private static string SignalPath => Environment.GetEnvironmentVariable("PROTECTEDAPP_TAMPER_SIGNAL_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ProtectedApp", "guardian-tamper.json");
    private static string AcknowledgementPath => Environment.GetEnvironmentVariable("PROTECTEDAPP_TAMPER_ACK_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProtectedApp", "guardian-tamper.ack");

    public static bool TryPeek(out TamperSignalInfo signal)
    {
        signal = new TamperSignalInfo(string.Empty, DateTimeOffset.MinValue, string.Empty);
        if (!TryPeekBatch(out var batch)) return false;
        signal = batch.Signals[0];
        return true;
    }

    public static bool TryPeekBatch(out TamperSignalBatchInfo batch)
    {
        batch = new TamperSignalBatchInfo([]);
        try
        {
            if (!File.Exists(SignalPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(SignalPath));
            var signals = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(ReadSignal).Where(item => item is not null).Cast<TamperSignal>().ToList()
                : ReadSignal(document.RootElement) is { } single ? [single] : [];
            var recent = signals
                .OrderBy(item => item.TimestampUtc)
                .ToList();
            if (recent.Count == 0) return false;

            var acknowledgedId = File.Exists(AcknowledgementPath)
                ? File.ReadAllText(AcknowledgementPath).Trim()
                : string.Empty;
            var acknowledgedIndex = recent.FindIndex(item => item.Id == acknowledgedId);
            var pending = (acknowledgedIndex >= 0
                    ? recent.Skip(acknowledgedIndex + 1)
                    : recent.Where(item => item.Id != acknowledgedId))
                .Select(item => new TamperSignalInfo(
                    item.Id,
                    item.TimestampUtc,
                    string.IsNullOrWhiteSpace(item.Reason)
                        ? "Se detectó una manipulación del servicio de protección."
                        : item.Reason))
                .ToArray();
            if (pending.Length == 0) return false;

            batch = new TamperSignalBatchInfo(pending);
            return true;
        }
        catch { return false; }
    }

    public static bool Acknowledge(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AcknowledgementPath)!);
            File.WriteAllText(AcknowledgementPath, id);
            return true;
        }
        catch { return false; }
    }

    public static bool TryConsume(out string reason)
    {
        reason = string.Empty;
        if (!TryPeek(out var signal) || !Acknowledge(signal.Id)) return false;
        reason = signal.Reason;
        return true;
    }

    private static TamperSignal? ReadSignal(JsonElement element)
    {
        if (!element.TryGetProperty("Id", out var idProperty)
            || !element.TryGetProperty("TimestampUtc", out var timestampProperty)) return null;
        var id = idProperty.GetString();
        if (string.IsNullOrWhiteSpace(id) || !timestampProperty.TryGetDateTimeOffset(out var timestamp)) return null;
        var message = element.TryGetProperty("Reason", out var reasonProperty) ? reasonProperty.GetString() : null;
        return new TamperSignal(id, timestamp, message);
    }

    private sealed record TamperSignal(string Id, DateTimeOffset TimestampUtc, string? Reason);
}

public sealed record TamperSignalInfo(string Id, DateTimeOffset TimestampUtc, string Reason);
public sealed record TamperSignalBatchInfo(IReadOnlyList<TamperSignalInfo> Signals)
{
    public string LastId => Signals.Count == 0 ? string.Empty : Signals[^1].Id;
}
