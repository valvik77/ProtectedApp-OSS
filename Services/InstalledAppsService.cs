using Microsoft.Win32;
using ProtectedApp.Models;
using System.Runtime.InteropServices;

namespace ProtectedApp.Services;

public static class InstalledAppsService
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    public static Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync() =>
        Task.Run<IReadOnlyList<InstalledApplication>>(() =>
        {
            // Discovery is a convenience feature. A stale Start Menu link or
            // a directory ACL must never be able to terminate the UI.
            try { return Enumerate(); }
            catch { return []; }
        });

    private static IReadOnlyList<InstalledApplication> Enumerate()
    {
        var applications = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        AddRegistryApplications(Registry.CurrentUser, applications);
        AddRegistryApplications(Registry.LocalMachine, applications);
        AddAppPathApplications(Registry.CurrentUser, applications);
        AddAppPathApplications(Registry.LocalMachine, applications);
        AddStartMenuApplications(applications);
        return applications.Values
            .Where(a => !PathsEqual(a.Path, Environment.ProcessPath))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void AddAppPathApplications(RegistryKey hive,
        Dictionary<string, InstalledApplication> applications)
    {
        try
        {
            using var appPaths = hive.OpenSubKey(AppPathsKey, false);
            if (appPaths is null) return;
            foreach (var subKeyName in appPaths.GetSubKeyNames())
            {
                using var entry = appPaths.OpenSubKey(subKeyName, false);
                var executable = entry?.GetValue(null) as string;
                if (!IsUsableExecutable(executable) || applications.ContainsKey(executable!)) continue;
                applications[executable!] = CreateApplication(
                    Path.GetFileNameWithoutExtension(executable!), executable!, "Registro de aplicaciones");
            }
        }
        catch { }
    }

    private static void AddStartMenuApplications(Dictionary<string, InstalledApplication> applications)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
         .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            try
            {
                // Enumeration is lazy: UnauthorizedAccessException can occur
                // while advancing to a child directory rather than when the
                // IEnumerable is created. Keep the entire iteration inside
                // the guarded block.
                foreach (var shortcut in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        var executable = ResolveShortcutTarget(shortcut);
                        if (!IsUsableExecutable(executable) || applications.ContainsKey(executable!)) continue;
                        applications[executable!] = CreateApplication(
                            Path.GetFileNameWithoutExtension(shortcut), executable!, "Menú Inicio");
                    }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            shortcut = shell.GetType().InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            return shortcut?.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
        }
        catch { return null; }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
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
            || name.EndsWith("service", StringComparison.OrdinalIgnoreCase)
            // Dokan is ProtectedApp's virtual-vault runtime. Its command-line
            // tools are not user applications and protecting them could stop
            // vault mounting altogether.
            || name.StartsWith("dokan", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
                StringComparison.OrdinalIgnoreCase);
    }

    private static InstalledApplication CreateApplication(string name, string executable, string source) => new()
    {
        Name = name.Trim(),
        Path = executable,
        Source = source,
        IconPng = ApplicationIconService.GetIconPng(executable)
    };

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record IconReference(string Path, int Index);
}
