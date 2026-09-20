using System.Text;

namespace ProtectedApp.Services;

/// <summary>
/// Appends launch and crash diagnostics to <c>%LOCALAPPDATA%\ProtectedApp</c>.
/// Every launch and activation writes here, so an unbounded file grows for the
/// life of the installation. No file ever exceeds <see cref="MaximumBytes"/>:
/// an entry that would not fit is preceded by rotation to a single <c>.1</c>
/// companion, and an entry larger than the limit itself is truncated. Disk use
/// is therefore bounded to twice that size. Logging must never disturb the
/// caller, so failures are swallowed.
/// </summary>
internal static class AppDiagnosticLog
{
    internal const long MaximumBytes = 256 * 1024;
    internal const string TruncationMarker = "\n[truncated]\n";
    private static readonly object Sync = new();

    public static void Append(string fileName, string text) => AppendTo(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp", fileName),
        text);

    internal static void AppendTo(string path, string text, long maximumBytes = MaximumBytes)
    {
        try
        {
            var entry = Fit(Encoding.UTF8.GetBytes(text), maximumBytes);
            if (entry.Length == 0) return;
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfFull(path, entry.Length, maximumBytes);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(entry);
            }
        }
        catch { }
    }

    /// <summary>Cuts an oversized entry on a character boundary and marks the cut.</summary>
    private static byte[] Fit(byte[] entry, long maximumBytes)
    {
        if (entry.Length <= maximumBytes) return entry;
        var marker = Encoding.UTF8.GetBytes(TruncationMarker);
        var keep = maximumBytes - marker.Length;
        if (keep <= 0) return [];
        var cut = (int)keep;
        // A UTF-8 continuation byte (10xxxxxx) means the cut would split a character.
        while (cut > 0 && (entry[cut] & 0xC0) == 0x80) cut--;
        var result = new byte[cut + marker.Length];
        Buffer.BlockCopy(entry, 0, result, 0, cut);
        Buffer.BlockCopy(marker, 0, result, cut, marker.Length);
        return result;
    }

    /// <summary>
    /// Rotates the current file when <paramref name="entryLength"/> more bytes would
    /// exceed the limit. If rotation fails (for instance another process holds the
    /// file) the exception aborts the append, so the entry is dropped rather than
    /// letting the file exceed the limit.
    /// </summary>
    private static void RotateIfFull(string path, int entryLength, long maximumBytes)
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length + entryLength <= maximumBytes) return;
        File.Move(path, path + ".1", overwrite: true);
    }
}
