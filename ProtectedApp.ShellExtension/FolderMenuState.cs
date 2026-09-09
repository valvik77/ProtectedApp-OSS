using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ProtectedApp.ShellExtension
{
    public enum FolderMenuAction
    {
        Protect,
        Activate,
        Unlock,
        LockNow
    }

    public static class FolderMenuState
    {
        public static string DefaultStatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProtectedApp",
            "folder-shell-state.json");

        public static FolderMenuAction Resolve(string selectedPath, string? statePath = null, DateTimeOffset? now = null)
        {
            if (string.IsNullOrWhiteSpace(selectedPath)) return FolderMenuAction.Protect;
            try
            {
                var normalized = Normalize(selectedPath);
                var path = string.IsNullOrWhiteSpace(statePath) ? DefaultStatePath : statePath!;
                if (!File.Exists(path)) return FolderMenuAction.Protect;
                var state = new JavaScriptSerializer().Deserialize<FolderShellState>(File.ReadAllText(path));
                var folders = state?.Folders ?? new List<FolderShellEntry>();
                foreach (var folder in folders)
                {
                    if (folder == null || string.IsNullOrWhiteSpace(folder.Path)
                        || !string.Equals(Normalize(folder.Path), normalized, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!folder.IsEnabled) return FolderMenuAction.Activate;
                    return folder.UnlockedUntilUtc.HasValue
                        && folder.UnlockedUntilUtc.Value > (now ?? DateTimeOffset.UtcNow)
                        ? FolderMenuAction.LockNow
                        : FolderMenuAction.Unlock;
                }
            }
            catch
            {
                // The cache is advisory. ProtectedApp validates the real state
                // with Guardian after the command is invoked.
            }
            return FolderMenuAction.Protect;
        }

        public static bool IsProtected(string selectedPath, string? statePath = null)
        {
            if (string.IsNullOrWhiteSpace(selectedPath)) return false;
            try
            {
                var path = string.IsNullOrWhiteSpace(statePath) ? DefaultStatePath : statePath!;
                var normalized = Normalize(selectedPath);
                // Explorer may retain the COM object beyond file timestamp
                // granularity. Read this small state file for each overlay
                // query so the overlay always agrees with the menu action.
                var state = File.Exists(path)
                    ? new JavaScriptSerializer().Deserialize<FolderShellState>(File.ReadAllText(path))
                    : null;
                return (state?.Folders ?? new List<FolderShellEntry>())
                    .Any(folder => folder != null && folder.IsEnabled
                        && !string.IsNullOrWhiteSpace(folder.Path)
                        && string.Equals(Normalize(folder.Path), normalized,
                            StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        public static string GetTitle(FolderMenuAction action)
        {
            switch (action)
            {
                case FolderMenuAction.Activate: return "Activar protección de carpeta";
                case FolderMenuAction.Unlock: return "Desbloquear carpeta con ProtectedApp";
                case FolderMenuAction.LockNow: return "Bloquear carpeta ahora";
                default: return "Proteger carpeta con ProtectedApp…";
            }
        }

        public static string GetToolTip(FolderMenuAction action)
        {
            switch (action)
            {
                case FolderMenuAction.Activate: return "Vuelve a aplicar la protección configurada para esta carpeta.";
                case FolderMenuAction.Unlock: return "Solicita la contraseña y abre temporalmente la carpeta.";
                case FolderMenuAction.LockNow: return "Finaliza el acceso temporal y bloquea la carpeta inmediatamente.";
                default: return "Añade la carpeta a ProtectedApp y configura su protección.";
            }
        }

        private static string Normalize(string path) => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private sealed class FolderShellState
        {
            public List<FolderShellEntry> Folders { get; set; } = new List<FolderShellEntry>();
        }

        private sealed class FolderShellEntry
        {
            public string Path { get; set; } = string.Empty;
            public bool IsEnabled { get; set; }
            public DateTimeOffset? UnlockedUntilUtc { get; set; }
        }
    }
}
