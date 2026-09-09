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
    public sealed class FolderExplorerCommand : IExplorerCommand
    {
        public const string ClassId = "6DCC2C90-2C9F-4C38-9075-7F2B1391C9B2";
        private static readonly Guid CanonicalName = new Guid("9530B2D5-7235-4737-96BB-A44870144D58");

        public int GetTitle(IShellItemArray selection, out IntPtr title)
        {
            title = IntPtr.Zero;
            try
            {
                var path = GetSingleFolderPath(selection);
                var action = path == null ? FolderMenuAction.Protect : FolderMenuState.Resolve(path);
                title = Marshal.StringToCoTaskMemUni(FolderMenuState.GetTitle(action));
                return HResult.Ok;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }

        public int GetIcon(IShellItemArray selection, out IntPtr icon)
        {
            icon = IntPtr.Zero;
            try
            {
                var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var iconPath = Path.GetFullPath(Path.Combine(assemblyFolder, "..", "Assets", "ProtectedApp.ico"));
                if (!File.Exists(iconPath)) return HResult.FileNotFound;
                // IExplorerCommand expects the standard icon-resource string
                // (path plus resource index), even when the source is an .ico.
                icon = Marshal.StringToCoTaskMemUni(iconPath + ",0");
                return HResult.Ok;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }

        public int GetToolTip(IShellItemArray selection, out IntPtr toolTip)
        {
            toolTip = IntPtr.Zero;
            try
            {
                var path = GetSingleFolderPath(selection);
                var action = path == null ? FolderMenuAction.Protect : FolderMenuState.Resolve(path);
                toolTip = Marshal.StringToCoTaskMemUni(FolderMenuState.GetToolTip(action));
                return HResult.Ok;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }

        public int GetCanonicalName(out Guid commandName)
        {
            commandName = CanonicalName;
            return HResult.Ok;
        }

        public int GetState(IShellItemArray selection, bool allowSlowOperations, out uint state)
        {
            state = GetSingleFolderPath(selection) == null
                ? (uint)ExplorerCommandState.Hidden
                : (uint)ExplorerCommandState.Enabled;
            return HResult.Ok;
        }

        public int Invoke(IShellItemArray selection, IntPtr bindContext)
        {
            try
            {
                var path = GetSingleFolderPath(selection);
                if (path == null) return HResult.InvalidArgument;
                var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var applicationPath = Path.GetFullPath(Path.Combine(assemblyFolder, "..", "ProtectedApp.exe"));
                if (!File.Exists(applicationPath)) return HResult.FileNotFound;
                Process.Start(new ProcessStartInfo
                {
                    FileName = applicationPath,
                    Arguments = "--folder-action " + WindowsCommandLine.Quote(path),
                    WorkingDirectory = Path.GetDirectoryName(applicationPath),
                    UseShellExecute = true
                });
                return HResult.Ok;
            }
            catch (Exception exception) { return Marshal.GetHRForException(exception); }
        }

        public int GetFlags(out uint flags)
        {
            flags = (uint)ExplorerCommandFlags.Default;
            return HResult.Ok;
        }

        public int EnumSubCommands(out IntPtr commands)
        {
            commands = IntPtr.Zero;
            return HResult.NotImplemented;
        }

        private static string? GetSingleFolderPath(IShellItemArray? selection)
        {
            if (selection == null || selection.GetCount(out var count) != HResult.Ok || count != 1
                || selection.GetItemAt(0, out var item) != HResult.Ok || item == null) return null;
            if (item.GetDisplayName(ShellDisplayName.FileSystemPath, out var pointer) != HResult.Ok
                || pointer == IntPtr.Zero) return null;
            try
            {
                var path = Marshal.PtrToStringUni(pointer);
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }

    }

    internal static class HResult
    {
        public const int Ok = 0;
        public const int False = 1;
        public const int NotImplemented = unchecked((int)0x80004001);
        public const int InvalidArgument = unchecked((int)0x80070057);
        public const int FileNotFound = unchecked((int)0x80070002);
    }

    [Flags]
    public enum ExplorerCommandState : uint
    {
        Enabled = 0x0,
        Disabled = 0x1,
        Hidden = 0x2
    }

    [Flags]
    public enum ExplorerCommandFlags : uint
    {
        Default = 0x0
    }

    public enum ShellDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    [ComVisible(true)]
    [Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IShellItemArray selection, out IntPtr title);
        [PreserveSig] int GetIcon(IShellItemArray selection, out IntPtr icon);
        [PreserveSig] int GetToolTip(IShellItemArray selection, out IntPtr toolTip);
        [PreserveSig] int GetCanonicalName(out Guid commandName);
        [PreserveSig] int GetState(IShellItemArray selection, [MarshalAs(UnmanagedType.Bool)] bool allowSlowOperations, out uint state);
        [PreserveSig] int Invoke(IShellItemArray selection, IntPtr bindContext);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IntPtr commands);
    }

    [ComVisible(true)]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetPropertyStore(uint flags, ref Guid interfaceId, out IntPtr propertyStore);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr propertyKey, ref Guid interfaceId, out IntPtr propertyDescriptionList);
        [PreserveSig] int GetAttributes(uint flags, uint attributes, out uint resultAttributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
        [PreserveSig] int EnumItems(out IntPtr enumerator);
    }

    [ComVisible(true)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
        [PreserveSig] int GetDisplayName(ShellDisplayName displayName, out IntPtr name);
        [PreserveSig] int GetAttributes(uint attributes, out uint resultAttributes);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
    }
}
