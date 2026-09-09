using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

public static class FolderIconService
{
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcneAttributes = 0x00000800;
    private const uint ShcneUpdateDir = 0x00001000;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;

    public static bool TryApply(ProtectedFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path)) return false;

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ProtectedFolderIcon.ico");
            if (!File.Exists(iconPath)) return false;

            var desktopIniPath = Path.Combine(folder.Path, "desktop.ini");
            if (!folder.HasFolderIconBackup)
            {
                folder.DesktopIniExistedBeforeProtection = File.Exists(desktopIniPath);
                folder.DesktopIniBackupBase64 = folder.DesktopIniExistedBeforeProtection
                    ? Convert.ToBase64String(File.ReadAllBytes(desktopIniPath))
                    : null;
                folder.DesktopIniAttributesBeforeProtection = folder.DesktopIniExistedBeforeProtection
                    ? (int)File.GetAttributes(desktopIniPath)
                    : 0;
                folder.FolderAttributesBeforeProtection = (int)File.GetAttributes(folder.Path);
                folder.HasFolderIconBackup = true;
            }

            Encoding? encoding = null;
            var existing = File.Exists(desktopIniPath) ? ReadText(desktopIniPath, out encoding) : string.Empty;
            encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var iconResource = iconPath.Replace("\"", string.Empty);
            var content = UpsertIconResource(existing, iconResource);
            var desktopIniExists = File.Exists(desktopIniPath);
            var iconChanged = !string.Equals(existing, content, StringComparison.Ordinal);
            if (iconChanged)
            {
                var attributes = desktopIniExists ? File.GetAttributes(desktopIniPath) : FileAttributes.Normal;
                if (desktopIniExists)
                    File.SetAttributes(desktopIniPath, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
                File.WriteAllText(desktopIniPath, content, encoding);
                File.SetAttributes(desktopIniPath, attributes | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            }

            var folderAttributes = File.GetAttributes(folder.Path);
            var folderChanged = !folderAttributes.HasFlag(FileAttributes.System);
            if (folderChanged)
                File.SetAttributes(folder.Path, folderAttributes | FileAttributes.System);
            if (iconChanged || folderChanged)
                RefreshShell(folder.Path);
            return true;
        }
        catch
        {
            // Guardian may already have updated desktop.ini as SYSTEM and
            // denied this user access to the folder. Still refresh file
            // managers so they reload the icon Guardian just wrote.
            NotifyIconChanged(folder.Path);
            return false;
        }
    }

    public static bool TryRestore(ProtectedFolder folder)
    {
        if (!folder.HasFolderIconBackup || string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path)) return true;

        try
        {
            var desktopIniPath = Path.Combine(folder.Path, "desktop.ini");
            if (folder.DesktopIniExistedBeforeProtection)
            {
                var original = string.IsNullOrWhiteSpace(folder.DesktopIniBackupBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(folder.DesktopIniBackupBase64);
                File.SetAttributes(desktopIniPath, File.GetAttributes(desktopIniPath)
                    & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
                File.WriteAllBytes(desktopIniPath, original);
                File.SetAttributes(desktopIniPath, (FileAttributes)folder.DesktopIniAttributesBeforeProtection);
            }
            else if (File.Exists(desktopIniPath))
            {
                File.SetAttributes(desktopIniPath, FileAttributes.Normal);
                File.Delete(desktopIniPath);
            }

            File.SetAttributes(folder.Path, (FileAttributes)folder.FolderAttributesBeforeProtection);
            folder.HasFolderIconBackup = false;
            folder.DesktopIniExistedBeforeProtection = false;
            folder.DesktopIniBackupBase64 = null;
            folder.DesktopIniAttributesBeforeProtection = 0;
            folder.FolderAttributesBeforeProtection = 0;
            RefreshShell(folder.Path);
            return true;
        }
        catch
        {
            NotifyIconChanged(folder.Path);
            return false;
        }
    }

    public static void RefreshShellIcon(ProtectedFolder folder)
    {
        NotifyIconChanged(folder.Path);
    }

    public static void NotifyIconChanged(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { RefreshShell(path); }
        catch { }
    }

    private static string ReadText(string path, out Encoding? encoding)
    {
        var bytes = File.ReadAllBytes(path);
        encoding = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
            ? Encoding.Unicode
            : bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
                ? Encoding.BigEndianUnicode
                : bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                    : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return encoding.GetString(bytes);
    }

    private static string UpsertIconResource(string existing, string iconResource)
    {
        const string iconResourcePattern = @"(?im)^\s*IconResource\s*=.*$";
        var replacement = "IconResource=" + iconResource + ",0";
        if (Regex.IsMatch(existing, iconResourcePattern))
            return Regex.Replace(existing, iconResourcePattern, replacement);

        return existing.TrimEnd() + Environment.NewLine + "[.ShellClassInfo]" + Environment.NewLine
            + replacement + Environment.NewLine;
    }

    private static void RefreshShell(string path)
    {
        // Run from the interactive session and wait until the shell receives
        // each change. Combining FLUSH and FLUSHNOWAIT (0x3000) is invalid
        // and allowed file managers to keep the previous image cached.
        var pathFlags = ShcnfPathW | ShcnfFlush;
        var desktopIniPath = Path.Combine(path, "desktop.ini");
        SHChangeNotify(ShcneUpdateItem, pathFlags, desktopIniPath, IntPtr.Zero);
        SHChangeNotify(ShcneAttributes, pathFlags, path, IntPtr.Zero);
        SHChangeNotify(ShcneUpdateItem, pathFlags, path, IntPtr.Zero);

        var parentPath = Directory.GetParent(path)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentPath))
            SHChangeNotify(ShcneUpdateDir, pathFlags, parentPath, IntPtr.Zero);

        // File managers cache desktop.ini customisations independently. An
        // association refresh makes Explorer discard that cached folder icon.
        SHChangeNotify(ShcneAssocChanged, ShcnfFlush, IntPtr.Zero, IntPtr.Zero);

        RefreshDirectoryOpus();
    }

    private static void RefreshDirectoryOpus()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "GPSoftware", "Directory Opus", "dopusrt.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "GPSoftware", "Directory Opus", "dopusrt.exe")
            };
            var runtime = candidates.FirstOrDefault(File.Exists);
            if (runtime is null) return;

            using var process = Process.Start(new ProcessStartInfo(runtime)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // DOpusRT expects the command and its arguments after /acmd.
                // Supplying the whole text as one argument makes it try to
                // open "Go REFRESH=all" as a file.
                ArgumentList = { "/acmd", "Go", "REFRESH=all", "REFRESHTHUMBS" }
            });
        }
        catch
        {
            // Directory Opus is optional; a failed refresh must never affect
            // the folder protection or the normal Windows shell refresh.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
