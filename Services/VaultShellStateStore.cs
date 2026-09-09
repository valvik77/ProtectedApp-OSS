using System.Runtime.InteropServices;
using System.Text.Json;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

public static class VaultShellStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly object Sync = new();
    private static string? _lastContent;

    public static void Save(IEnumerable<VaultContainer> vaults)
    {
        try
        {
            var content = JsonSerializer.Serialize(new VaultShellState
            {
                Vaults = vaults.Where(vault => !string.IsNullOrWhiteSpace(vault.VaultFilePath))
                    .Select(vault => new VaultShellEntry
                    {
                        Path = Path.GetFullPath(vault.VaultFilePath!),
                        IsMounted = vault.IsMounted
                    }).ToList()
            }, JsonOptions);
            lock (Sync)
            {
                if (string.Equals(content, _lastContent, StringComparison.Ordinal)) return;
                var path = StatePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp-" + Environment.ProcessId;
                File.WriteAllText(temporary, content);
                File.Move(temporary, path, true);
                _lastContent = content;
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
    }

    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProtectedApp", "vault-shell-state.json");

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private sealed class VaultShellState { public List<VaultShellEntry> Vaults { get; set; } = []; }
    private sealed class VaultShellEntry { public string Path { get; set; } = string.Empty; public bool IsMounted { get; set; } }
}
