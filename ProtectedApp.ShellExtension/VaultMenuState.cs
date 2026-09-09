using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ProtectedApp.ShellExtension
{
    public static class VaultMenuState
    {
        private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProtectedApp", "vault-shell-state.json");

        public static bool IsMounted(string vaultPath)
        {
            try
            {
                var normalized = Normalize(vaultPath);
                // Explorer can reuse this COM object longer than the file
                // timestamp granularity. The state file is tiny and opening a
                // context menu is infrequent, so read it afresh to ensure the
                // label immediately changes between montar/desmontar.
                var state = File.Exists(StatePath)
                    ? new JavaScriptSerializer().Deserialize<VaultShellState>(File.ReadAllText(StatePath))
                    : null;
                return (state?.Vaults ?? new List<VaultShellEntry>())
                    .Where(vault => vault != null && !string.IsNullOrWhiteSpace(vault.Path))
                    .Any(vault => string.Equals(Normalize(vault.Path), normalized,
                        StringComparison.OrdinalIgnoreCase) && vault.IsMounted);
            }
            catch { return false; }
        }

        public static string GetTitle(string vaultPath) => IsMounted(vaultPath)
            ? "Desmontar bóveda con ProtectedApp"
            : "Montar/Desmontar bóveda";

        public static string GetToolTip(string vaultPath) => IsMounted(vaultPath)
            ? "Guarda los cambios, desmonta la unidad virtual y protege la bóveda."
            : "Solicita la contraseña y monta la bóveda como unidad virtual.";

        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        private sealed class VaultShellState { public List<VaultShellEntry> Vaults { get; set; } = new List<VaultShellEntry>(); }
        private sealed class VaultShellEntry { public string Path { get; set; } = string.Empty; public bool IsMounted { get; set; } }
    }
}
