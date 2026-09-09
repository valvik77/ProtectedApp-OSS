using System.Text.Json;
using System.Runtime.InteropServices;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

public static class FolderShellStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly object Sync = new();
    private static string? _lastContent;

    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProtectedApp",
        "folder-shell-state.json");

    public static void Save(IEnumerable<ProtectedFolder> folders)
    {
        try
        {
            var state = new FolderShellState
            {
                Folders = folders.Select(folder => new FolderShellEntry
                {
                    Path = Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    IsEnabled = folder.IsEnabled,
                    UnlockedUntilUtc = folder.UnlockedUntilUtc
                }).ToList()
            };
            var content = JsonSerializer.Serialize(state, JsonOptions);
            lock (Sync)
            {
                if (string.Equals(content, _lastContent, StringComparison.Ordinal)) return;
                var directory = Path.GetDirectoryName(StatePath)!;
                Directory.CreateDirectory(directory);
                var temporary = StatePath + ".tmp-" + Environment.ProcessId;
                File.WriteAllText(temporary, content);
                File.Move(temporary, StatePath, true);
                _lastContent = content;
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // This cache only controls the label shown by Explorer. Guardian and
            // the application always re-evaluate the authoritative policy.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private sealed class FolderShellState
    {
        public List<FolderShellEntry> Folders { get; set; } = [];
    }

    private sealed class FolderShellEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public DateTimeOffset? UnlockedUntilUtc { get; set; }
    }
}
