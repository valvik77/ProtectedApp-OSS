using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using ProtectedApp.Shared;

namespace ProtectedApp.Service;

internal sealed class ExecutionGateManager(
    GuardianOptions options,
    ILogger<ExecutionGateManager> logger)
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string ManagedMarker = "ProtectedAppManaged";
    private const string OwnerMarker = "ProtectedAppUseFilterOwner";
    private const string PreviousUseFilter = "ProtectedAppPreviousUseFilter";
    private readonly object _sync = new();
    private readonly HashSet<string> _knownImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<RegistryView> _discoveredViews = [];
    private readonly HashSet<string> _observedPythonHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedPythonHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _applicationAuthorizations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _hostAuthorizations =
        new(StringComparer.OrdinalIgnoreCase);

    public string GatePath => Path.Combine(GuardianConstants.StateFolder, "ProtectedApp.Gate.exe");

    public void Synchronize(IReadOnlyList<GuardianPolicy> policies)
    {
        if (!options.DiagnosticMode && !File.Exists(GatePath)) return;
        var targets = policies.SelectMany(policy => policy.Rules)
            .Where(rule => rule.IsEnabled
                && Path.GetExtension(rule.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(rule.Path))
            .Select(rule => new GateTarget(Path.GetFullPath(rule.Path), IsHost: false))
            .ToList();
        foreach (var policy in policies.Where(policy => policy.Rules.Any(rule =>
                     rule.IsEnabled && Path.GetExtension(rule.Path).Equals(".py", StringComparison.OrdinalIgnoreCase))))
        {
            targets.AddRange(GetPythonHostsForUser(policy.UserSid).Select(path => new GateTarget(path, IsHost: true)));
            targets.AddRange(policy.Rules.Where(rule => rule.IsEnabled
                    && Path.GetExtension(rule.Path).Equals(".py", StringComparison.OrdinalIgnoreCase))
                .SelectMany(rule => DiscoverNearbyPythonHosts(rule.Path))
                .Select(path => new GateTarget(path, IsHost: true)));
        }
        lock (_sync)
            targets.AddRange(_observedPythonHosts.Select(path => new GateTarget(path, IsHost: true)));
        var distinctTargets = targets.GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(target => target.IsHost).First())
            .ToArray();

        lock (_sync)
        {
            _managedPythonHosts.Clear();
            foreach (var host in distinctTargets.Where(target => target.IsHost))
                _managedPythonHosts.Add(host.Path);
            if (options.DiagnosticMode) return;
            foreach (var view in Views())
            {
                try { SynchronizeView(view, distinctTargets); }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudo sincronizar la puerta preventiva IFEO ({View}).", view); }
            }
        }
    }

    public bool IsPythonHost(string userSid, string path)
    {
        var normalized = Path.GetFullPath(path);
        lock (_sync)
            if (_managedPythonHosts.Contains(normalized) || _observedPythonHosts.Contains(normalized)) return true;
        return GetPythonHostsForUser(userSid).Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public bool ObservePythonHost(string path)
    {
        string normalized;
        try { normalized = Path.GetFullPath(path); }
        catch { return false; }
        if (!File.Exists(normalized) || !ProtectedTarget.IsPotentialScriptHost(normalized)
            || !Path.GetFileName(normalized).StartsWith("py", StringComparison.OrdinalIgnoreCase)) return false;
        lock (_sync) return _observedPythonHosts.Add(normalized);
    }

    public string? FindPythonLauncher(string userSid) => GetPythonHostsForUser(userSid)
        .OrderBy(path => Path.GetFileName(path).StartsWith("py.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .FirstOrDefault();

    public string[] GetHostAuthorizationFamily(string userSid, string executable)
    {
        var normalized = Path.GetFullPath(executable);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalized };
        var directory = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                    if (ProtectedTarget.IsPotentialScriptHost(candidate)) hosts.Add(Path.GetFullPath(candidate));
            }
            catch { }
        }

        var fileName = Path.GetFileName(normalized);
        if (fileName.Equals("py.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("pyw.exe", StringComparison.OrdinalIgnoreCase))
            hosts.UnionWith(GetPythonHostsForUser(userSid));
        return hosts.Where(File.Exists).ToArray();
    }

    public void BeginApplicationAuthorization(string targetPath)
    {
        if (options.DiagnosticMode) return;
        var normalized = Path.GetFullPath(targetPath);
        lock (_sync)
        {
            _applicationAuthorizations[normalized] = DateTimeOffset.UtcNow.AddSeconds(8);
            SetTargetDebugger(normalized, enabled: false, isHost: false);
        }
    }

    public void CancelApplicationAuthorization(string targetPath)
    {
        if (options.DiagnosticMode) return;
        var normalized = Path.GetFullPath(targetPath);
        lock (_sync)
        {
            _applicationAuthorizations.Remove(normalized);
            SetTargetDebugger(normalized, enabled: true, isHost: false);
        }
    }

    public void BeginHostAuthorization(IEnumerable<string> hostPaths)
    {
        var normalizedHosts = hostPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_sync)
        {
            foreach (var host in normalizedHosts)
            {
                _hostAuthorizations[host] = DateTimeOffset.UtcNow.AddSeconds(8);
                if (!options.DiagnosticMode) SetTargetDebugger(host, enabled: false, isHost: true);
            }
        }
    }

    public void CancelHostAuthorization(IEnumerable<string> hostPaths)
    {
        lock (_sync)
        {
            foreach (var host in hostPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _hostAuthorizations.Remove(host);
                if (!options.DiagnosticMode) SetTargetDebugger(host, enabled: true, isHost: true);
            }
        }
    }

    public void ReconcileHostAuthorizations(IEnumerable<string> activeHostPaths)
    {
        var active = activeHostPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            foreach (var pair in _hostAuthorizations.ToArray())
            {
                if (active.Contains(pair.Key) || pair.Value > now) continue;
                _hostAuthorizations.Remove(pair.Key);
                if (!options.DiagnosticMode) SetTargetDebugger(pair.Key, enabled: true, isHost: true);
                logger.LogInformation("Puerta del intérprete restaurada al finalizar la ejecución autorizada: {HostPath}.", pair.Key);
            }
        }
    }

    internal bool IsHostAuthorizationActive(string hostPath)
    {
        lock (_sync) return _hostAuthorizations.ContainsKey(Path.GetFullPath(hostPath));
    }

    public void ReconcileApplicationAuthorizations(IEnumerable<string> activePaths)
    {
        if (options.DiagnosticMode) return;
        var active = activePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            foreach (var pair in _applicationAuthorizations.ToArray())
            {
                if (active.Contains(pair.Key) || pair.Value > now) continue;
                _applicationAuthorizations.Remove(pair.Key);
                SetTargetDebugger(pair.Key, enabled: true, isHost: false);
                logger.LogInformation("Puerta preventiva restaurada al cerrar la última instancia: {Target}.", pair.Key);
            }
        }
    }

    public T WithTemporaryBypass<T>(string targetPath, Func<T> action)
    {
        if (options.DiagnosticMode || !Path.GetExtension(targetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return action();

        lock (_sync)
        {
            var removed = new List<(RegistryView View, string Debugger)>();
            foreach (var view in Views())
            {
                try
                {
                    using var child = OpenManagedTarget(view, targetPath, writable: true);
                    var debugger = child?.GetValue("Debugger") as string;
                    if (child is null || string.IsNullOrWhiteSpace(debugger)) continue;
                    child.DeleteValue("Debugger", false);
                    removed.Add((view, debugger));
                }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudo abrir temporalmente la puerta para {Target}.", targetPath); }
            }

            try { return action(); }
            finally
            {
                foreach (var item in removed)
                {
                    try
                    {
                        using var child = OpenManagedTarget(item.View, targetPath, writable: true);
                        var normalized = Path.GetFullPath(targetPath);
                        if (!_applicationAuthorizations.ContainsKey(normalized)
                            && !_hostAuthorizations.ContainsKey(normalized))
                            child?.SetValue("Debugger", item.Debugger, RegistryValueKind.String);
                    }
                    catch (Exception ex) { logger.LogError(ex, "No se pudo restaurar la puerta preventiva para {Target}.", targetPath); }
                }
            }
        }
    }

    public static void RemoveAllManagedEntries()
    {
        foreach (var view in Views())
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = machine.OpenSubKey(IfeoPath, writable: true);
            if (root is null) continue;
            foreach (var imageName in root.GetSubKeyNames()) CleanupImage(root, imageName, []);
        }
    }

    private void SynchronizeView(RegistryView view, IReadOnlyList<GateTarget> targets)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var root = machine.CreateSubKey(IfeoPath, writable: true);
        if (root is null) return;

        if (!_discoveredViews.Contains(view))
        {
            foreach (var imageName in root.GetSubKeyNames())
            {
                using var image = root.OpenSubKey(imageName);
                if (image?.GetSubKeyNames().Any(name => IsManaged(image, name)) == true) _knownImages.Add(imageName);
            }
        }

        foreach (var target in targets) _knownImages.Add(Path.GetFileName(target.Path));
        foreach (var imageName in _knownImages.ToArray())
        {
            var imageTargets = targets.Where(target => Path.GetFileName(target.Path).Equals(imageName, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(target => TargetKey(target.Path), StringComparer.OrdinalIgnoreCase);
            CleanupImage(root, imageName, imageTargets.Keys);
            if (imageTargets.Count == 0)
            {
                _knownImages.Remove(imageName);
                continue;
            }

            using var image = root.CreateSubKey(imageName, writable: true)!;
            if (image.GetValue(OwnerMarker) is null)
            {
                var previous = image.GetValue("UseFilter");
                image.SetValue(PreviousUseFilter, previous is int value ? value : -1, RegistryValueKind.DWord);
                image.SetValue(OwnerMarker, 1, RegistryValueKind.DWord);
            }
            image.SetValue("UseFilter", 1, RegistryValueKind.DWord);

            foreach (var (key, target) in imageTargets)
            {
                using var child = image.CreateSubKey(key, writable: true)!;
                child.SetValue(ManagedMarker, 1, RegistryValueKind.DWord);
                child.SetValue("FilterFullPath", target.Path, RegistryValueKind.String);
                var mode = target.IsHost ? "--host" : "--target";
                var authorized = target.IsHost
                    ? _hostAuthorizations.ContainsKey(target.Path)
                    : _applicationAuthorizations.ContainsKey(target.Path);
                if (authorized)
                    child.DeleteValue("Debugger", false);
                else
                    child.SetValue("Debugger", $"\"{GatePath}\" {mode} \"{target.Path}\"", RegistryValueKind.String);
            }
        }
        _discoveredViews.Add(view);
    }

    private RegistryKey? OpenManagedTarget(RegistryView view, string targetPath, bool writable)
    {
        var imageName = Path.GetFileName(targetPath);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var root = machine.OpenSubKey(IfeoPath, writable);
        using var image = root?.OpenSubKey(imageName, writable);
        var child = image?.OpenSubKey(TargetKey(Path.GetFullPath(targetPath)), writable);
        if (child?.GetValue(ManagedMarker) is int marker && marker == 1) return child;
        child?.Dispose();
        return null;
    }

    private void SetTargetDebugger(string targetPath, bool enabled, bool isHost)
    {
        foreach (var view in Views())
        {
            try
            {
                using var child = OpenManagedTarget(view, targetPath, writable: true);
                if (child is null) continue;
                if (enabled)
                {
                    var mode = isHost ? "--host" : "--target";
                    child.SetValue("Debugger", $"\"{GatePath}\" {mode} \"{targetPath}\"", RegistryValueKind.String);
                }
                else
                    child.DeleteValue("Debugger", false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo cambiar el estado de la puerta para {Target}.", targetPath);
            }
        }
    }

    private static void CleanupImage(RegistryKey root, string imageName, IEnumerable<string> desiredKeys)
    {
        var desired = desiredKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var image = root.OpenSubKey(imageName, writable: true);
        if (image is null) return;
        foreach (var childName in image.GetSubKeyNames())
        {
            if (!desired.Contains(childName) && IsManaged(image, childName)) image.DeleteSubKeyTree(childName, false);
        }
        if (image.GetSubKeyNames().Any(name => IsManaged(image, name))) return;
        if (image.GetValue(OwnerMarker) is int owner && owner == 1)
        {
            var previous = image.GetValue(PreviousUseFilter);
            var anotherFilterExists = image.GetSubKeyNames().Any(name =>
            {
                using var child = image.OpenSubKey(name);
                return child?.GetValue("FilterFullPath") is string;
            });
            if (anotherFilterExists) image.SetValue("UseFilter", 1, RegistryValueKind.DWord);
            else if (previous is int value && value >= 0) image.SetValue("UseFilter", value, RegistryValueKind.DWord);
            else image.DeleteValue("UseFilter", false);
            image.DeleteValue(OwnerMarker, false);
            image.DeleteValue(PreviousUseFilter, false);
        }
    }

    private static bool IsManaged(RegistryKey image, string childName)
    {
        using var child = image.OpenSubKey(childName);
        return child?.GetValue(ManagedMarker) is int marker && marker == 1;
    }

    private static string TargetKey(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return "ProtectedApp_" + Convert.ToHexString(bytes.AsSpan(0, 12));
    }

    private static string[] GetPythonHostsForUser(string userSid)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
            catch { return; }
            if (File.Exists(path)) candidates.Add(path);
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Add(Path.Combine(windows, "py.exe"));
        Add(Path.Combine(windows, "pyw.exe"));

        string? profile = null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{userSid}");
            profile = key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (profile is not null) profile = Environment.ExpandEnvironmentVariables(profile);
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            var programs = Path.Combine(profile, "AppData", "Local", "Programs", "Python");
            Add(Path.Combine(programs, "Launcher", "py.exe"));
            Add(Path.Combine(programs, "Launcher", "pyw.exe"));
            try
            {
                if (Directory.Exists(programs))
                    foreach (var directory in Directory.EnumerateDirectories(programs, "Python*"))
                    {
                        Add(Path.Combine(directory, "python.exe"));
                        Add(Path.Combine(directory, "pythonw.exe"));
                    }
            }
            catch { }
        }

        return candidates.ToArray();
    }

    private static IEnumerable<string> DiscoverNearbyPythonHosts(string scriptPath)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo? current;
        try { current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(scriptPath))!); }
        catch { yield break; }

        for (var depth = 0; current is not null && depth < 7; depth++, current = current.Parent)
        {
            foreach (var name in new[] { "python.exe", "pythonw.exe", "py.exe", "pyw.exe" })
            {
                var direct = Path.Combine(current.FullName, name);
                if (File.Exists(direct)) found.Add(direct);
            }
            try
            {
                foreach (var directory in current.EnumerateDirectories().Where(directory =>
                             directory.Name.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                             || directory.Name.Equals("venv", StringComparison.OrdinalIgnoreCase)
                             || directory.Name.Equals(".venv", StringComparison.OrdinalIgnoreCase)
                             || directory.Name.Equals("env", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var relative in new[]
                             { "python.exe", "pythonw.exe", @"Scripts\python.exe", @"Scripts\pythonw.exe" })
                    {
                        var candidate = Path.Combine(directory.FullName, relative);
                        if (File.Exists(candidate)) found.Add(candidate);
                    }
                }
            }
            catch { }
        }
        foreach (var path in found) yield return path;
    }

    private sealed record GateTarget(string Path, bool IsHost);

    private static RegistryView[] Views() => Environment.Is64BitOperatingSystem
        ? [RegistryView.Registry64, RegistryView.Registry32]
        : [RegistryView.Registry32];
}
