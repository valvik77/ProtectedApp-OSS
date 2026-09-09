using System.Text;
using System.Text.Json;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

public static class ActivityExportService
{
    public static byte[] CreateCsv(IEnumerable<ActivityEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Fecha y hora;Aplicación;Tipo;Evento");
        foreach (var entry in entries)
        {
            builder.Append(EscapeCsv(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))).Append(';')
                .Append(EscapeCsv(entry.AppName)).Append(';')
                .Append(EscapeCsv(entry.KindLabel)).Append(';')
                .Append(EscapeCsv(entry.Message)).AppendLine();
        }

        var content = Encoding.UTF8.GetBytes(builder.ToString());
        var preamble = Encoding.UTF8.GetPreamble();
        var result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    public static byte[] CreateJson(IEnumerable<ActivityEntry> entries) =>
        JsonSerializer.SerializeToUtf8Bytes(entries.Select(entry => new ExportedActivityEntry
        {
            Timestamp = entry.Timestamp.ToLocalTime(),
            Application = entry.AppName,
            Type = entry.KindLabel,
            Event = entry.Message
        }), new JsonSerializerOptions { WriteIndented = true });

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(';') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private sealed class ExportedActivityEntry
    {
        public DateTimeOffset Timestamp { get; init; }
        public string Application { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Event { get; init; } = string.Empty;
    }
}
