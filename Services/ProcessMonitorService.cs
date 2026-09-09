using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using ProtectedApp.Models;
using ProtectedApp.Shared;

namespace ProtectedApp.Services;

public sealed class ProcessMonitorService : IDisposable
{
    private readonly Func<IReadOnlyList<ProtectedApplication>> _getApplications;
    private readonly ConcurrentDictionary<int, AllowedProcess> _allowedProcesses = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _launchAllowances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrustedAuthorization> _trustedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _timedSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ProcessCommandLine> _commandLines = new();
    private readonly ConcurrentDictionary<int, byte> _eventChecks = new();
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private ManagementEventWatcher? _processStartWatcher;

    public int IntervalMilliseconds { get; set; } = 700;
    public bool IsPaused { get; set; }
    public event Action<ProtectedApplication>? AccessRequired;
    public event Action<ProtectedApplication, string>? MonitorEvent;

    public ProcessMonitorService(Func<IReadOnlyList<ProtectedApplication>> getApplications) =>
        _getApplications = getApplications;

    public void Start()
    {
        if (_monitorTask is not null) return;
        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        try
        {
            _processStartWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT ProcessID, ProcessName FROM Win32_ProcessStartTrace"));
            _processStartWatcher.EventArrived += ProcessStartEventArrived;
            _processStartWatcher.Start();
        }
        catch
        {
            _processStartWatcher?.Dispose();
            _processStartWatcher = null;
        }
    }

    public void PrepareAuthorizedLaunch(ProtectedApplication app)
    {
        var key = Normalize(app.Path);
        _launchAllowances[key] = DateTimeOffset.UtcNow.AddSeconds(8);
        if (app.UnlockGraceMinutes > 0)
        {
            var grantedUtc = DateTimeOffset.UtcNow;
            _trustedUntil[key] = new TrustedAuthorization(grantedUtc,
                grantedUtc.AddMinutes(app.UnlockGraceMinutes), app.UnlockGraceMinutes,
                app.PasswordHash, app.PasswordSalt);
        }
        else
            _trustedUntil.TryRemove(key, out _);
        if (app.ForceCloseAfterMinutes > 0)
            _timedSessions[key] = DateTimeOffset.UtcNow;
        else
            _timedSessions.TryRemove(key, out _);
    }

    public void CancelAuthorizedLaunch(ProtectedApplication app)
    {
        var key = Normalize(app.Path);
        _launchAllowances.TryRemove(key, out _);
        _trustedUntil.TryRemove(key, out _);
        _timedSessions.TryRemove(key, out _);
    }

    public void AllowProcess(Process process, string protectedPath)
    {
        RememberAllowedProcess(process, protectedPath);
    }

    public void Resolve(ProtectedApplication app) => _pendingPaths.TryRemove(Normalize(app.Path), out _);

    public void RevokeAllAuthorizations()
    {
        ClearAuthorizations();
        IsPaused = false;
    }

