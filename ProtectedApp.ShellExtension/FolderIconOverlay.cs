using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ProtectedApp.ShellExtension
{
    [ComVisible(true)]
    [Guid(ClassId)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class FolderIconOverlay : IShellIconOverlayIdentifier
    {
        public const string ClassId = "6BB2DA61-9C95-41A6-9F2D-35C3335D0F22";

        public int GetOverlayInfo(StringBuilder iconFile, int cchMax, out int iconIndex, out uint flags)
        {
            iconIndex = 0;
            flags = (uint)(ShellIconOverlayFlags.IconFile | ShellIconOverlayFlags.IconIndex);
            try
            {
                var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var iconPath = Path.GetFullPath(Path.Combine(assemblyFolder, "..", "Assets", "ProtectedApp.ico"));
                if (!File.Exists(iconPath) || cchMax <= iconPath.Length) return HResult.FileNotFound;
                iconFile.Clear();
                iconFile.Append(iconPath);
                return HResult.Ok;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }

        public int GetPriority(out int priority)
        {
            priority = 0;
            return HResult.Ok;
        }

        public int IsMemberOf(string path, uint attributes) =>
            Directory.Exists(path) && FolderMenuState.IsProtected(path) ? HResult.Ok : HResult.False;
    }

    [Flags]
    internal enum ShellIconOverlayFlags : uint
    {
        IconFile = 0x1,
        IconIndex = 0x2
    }

    [ComVisible(true)]
    [Guid("0C6C4200-C589-11D0-999A-00C04FD655E1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellIconOverlayIdentifier
    {
        [PreserveSig] int GetOverlayInfo([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconFile,
            int cchMax, out int iconIndex, out uint flags);
        [PreserveSig] int GetPriority(out int priority);
        [PreserveSig] int IsMemberOf([MarshalAs(UnmanagedType.LPWStr)] string path, uint attributes);
    }
}
