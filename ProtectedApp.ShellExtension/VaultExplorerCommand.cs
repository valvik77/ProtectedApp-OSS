using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ProtectedApp.ShellExtension
{
    [ComVisible(true)]
    [Guid(ClassId)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class VaultExplorerCommand : IExplorerCommand
    {
        public const string ClassId = "42C771D7-2B8A-464C-97AB-BE23E892F0F9";
        private static readonly Guid CanonicalName = new Guid("3DEB87CF-61C4-4748-9F3A-1D5A83879250");

        public int GetTitle(IShellItemArray selection, out IntPtr title) => GetString(selection,
            path => VaultMenuState.GetTitle(path), out title);

        public int GetIcon(IShellItemArray selection, out IntPtr icon) => GetString(selection, _ => IconPath(), out icon);

        public int GetToolTip(IShellItemArray selection, out IntPtr toolTip) => GetString(selection,
            path => VaultMenuState.GetToolTip(path), out toolTip);

        public int GetCanonicalName(out Guid commandName) { commandName = CanonicalName; return HResult.Ok; }

        public int GetState(IShellItemArray selection, bool allowSlowOperations, out uint state)
        {
            state = GetSingleVaultPath(selection) is null ? (uint)ExplorerCommandState.Hidden : (uint)ExplorerCommandState.Enabled;
            return HResult.Ok;
        }

        public int Invoke(IShellItemArray selection, IntPtr bindContext)
        {
            try
            {
                var path = GetSingleVaultPath(selection);
                if (path is null) return HResult.InvalidArgument;
                var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var appPath = Path.GetFullPath(Path.Combine(folder, "..", "ProtectedApp.exe"));
                if (!File.Exists(appPath)) return HResult.FileNotFound;
                Process.Start(new ProcessStartInfo
                {
                    FileName = appPath,
                    Arguments = "--vault-action " + WindowsCommandLine.Quote(path),
                    WorkingDirectory = Path.GetDirectoryName(appPath),
                    UseShellExecute = true
                });
                return HResult.Ok;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }

        public int GetFlags(out uint flags) { flags = (uint)ExplorerCommandFlags.Default; return HResult.Ok; }
        public int EnumSubCommands(out IntPtr commands) { commands = IntPtr.Zero; return HResult.NotImplemented; }

        private static int GetString(IShellItemArray selection, Func<string, string> create, out IntPtr result)
        {
            result = IntPtr.Zero;
            try
            {
                var path = GetSingleVaultPath(selection);
                if (path is null) return HResult.InvalidArgument;
                result = Marshal.StringToCoTaskMemUni(create(path));
                return HResult.Ok;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }

        private static string? GetSingleVaultPath(IShellItemArray? selection)
        {
            if (selection is null || selection.GetCount(out var count) != HResult.Ok || count != 1
                || selection.GetItemAt(0, out var item) != HResult.Ok || item is null) return null;
            if (item.GetDisplayName(ShellDisplayName.FileSystemPath, out var pointer) != HResult.Ok || pointer == IntPtr.Zero) return null;
            try
            {
                var path = Marshal.PtrToStringUni(pointer);
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    && path.EndsWith(".pavault", StringComparison.OrdinalIgnoreCase) ? path : null;
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }

        private static string IconPath()
        {
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            return Path.GetFullPath(Path.Combine(folder, "..", "Assets", "ProtectedApp.ico")) + ",0";
        }
    }
}
