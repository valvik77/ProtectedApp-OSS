using Microsoft.Win32;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

public static class InstalledAppsService
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public static Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync() =>
        Task.Run<IReadOnlyList<InstalledApplication>>(Enumerate);

    private static IReadOnlyList<InstalledApplication> Enumerate()
    {
        var applications = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        AddRegistryApplications(Registry.CurrentUser, applications);
        AddRegistryApplications(Registry.LocalMachine, applications);
        return applications.Values
            .Where(a => !PathsEqual(a.Path, Environment.ProcessPath))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void AddRegistryApplications(RegistryKey hive, Dictionary<string, InstalledApplication> applications)
    {
        foreach (var keyPath in UninstallKeys)
        {
            using var uninstall = hive.OpenSubKey(keyPath, false);
            if (uninstall is null) continue;
            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(subKeyName, false);
                    if (entry is null || Convert.ToInt32(entry.GetValue("SystemComponent", 0)) == 1) continue;
                    var name = entry.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var publisher = entry.GetValue("Publisher") as string ?? string.Empty;
                    var iconReference = ParseDisplayIcon(entry.GetValue("DisplayIcon") as string);
                    var executable = ResolveExecutableFromDisplayIcon(iconReference)
                                     ?? ResolveFromInstallLocation(entry.GetValue("InstallLocation") as string, name);
                    if (!IsUsableExecutable(executable)) continue;
                    if (!applications.ContainsKey(executable!))
                    {
                        var iconPng = iconReference is not null
                            ? ApplicationIconService.GetIconPng(iconReference.Path, iconReference.Index)
                            : null;
                        iconPng ??= ApplicationIconService.GetIconPng(executable!);
                        applications[executable!] = new InstalledApplication
                        {
                            Name = name.Trim(),
                            Path = executable!,
                            Publisher = publisher.Trim(),
                            Source = "Programas instalados",
                            IconPng = iconPng
                        };
                    }
                }
                catch { }
            }
        }
    }

    private static IconReference? ParseDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        string path;
        var index = 0;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote <= 1) return null;
            path = expanded[1..closingQuote];
            var suffix = expanded[(closingQuote + 1)..].Trim();
            if (suffix.StartsWith(',') && int.TryParse(suffix[1..].Trim(), out var parsedIndex)) index = parsedIndex;
        }
        else
        {
            var comma = expanded.LastIndexOf(',');
            if (comma > 0 && int.TryParse(expanded[(comma + 1)..].Trim(), out var parsedIndex))
            {
                path = expanded[..comma];
                index = parsedIndex;
            }
            else path = expanded;
        }

        path = path.Trim(' ', '"');
        return File.Exists(path) ? new IconReference(path, index) : null;
    }

    private static string? ResolveExecutableFromDisplayIcon(IconReference? iconReference) =>
        iconReference is not null && IsUsableExecutable(iconReference.Path) ? iconReference.Path : null;

    private static string? ResolveFromInstallLocation(string? installLocation, string displayName)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation)) return null;
        try
        {
            var normalizedName = new string(displayName.Where(char.IsLetterOrDigit).ToArray());
            return Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !IsHelperExecutable(path))
                .OrderByDescending(path => NameScore(Path.GetFileNameWithoutExtension(path), normalizedName))
                .ThenByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static int NameScore(string executableName, string displayName)
    {
        var normalized = new string(executableName.Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Equals(displayName, StringComparison.OrdinalIgnoreCase)) return 3;
        if (displayName.Contains(normalized, StringComparison.OrdinalIgnoreCase) || normalized.Contains(displayName, StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    private static bool IsUsableExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !IsHelperExecutable(path);

    private static bool IsHelperExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.Contains("unins", StringComparison.OrdinalIgnoreCase)
            || name.Equals("setup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("update", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("service", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record IconReference(string Path, int Index);
}