    public int RevokeAllAuthorizationsAndTerminate()
    {
        IsPaused = true;
        ClearAuthorizations();
        var terminated = 0;
        try
        {
            var (executableRules, scriptRules) = GetRules();
            var now = DateTimeOffset.UtcNow;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        var path = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        var protectedExecutable = executableRules.ContainsKey(Normalize(path));
                        var protectedScript = false;
                        if (!protectedExecutable
                            && scriptRules.Length > 0
                            && ProtectedTarget.IsPotentialScriptHost(path)
                            && TryGetProcessCommandLine(process, now, out var commandLine)
                            && !string.IsNullOrWhiteSpace(commandLine))
                        {
                            protectedScript = scriptRules.Any(rule =>
                                ProtectedTarget.CommandLineReferences(commandLine, rule.Path));
                        }
                        if (!protectedExecutable && !protectedScript) continue;
                        process.Kill(entireProcessTree: true);
                        terminated++;
                    }
                    catch { }
                }
            }
        }
        finally { IsPaused = false; }
        return terminated;
    }

    public int RevokeAuthorizationAndTerminate(ProtectedApplication app)
    {
        var key = Normalize(app.Path);
        _launchAllowances.TryRemove(key, out _);
        _trustedUntil.TryRemove(key, out _);
        _timedSessions.TryRemove(key, out _);
        _pendingPaths.TryRemove(key, out _);

        var wasPaused = IsPaused;
        IsPaused = true;
        var terminatedProcessIds = new HashSet<int>();
        try
        {
            foreach (var allowed in _allowedProcesses.ToArray())
            {
                if (!string.Equals(allowed.Value.ProtectedPath, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                TryTerminate(allowed.Key, terminatedProcessIds);
                _allowedProcesses.TryRemove(allowed.Key, out _);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || terminatedProcessIds.Contains(process.Id)) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        var path = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        var matches = !ProtectedTarget.IsScript(app.Path)
                            ? string.Equals(Normalize(path), key, StringComparison.OrdinalIgnoreCase)
                            : ProtectedTarget.IsPotentialScriptHost(path)
                                && TryGetProcessCommandLine(process, now, out var commandLine)
                                && ProtectedTarget.CommandLineReferences(commandLine, app.Path);
                        if (matches) TryTerminate(process.Id, terminatedProcessIds);
                    }
                    catch { }
                }
            }
        }
        finally { IsPaused = wasPaused; }
        return terminatedProcessIds.Count;
    }

    private static void TryTerminate(int processId, ISet<int> terminatedProcessIds)
    {
        if (terminatedProcessIds.Contains(processId)) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            terminatedProcessIds.Add(processId);
        }
        catch { }
    }

    private void ClearAuthorizations()
    {
        _allowedProcesses.Clear();
        _launchAllowances.Clear();
        _trustedUntil.Clear();
        _timedSessions.Clear();
        _pendingPaths.Clear();
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsPaused) Scan();
                await Task.Delay(Math.Clamp(IntervalMilliseconds, 100, 5000), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { await Task.Delay(1000, token); }
        }
    }

    private void Scan()
    {
        var now = DateTimeOffset.UtcNow;
        EnforceTimedSessions(now);
        var (executableRules, scriptRules) = GetRules();
        if (executableRules.Count == 0 && scriptRules.Length == 0) return;

        foreach (var allowance in _launchAllowances)
            if (allowance.Value <= now) _launchAllowances.TryRemove(allowance.Key, out _);
        foreach (var trust in _trustedUntil)
            if (trust.Value.ExpiresUtc <= now) _trustedUntil.TryRemove(trust.Key, out _);
        foreach (var commandLine in _commandLines)
            if (now - commandLine.Value.LastSeenUtc > TimeSpan.FromMinutes(1))
                _commandLines.TryRemove(commandLine.Key, out _);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                InspectProcess(process, executableRules, scriptRules, now);
            }
        }
    }

    private void ProcessStartEventArrived(object sender, EventArrivedEventArgs e)
    {
        if (IsPaused) return;
        var processName = Convert.ToString(e.NewEvent.Properties["ProcessName"].Value);
        if (!MayProtectProcessName(processName)) return;
        var processId = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
        if (!_eventChecks.TryAdd(processId, 0)) return;
        var suspended = ProcessSuspender.TrySuspend(processId);
        _ = InspectStartedProcessAsync(processId, suspended);
    }

    private bool MayProtectProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        return _getApplications().Any(app => app.IsEnabled && File.Exists(app.Path)
            && (ProtectedTarget.IsScript(app.Path)
                ? ProtectedTarget.IsPotentialScriptHost(processName)
                : string.Equals(Path.GetFileName(app.Path), processName, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task InspectStartedProcessAsync(int processId, bool suspended)
    {
        try
        {
            foreach (var delay in new[] { 0, 5, 15, 35, 75 })
            {
                if (delay > 0) await Task.Delay(delay);
                try
                {
                    using var process = Process.GetProcessById(processId);
                    var (executableRules, scriptRules) = GetRules();
                    if (InspectProcess(process, executableRules, scriptRules, DateTimeOffset.UtcNow)) return;
                }
                catch (ArgumentException) { return; }
                catch { }
            }
        }
        finally
        {
            if (suspended) ProcessSuspender.TryResume(processId);
            _eventChecks.TryRemove(processId, out _);
        }
    }

    private (Dictionary<string, ProtectedApplication> Executables, ProtectedApplication[] Scripts) GetRules()
    {
        var enabled = _getApplications().Where(a => a.IsEnabled && File.Exists(a.Path)).ToArray();
        return (
            enabled.Where(a => !ProtectedTarget.IsScript(a.Path))
                .GroupBy(a => Normalize(a.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            enabled.Where(a => ProtectedTarget.IsScript(a.Path)).ToArray());
    }

    private bool InspectProcess(Process process,
        IReadOnlyDictionary<string, ProtectedApplication> executableRules,
        IReadOnlyList<ProtectedApplication> scriptRules,
        DateTimeOffset now)
    {
        if (process.Id == Environment.ProcessId) return true;
        string? path = null;
        try { path = process.MainModule?.FileName; } catch { }
        if (path is null) return false;

        ProtectedApplication? app = null;
        if (!executableRules.TryGetValue(Normalize(path), out app)
            && scriptRules.Count > 0
            && ProtectedTarget.IsPotentialScriptHost(path))
        {
            if (!TryGetProcessCommandLine(process, now, out var commandLine)) return false;
            app = scriptRules.FirstOrDefault(candidate =>
                ProtectedTarget.CommandLineReferences(commandLine, candidate.Path));
        }
        if (app is null) return true;

        var key = Normalize(app.Path);
        var schedule = ProtectionSchedule.GetDisposition(app.ScheduleEnabled, app.ScheduleDays,
            app.ScheduleStartMinutes, app.ScheduleEndMinutes, app.BlockOutsideSchedule,
            now.ToLocalTime());
        if (schedule == ScheduleDisposition.Allow) return true;
        if (schedule == ScheduleDisposition.Block)
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(2000);
                _allowedProcesses.TryRemove(process.Id, out _);
                _pendingPaths.TryRemove(key, out _);
                app.BlockCount++;
                MonitorEvent?.Invoke(app, "Ejecución bloqueada fuera del horario permitido");
            }
            catch
            {
                MonitorEvent?.Invoke(app, "No se pudo aplicar el bloqueo fuera del horario permitido");
            }
            return true;
        }
        if (IsAllowed(process, key) || HasActiveAuthorization(key)
            || (_launchAllowances.TryGetValue(key, out var expires) && expires > now)
            || HasTrustedAuthorization(key, now, app))
        {
            RememberAllowedProcess(process, key);
            return true;
        }

        // Siempre se cierra cada nueva instancia, aunque ya exista un diálogo pendiente.
        var firstRequest = _pendingPaths.TryAdd(key, 0);
        try
        {
            process.Kill(true);
            process.WaitForExit(2000);
            if (firstRequest)
            {
                app.BlockCount++;
                MonitorEvent?.Invoke(app, "Ejecución interceptada antes de mostrar su ventana");
                AccessRequired?.Invoke(app);
            }
        }
        catch
        {
            if (firstRequest) _pendingPaths.TryRemove(key, out _);
            MonitorEvent?.Invoke(app, "No se pudo cerrar el proceso; quizá requiere permisos de administrador");
        }
        return true;
    }

    private bool IsAllowed(Process process, string protectedPath)
    {
        if (!_allowedProcesses.TryGetValue(process.Id, out var allowed)) return false;
        try
        {
            if (string.Equals(allowed.ProtectedPath, protectedPath, StringComparison.OrdinalIgnoreCase)
                && process.StartTime.ToUniversalTime() == allowed.StartUtc)
                return true;
        }
        catch { }
        _allowedProcesses.TryRemove(process.Id, out _);
        return false;
    }

    private bool HasActiveAuthorization(string protectedPath)
    {
        foreach (var pair in _allowedProcesses)
        {
            if (!string.Equals(pair.Value.ProtectedPath, protectedPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var process = Process.GetProcessById(pair.Key);
                if (!process.HasExited && process.StartTime.ToUniversalTime() == pair.Value.StartUtc) return true;
            }
            catch { }
            _allowedProcesses.TryRemove(pair.Key, out _);
        }
        return false;
    }

    private void RememberAllowedProcess(Process process, string protectedPath)
    {
        try
        {
            _allowedProcesses[process.Id] = new AllowedProcess(
                Normalize(protectedPath), process.StartTime.ToUniversalTime());
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _allowedProcesses.TryRemove(process.Id, out _);
        }
        catch { }
    }

    private bool TryGetProcessCommandLine(Process process, DateTimeOffset now, out string? commandLine)
    {
        commandLine = null;
        try
        {
            var startUtc = process.StartTime.ToUniversalTime();
            if (_commandLines.TryGetValue(process.Id, out var cached) && cached.StartUtc == startUtc)
            {
                commandLine = cached.CommandLine;
                _commandLines[process.Id] = cached with { LastSeenUtc = now };
                return true;
            }
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                commandLine = Convert.ToString(item["CommandLine"]);
                if (string.IsNullOrWhiteSpace(commandLine)) return false;
                _commandLines[process.Id] = new ProcessCommandLine(commandLine, startUtc, now);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private void EnforceTimedSessions(DateTimeOffset now)
    {
        var applications = _getApplications();
        foreach (var pair in _timedSessions)
        {
            var app = applications.FirstOrDefault(candidate => candidate.IsEnabled
                && string.Equals(Normalize(candidate.Path), pair.Key, StringComparison.OrdinalIgnoreCase));
            if (app is null || app.ForceCloseAfterMinutes <= 0)
            {
                _timedSessions.TryRemove(pair.Key, out _);
                continue;
            }
            if (pair.Value.AddMinutes(app.ForceCloseAfterMinutes) > now) continue;
            if (!_timedSessions.TryRemove(pair.Key, out _)) continue;
            _trustedUntil.TryRemove(pair.Key, out _);
            _launchAllowances.TryRemove(pair.Key, out _);
            _pendingPaths.TryRemove(pair.Key, out _);
            foreach (var allowed in _allowedProcesses.ToArray())
            {
                if (!string.Equals(allowed.Value.ProtectedPath, pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var process = Process.GetProcessById(allowed.Key);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
                _allowedProcesses.TryRemove(allowed.Key, out _);
            }
            MonitorEvent?.Invoke(app, $"Tiempo de uso agotado ({app.ForceCloseAfterMinutes} min); aplicación cerrada y bloqueada");
        }
    }

    private bool HasTrustedAuthorization(string key, DateTimeOffset now, ProtectedApplication app)
    {
        if (app.UnlockGraceMinutes <= 0 || !_trustedUntil.TryGetValue(key, out var trust)) return false;
        return trust.ExpiresUtc > now
            && trust.GrantedUtc.AddMinutes(app.UnlockGraceMinutes) > now
            && trust.GraceMinutes == app.UnlockGraceMinutes
            && string.Equals(trust.PasswordHash, app.PasswordHash, StringComparison.Ordinal)
            && string.Equals(trust.PasswordSalt, app.PasswordSalt, StringComparison.Ordinal);
    }

    private sealed record AllowedProcess(string ProtectedPath, DateTime StartUtc);
    private sealed record ProcessCommandLine(string CommandLine, DateTime StartUtc, DateTimeOffset LastSeenUtc);
    private sealed record TrustedAuthorization(DateTimeOffset GrantedUtc, DateTimeOffset ExpiresUtc,
        int GraceMinutes, string? PasswordHash, string? PasswordSalt);

    public void Dispose()
    {
        if (_processStartWatcher is not null)
        {
            try
            {
                _processStartWatcher.EventArrived -= ProcessStartEventArrived;
                _processStartWatcher.Stop();
            }
            catch { }
            _processStartWatcher.Dispose();
        }
        _cts?.Cancel();
        try { _monitorTask?.Wait(1500); } catch { }
        _cts?.Dispose();
    }
}
