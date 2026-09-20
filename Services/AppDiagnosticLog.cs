namespace ProtectedApp.Services;

/// <summary>
/// Appends launch and crash diagnostics to <c>%LOCALAPPDATA%\ProtectedApp</c>.
/// Every launch and activation writes here, so an unbounded file grows for the
/// life of the installation. A file that reaches <see cref="MaximumBytes"/> is
/// rotated to a single <c>.1</c> companion, bounding disk use to about twice
/// that size. Logging must never disturb the caller, so failures are swallowed.
/// </summary>
internal static class AppDiagnosticLog
{
    internal const long MaximumBytes = 256 * 1024;
    private static readonly object Sync = new();

    public static void Append(string fileName, string text) => AppendTo(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp", fileName),
        text);

    internal static void AppendTo(string path, string text, long maximumBytes = MaximumBytes)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded(path, maximumBytes);
                File.AppendAllText(path, text);
            }
        }
        catch { }
    }

    private static void RotateIfNeeded(string path, long maximumBytes)
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length < maximumBytes) return;
        File.Move(path, path + ".1", overwrite: true);
    }
}
