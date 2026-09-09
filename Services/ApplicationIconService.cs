using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ProtectedApp.Services;

internal static class ApplicationIconService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProtectedApp",
        "IconCache");

    public static byte[]? GetIconPng(string resourcePath, int resourceIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || !File.Exists(resourcePath)) return null;

        try
        {
            var fullPath = Path.GetFullPath(resourcePath);
            var cachePath = GetCachePath(fullPath, resourceIndex);
            if (File.Exists(cachePath)) return File.ReadAllBytes(cachePath);

            var png = ExtractIconPng(fullPath, resourceIndex) ?? ExtractAssociatedIconPng(fullPath);
            if (png is null) return null;

            try
            {
                Directory.CreateDirectory(CacheDirectory);
                File.WriteAllBytes(cachePath, png);
            }
            catch
            {
                // A cache failure must never prevent the application list from loading.
            }

            return png;
        }
        catch
        {
            return null;
        }
    }

    private static string GetCachePath(string resourcePath, int resourceIndex)
    {
        var version = File.GetLastWriteTimeUtc(resourcePath).Ticks;
        var identity = $"{resourcePath.ToUpperInvariant()}|{resourceIndex}|{version}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(CacheDirectory, $"{hash}.png");
    }

    private static byte[]? ExtractIconPng(string resourcePath, int resourceIndex)
    {
        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        var extracted = ExtractIconEx(resourcePath, resourceIndex, largeIcons, smallIcons, 1);
        var handle = largeIcons[0] != IntPtr.Zero ? largeIcons[0] : smallIcons[0];

        try
        {
            if (extracted == 0 || handle == IntPtr.Zero) return null;
            using var icon = (Icon)Icon.FromHandle(handle).Clone();
            using var bitmap = icon.ToBitmap();
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        finally
        {
            if (largeIcons[0] != IntPtr.Zero) DestroyIcon(largeIcons[0]);
            if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != largeIcons[0]) DestroyIcon(smallIcons[0]);
        }
    }

    private static byte[]? ExtractAssociatedIconPng(string path)
    {
        var result = SHGetFileInfo(
            path,
            0,
            out var fileInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellFileInfoIcon | ShellFileInfoLargeIcon);

        try
        {
            if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero) return null;
            using var icon = (Icon)Icon.FromHandle(fileInfo.IconHandle).Clone();
            using var bitmap = icon.ToBitmap();
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        finally
        {
            if (fileInfo.IconHandle != IntPtr.Zero) DestroyIcon(fileInfo.IconHandle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        IntPtr[] largeIcons,
        IntPtr[] smallIcons,
        uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    private const uint ShellFileInfoIcon = 0x00000100;
    private const uint ShellFileInfoLargeIcon = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
