using Microsoft.Win32;
using System.Diagnostics;

namespace ProtectedApp.Service;

/// <summary>
/// Avoids a known incompatibility between Directory Opus with Page Heap enabled
/// and file systems provided by Dokany. Page Heap is a diagnostic option, not a
/// protection feature, and causes Windows to terminate Opus on verifier stops.
/// </summary>
internal static class DirectoryOpusCompatibility
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\dopus.exe";
    private const int PageHeapGlobalFlag = 0x02000000;

    public static void DisablePageHeapIfInstalled()
    {
        bool installed;
        try
        {
            installed = IsInstalled();
        }
        catch (Exception ex)
        {
            WriteFailureEvent("No se pudo detectar Directory Opus para aplicar la mitigación Page Heap.", ex);
            return;
        }
        if (!installed) return;
        foreach (var view in Views())
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(IfeoPath, writable: true);
                RemovePageHeapValues(isDirectoryOpusInstalled: true, [key]);
            }
            catch (Exception ex)
            {
                WriteFailureEvent($"No se pudo aplicar la mitigación Page Heap de Directory Opus ({view}).", ex);
            }
        }
    }

    private static void WriteFailureEvent(string message, Exception exception)
    {
        try { EventLog.WriteEntry(GuardianConstants.EventSource, $"{message} {exception.Message}", EventLogEntryType.Warning, 1003); }
        catch { }
    }

    internal static bool RemovePageHeapValues(bool isDirectoryOpusInstalled, IEnumerable<RegistryKey?> keys)
    {
        if (!isDirectoryOpusInstalled) return false;
        var changed = false;
        foreach (var key in keys)
        {
            if (key is null) continue;
            if (key.GetValue("GlobalFlag") is int globalFlag && (globalFlag & PageHeapGlobalFlag) != 0)
            {
                var remainingFlags = globalFlag & ~PageHeapGlobalFlag;
                if (remainingFlags == 0) key.DeleteValue("GlobalFlag", throwOnMissingValue: false);
                else key.SetValue("GlobalFlag", remainingFlags, RegistryValueKind.DWord);
                changed = true;
            }
            if (key.GetValue("PageHeapFlags") is not null)
            {
                key.DeleteValue("PageHeapFlags", throwOnMissingValue: false);
                changed = true;
            }
        }
        return changed;
    }

    private static bool IsInstalled()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GPSoftware", "Directory Opus", "dopus.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GPSoftware", "Directory Opus", "dopus.exe")
        };
        if (candidates.Any(File.Exists)) return true;

        // Directory Opus can be installed outside Program Files. App Paths is
        // the standard registration used by Windows for that case.
        foreach (var view in Views())
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var appPath = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\dopus.exe");
                if (appPath?.GetValue(null) is string executable && File.Exists(executable.Trim('"')))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static RegistryView[] Views() => Environment.Is64BitOperatingSystem
        ? [RegistryView.Registry64, RegistryView.Registry32]
        : [RegistryView.Registry32];
}
