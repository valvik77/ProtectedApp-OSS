using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using ProtectedApp.Shared;

namespace ProtectedApp.Service;

internal sealed class GuardianEnforcer(
    GuardianOptions options,
    GuardianPolicyStore policyStore,
    ExecutionGateManager executionGate,
    ILogger<GuardianEnforcer> logger) : BackgroundService
{
    private const int GracefulCloseTimeoutMilliseconds = 2_500;
    private const int ImmediateLockGracePeriodMilliseconds = 5_000;
    private static readonly TimeSpan InteractiveCloseGracePeriod = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<int, AllowedProcess> _allowedProcesses = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _launchAllowances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrustedAuthorization> _trustedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TimedSession> _timedSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InactiveSession> _inactiveSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingProcess> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, SessionIdentity> _sessionIdentities = new();
    private readonly ConcurrentDictionary<int, byte> _eventChecks = new();
    private readonly ConcurrentDictionary<int, ProcessCommandLine> _commandLines = new();
    private readonly ConcurrentDictionary<uint, byte> _reportedMissingAgentSessions = new();
    private readonly ConcurrentDictionary<uint, AgentRecoveryState> _agentRecovery = new();
    private bool _healthTaskIncidentActive;
    private readonly ConcurrentDictionary<string, ScriptHostAuthorization> _scriptHostAuthorizations =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken _stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var leaseId = options.DiagnosticMode ? string.Empty : TamperState.BeginServiceLease(logger);
        var nextAgentCheck = DateTimeOffset.MinValue;
        var nextConfigurationCheck = DateTimeOffset.MinValue;
        var nextIntegrityCheck = DateTimeOffset.MinValue;
        var nextGateCheck = DateTimeOffset.MinValue;
        ManagementEventWatcher? processStartWatcher = null;
        _stoppingToken = stoppingToken;
        logger.LogInformation("Guardian v{Version} iniciado con cierre preventivo, autenticación progresiva y bloqueo selectivo.", GuardianConstants.ProtectionVersion);
        try
        {
            // A previous authorized launch may have temporarily removed an IFEO
            // debugger. Re-arm every gate before WMI initialization or any scan,
            // so a restarted service always starts from a fail-closed state.
            if (!options.DiagnosticMode) SynchronizeExecutionGates();
            processStartWatcher = StartProcessEventWatcher();
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    if (!options.DiagnosticMode && now >= nextConfigurationCheck)
                    {
                        nextConfigurationCheck = now.AddSeconds(10);
                        if (!TamperState.IsMaintenanceActive()
                            && ServiceConfiguration.EnsureExpectedConfiguration(Environment.ProcessPath, options.AppPath))
                            TamperState.SignalTamper("La configuración de Guardian fue modificada y se restauró su inicio, binario o recuperación automática.", logger);
                        if (!TamperState.IsMaintenanceActive())
                        {
                            var task = GuardianSystemDiagnostics.CheckHealthTask(
                                Environment.ProcessPath ?? string.Empty, options.AppPath);
                            if (!task.Healthy && !_healthTaskIncidentActive)
                            {
                                var repaired = GuardianSystemDiagnostics.TryRepairHealthTask(
                                    Environment.ProcessPath ?? string.Empty, options.AppPath, out var repairDetail);
                                _healthTaskIncidentActive = !repaired;
                                if (repaired)
                                    TamperState.SignalTamper("La tarea SYSTEM de recuperación fue modificada y Guardian la restauró automáticamente.", logger);
                                else
                                    TamperState.SignalTamper(
                                        $"La tarea SYSTEM de recuperación fue eliminada, deshabilitada o modificada: {task.Detail}. No se pudo restaurar: {repairDetail}",
                                        logger, TamperEventCode.RecoveryFailed);
                            }
                            else if (task.Healthy)
                            {
                                _healthTaskIncidentActive = false;
                            }
                        }
                    }
                    if (!options.DiagnosticMode && !TamperState.IsMaintenanceActive() && now >= nextIntegrityCheck)
                    {
                        nextIntegrityCheck = now.AddMinutes(1);
                        var integrity = GuardianIntegrity.VerifyAndRepair(Environment.ProcessPath ?? string.Empty);
                        if (integrity.Detected)
                        {
                            SynchronizeExecutionGates();
                            if (integrity.Repaired) TamperState.ExitSafeRecovery();
                            else TamperState.EnterSafeRecovery(
                                "Guardian no pudo restaurar un componente de protección y mantiene las puertas preventivas activas.", logger);
                            TamperState.SignalTamper(integrity.Repaired
                                ? $"Se detectó una modificación de binarios de Guardian y se restauró: {integrity.Detail}"
                                : $"Se detectó una modificación de binarios de Guardian que no se pudo restaurar: {integrity.Detail}",
                                logger, integrity.Repaired ? TamperEventCode.TamperDetected : TamperEventCode.RecoveryFailed);
                        }
                        if (!TamperState.IsAuditTrailValid())
                        {
                            SynchronizeExecutionGates();
                            TamperState.EnterSafeRecovery(
                                "El registro de seguridad de Guardian presenta una discontinuidad y la protección permanece en recuperación segura.", logger);
                        }
                    }

                    if (!options.DiagnosticMode && now >= nextGateCheck)
                    {
                        nextGateCheck = now.AddSeconds(2);
                        SynchronizeExecutionGates();
                    }

                    ScanProtectedProcesses(now);

                    if (!options.DiagnosticMode && now >= nextAgentCheck)
                    {
                        nextAgentCheck = now.AddSeconds(2);
                        EnsureInteractiveAgent();
                    }
                    Cleanup(now);
                }
                catch (Exception ex) { logger.LogError(ex, "Error durante la aplicación de políticas."); }

                await Task.Delay(TimeSpan.FromMilliseconds(policyStore.GetScanIntervalMilliseconds()), stoppingToken);
            }
        }
        finally
        {
            if (processStartWatcher is not null)
            {
                try { processStartWatcher.Stop(); } catch { }
                processStartWatcher.Dispose();
            }
            if (!options.DiagnosticMode)
            {
                // A normal Windows shutdown or service stop is not evidence
                // that the interactive agent was killed. Forced termination
                // cannot execute this cleanup and therefore retains evidence.
                TamperState.ClearAgentObservation();
                TamperState.EndServiceLease(leaseId);
            }
        }
    }

    public GuardianPendingRequest? ClaimPending(string userSid, int sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _pending.OrderBy(item => item.Value.DetectedUtc))
        {
            var pending = pair.Value;
            if (pending.SessionId != sessionId || !SidEquals(pending.UserSid, userSid) || pending.ClaimedUntilUtc > now)
                continue;
            pending.ClaimedUntilUtc = now.AddSeconds(45);
            return new GuardianPendingRequest
            {
                RuleId = pending.Rule.Id,
                Name = pending.Rule.Name,
                Path = pending.Rule.Path,
                DetectedUtc = pending.DetectedUtc,
                Kind = pending.Kind,
                Message = pending.Message,
                ProcessIds = pending.ProcessIds.ToList()
            };
        }
        return null;
    }

    public void DismissPending(string userSid, int sessionId, Guid ruleId)
    {
        foreach (var pair in _pending)
        {
            if (pair.Value.SessionId == sessionId && pair.Value.Rule.Id == ruleId && SidEquals(pair.Value.UserSid, userSid))
                _pending.TryRemove(pair.Key, out _);
        }
    }

    public bool LaunchAuthorized(string userSid, int sessionId, GuardianRule rule, out int processId, out string? error) =>
        LaunchAuthorizedCore(userSid, sessionId, rule, renewTrust: true, out processId, out error);

    private bool LaunchAuthorizedCore(string userSid, int sessionId, GuardianRule rule, bool renewTrust,
        out int processId, out string? error, bool scheduleBypass = false)
    {
        processId = 0;
        error = null;
        var key = ProcessKey(userSid, sessionId, rule.Path);
        _launchAllowances[key] = DateTimeOffset.UtcNow.AddSeconds(8);
        uint pid = 0;
        string? launchError = null;
        _pending.TryGetValue(key, out var pendingRequest);
        var capturedLaunch = pendingRequest?.LaunchContext;
        var launch = capturedLaunch is not null
            ? (capturedLaunch.Executable, capturedLaunch.Arguments)
            : ProtectedTarget.GetLaunchCommand(rule.Path);
        if (capturedLaunch is null
            && Path.GetExtension(rule.Path).Equals(".py", StringComparison.OrdinalIgnoreCase)
            && executionGate.FindPythonLauncher(userSid) is { } pythonLauncher)
            launch = (pythonLauncher, $"\"{Path.GetFullPath(rule.Path)}\"");
        var launchWorkingDirectory = capturedLaunch?.WorkingDirectory ?? Path.GetDirectoryName(rule.Path);
        var authorizedHostPaths = capturedLaunch is not null
            && Path.GetExtension(rule.Path).Equals(".py", StringComparison.OrdinalIgnoreCase)
                ? executionGate.GetHostAuthorizationFamily(userSid, capturedLaunch.Executable)
                : [];
        if (authorizedHostPaths.Length > 0)
        {
            var authorization = new ScriptHostAuthorization(userSid, sessionId, DateTime.UtcNow,
                DateTimeOffset.UtcNow.AddSeconds(8), authorizedHostPaths);
            _scriptHostAuthorizations[key] = authorization;
            executionGate.BeginHostAuthorization(authorizedHostPaths);
        }
        var persistentApplicationAuthorization = Path.GetExtension(rule.Path)
            .Equals(".exe", StringComparison.OrdinalIgnoreCase);
        if (persistentApplicationAuthorization) executionGate.BeginApplicationAuthorization(rule.Path);
        var launched = executionGate.WithTemporaryBypass(launch.Executable, () =>
        {
            if (options.DiagnosticMode)
            {
                try
                {
                    var process = Process.Start(new ProcessStartInfo(launch.Executable, launch.Arguments)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = launchWorkingDirectory
                    });
                    if (process is null) throw new InvalidOperationException("Process.Start no devolvió un proceso.");
                    pid = checked((uint)process.Id);
                    process.Dispose();
                    return true;
                }
                catch (Exception ex)
                {
                    launchError = ex.Message;
                    return false;
                }
            }

            if (!InteractiveProcessLauncher.StartInSession(launch.Executable, launch.Arguments,
                    (uint)sessionId, out var startedPid, out var win32Error, launchWorkingDirectory))
            {
                launchError = $"No se pudo iniciar la aplicación. Error Win32: {win32Error}.";
                return false;
            }
            pid = startedPid;
            return true;
        });

        if (!launched)
        {
            if (persistentApplicationAuthorization) executionGate.CancelApplicationAuthorization(rule.Path);
            if (authorizedHostPaths.Length > 0)
            {
                _scriptHostAuthorizations.TryRemove(key, out _);
                executionGate.CancelHostAuthorization(authorizedHostPaths);
            }
            _launchAllowances.TryRemove(key, out _);
            error = launchError ?? "No se pudo iniciar la aplicación.";
            return false;
        }

        processId = checked((int)pid);
        TryRememberAllowedProcess(processId, rule.Path, userSid, sessionId, scheduleBypass);
        if (renewTrust)
        {
            if (rule.UnlockGraceMinutes > 0)
            {
                var grantedUtc = DateTimeOffset.UtcNow;
                var credential = GetEffectiveCredential(userSid, rule);
                _trustedUntil[key] = new TrustedAuthorization(grantedUtc,
                    grantedUtc.AddMinutes(rule.UnlockGraceMinutes), rule.UnlockGraceMinutes,
                    credential.Hash, credential.Salt);
            }
            else
                _trustedUntil.TryRemove(key, out _);

            if (rule.ForceCloseAfterMinutes > 0)
            {
                var credential = GetEffectiveCredential(userSid, rule);
                _timedSessions[key] = new TimedSession(DateTimeOffset.UtcNow, userSid, sessionId,
                    rule.Id, rule.Path, rule.Name, credential.Hash, credential.Salt);
            }
            else
                _timedSessions.TryRemove(key, out _);

            if (rule.ForceCloseAfterMinutes == 0 && rule.ForceCloseAfterInactivityMinutes > 0)
            {
                var credential = GetEffectiveCredential(userSid, rule);
                _inactiveSessions[key] = new InactiveSession(DateTimeOffset.UtcNow, userSid, sessionId,
                    rule.Id, rule.Path, rule.Name, credential.Hash, credential.Salt);
            }
            else
                _inactiveSessions.TryRemove(key, out _);
        }
        DismissPending(userSid, sessionId, rule.Id);
        logger.LogInformation("Lanzamiento autorizado: {Rule} en sesión {SessionId}, PID {ProcessId}.", rule.Name, sessionId, processId);
        return true;
    }

    public void SynchronizeExecutionGates() => executionGate.Synchronize(policyStore.GetPolicies());

    // A rule can be disabled and enabled again while its previous process is
    // still winding down (browsers commonly keep background processes alive).
    // Do not carry that old authorization into the newly enabled rule.
    public void RevokeDisabledRuleAuthorizations(string userSid)
    {
        var policy = policyStore.GetPolicy(userSid);
        if (policy is null) return;

        var enabledPaths = policy.Rules
            .Where(rule => rule.IsEnabled)
            .Select(rule => rule.Path)
            .ToArray();
        var prefix = userSid + "|";
        var isDisabledPath = (string path) => !enabledPaths.Any(enabled => PathsEqual(enabled, path));
        var isDisabledKey = (string key) => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && TryGetPathFromProcessKey(key, out var path)
            && isDisabledPath(path);

        foreach (var allowed in _allowedProcesses.ToArray())
            if (SidEquals(allowed.Value.UserSid, userSid) && isDisabledPath(allowed.Value.Path))
                _allowedProcesses.TryRemove(allowed.Key, out _);
        foreach (var session in _timedSessions.ToArray())
            if (SidEquals(session.Value.UserSid, userSid) && isDisabledPath(session.Value.Path))
                _timedSessions.TryRemove(session.Key, out _);
        foreach (var session in _inactiveSessions.ToArray())
            if (SidEquals(session.Value.UserSid, userSid) && isDisabledPath(session.Value.Path))
                _inactiveSessions.TryRemove(session.Key, out _);
        foreach (var pending in _pending.ToArray())
            if (SidEquals(pending.Value.UserSid, userSid) && isDisabledPath(pending.Value.Rule.Path))
                _pending.TryRemove(pending.Key, out _);
        foreach (var trust in _trustedUntil.ToArray())
            if (isDisabledKey(trust.Key)) _trustedUntil.TryRemove(trust.Key, out _);
        foreach (var allowance in _launchAllowances.ToArray())
            if (isDisabledKey(allowance.Key)) _launchAllowances.TryRemove(allowance.Key, out _);
        foreach (var authorization in _scriptHostAuthorizations.ToArray())
        {
            if (!SidEquals(authorization.Value.UserSid, userSid) || !isDisabledKey(authorization.Key)) continue;
            if (_scriptHostAuthorizations.TryRemove(authorization.Key, out var removed))
                executionGate.CancelHostAuthorization(removed.HostPaths);
        }

        executionGate.ReconcileApplicationAuthorizations(_allowedProcesses.Values
            .Select(allowed => allowed.Path)
            .Where(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)));
    }

    public void SynchronizeTimedSessions(string userSid, int sessionId)
    {
        var policy = policyStore.GetPolicy(userSid);
        if (policy is null) return;

        foreach (var rule in policy.Rules.Where(candidate => candidate.IsEnabled))
        {
            var key = ProcessKey(userSid, sessionId, rule.Path);
            var active = HasActiveApplicationAuthorization(userSid, sessionId, rule.Path);
            if (rule.ForceCloseAfterMinutes > 0)
            {
                _inactiveSessions.TryRemove(key, out _);
                if (!_timedSessions.ContainsKey(key) && active)
                {
                    var credential = GetEffectiveCredential(userSid, rule);
                    _timedSessions[key] = new TimedSession(DateTimeOffset.UtcNow, userSid, sessionId,
                        rule.Id, rule.Path, rule.Name, credential.Hash, credential.Salt);
                    logger.LogInformation("Cierre automático iniciado al actualizar la política: {Rule}, sesión {SessionId}.",
                        rule.Name, sessionId);
                }
                continue;
            }

            _timedSessions.TryRemove(key, out _);
            if (rule.ForceCloseAfterInactivityMinutes <= 0)
            {
                _inactiveSessions.TryRemove(key, out _);
                continue;
            }
            if (_inactiveSessions.ContainsKey(key) || !active) continue;
            var inactiveCredential = GetEffectiveCredential(userSid, rule);
            _inactiveSessions[key] = new InactiveSession(DateTimeOffset.UtcNow, userSid, sessionId,
                rule.Id, rule.Path, rule.Name, inactiveCredential.Hash, inactiveCredential.Salt);
            logger.LogInformation("Cierre por inactividad iniciado al actualizar la política: {Rule}, sesión {SessionId}.",
                rule.Name, sessionId);
        }
    }

    public bool ExtendTimedSession(string userSid, int sessionId, Guid ruleId, out string? error)
    {
        error = null;
        var policy = policyStore.GetPolicy(userSid);
        var rule = policy?.Rules.FirstOrDefault(candidate => candidate.Id == ruleId && candidate.IsEnabled);
        if (rule is null || rule.ForceCloseAfterMinutes <= 0)
        {
            error = "El cierre automático ya no está activo para esta aplicación.";
            return false;
        }

        var key = ProcessKey(userSid, sessionId, rule.Path);
        if (!_timedSessions.TryGetValue(key, out var session))
        {
            error = "La sesión de la aplicación ya no está activa.";
            return false;
        }

        var credential = GetEffectiveCredential(userSid, rule);
        _timedSessions[key] = session with
        {
            GrantedUtc = DateTimeOffset.UtcNow,
            PasswordHash = credential.Hash,
            PasswordSalt = credential.Salt,
            WarningIssued = false,
            GracefulCloseRequestedUtc = null
        };
        logger.LogInformation("Cierre automático ampliado para {Rule}, sesión {SessionId}.", rule.Name, sessionId);
        return true;
    }

    public bool RecordApplicationActivity(string userSid, int sessionId, Guid ruleId)
    {
        var policy = policyStore.GetPolicy(userSid);
        var rule = policy?.Rules.FirstOrDefault(candidate => candidate.Id == ruleId && candidate.IsEnabled
            && candidate.ForceCloseAfterMinutes == 0 && candidate.ForceCloseAfterInactivityMinutes > 0);
        if (rule is null) return false;
        var key = ProcessKey(userSid, sessionId, rule.Path);
        if (!_inactiveSessions.TryGetValue(key, out var session)) return false;
        _inactiveSessions[key] = session with
        {
            LastActivityUtc = DateTimeOffset.UtcNow,
            WarningIssued = false,
            GracefulCloseRequestedUtc = null
        };
        return true;
    }

    public bool ExtendInactiveSession(string userSid, int sessionId, Guid ruleId, out string? error)
    {
        error = null;
        var policy = policyStore.GetPolicy(userSid);
        var rule = policy?.Rules.FirstOrDefault(candidate => candidate.Id == ruleId && candidate.IsEnabled);
        if (rule is null || rule.ForceCloseAfterMinutes > 0 || rule.ForceCloseAfterInactivityMinutes <= 0)
        {
            error = "El cierre por inactividad ya no está activo para esta aplicación.";
            return false;
        }

        var key = ProcessKey(userSid, sessionId, rule.Path);
        if (!_inactiveSessions.TryGetValue(key, out var session))
        {
            error = "La sesión de la aplicación ya no está activa.";
            return false;
        }

        var credential = GetEffectiveCredential(userSid, rule);
        _inactiveSessions[key] = session with
        {
            LastActivityUtc = DateTimeOffset.UtcNow,
            PasswordHash = credential.Hash,
            PasswordSalt = credential.Salt,
            WarningIssued = false,
            GracefulCloseRequestedUtc = null
        };
        logger.LogInformation("Cierre por inactividad ampliado para {Rule}, sesión {SessionId}.", rule.Name, sessionId);
        return true;
    }

    /// <summary>
    /// Revokes every authorization in a session.  When <paramref name="requestGracefulClose"/>
    /// is set, applications first receive their normal close request and have a short
    /// grace period to save work or show their own confirmation UI.  Only processes
    /// still running after that period are forcibly terminated.
    /// </summary>
    public ProcessCloseSummary RevokeAllAndTerminate(string userSid, int sessionId, bool requestGracefulClose = false)
    {
        var prefix = $"{userSid}|{sessionId}|";
        foreach (var pair in _launchAllowances)
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _launchAllowances.TryRemove(pair.Key, out _);
        foreach (var pair in _trustedUntil)
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _trustedUntil.TryRemove(pair.Key, out _);
        foreach (var pair in _timedSessions)
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _timedSessions.TryRemove(pair.Key, out _);
        foreach (var pair in _inactiveSessions)
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _inactiveSessions.TryRemove(pair.Key, out _);
        foreach (var pair in _pending)
            if (pair.Value.SessionId == sessionId && SidEquals(pair.Value.UserSid, userSid))
                _pending.TryRemove(pair.Key, out _);

        foreach (var pair in _scriptHostAuthorizations.ToArray())
        {
            var authorization = pair.Value;
            if (authorization.SessionId != sessionId || !SidEquals(authorization.UserSid, userSid)) continue;
            executionGate.CancelHostAuthorization(authorization.HostPaths);
            _scriptHostAuthorizations.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _allowedProcesses.ToArray())
            if (pair.Value.SessionId == sessionId && SidEquals(pair.Value.UserSid, userSid))
                _allowedProcesses.TryRemove(pair.Key, out _);

        var policy = policyStore.GetPolicy(userSid);
        if (policy is null) return new ProcessCloseSummary(0, 0);
        var rules = policy.Rules.Where(rule => rule.IsEnabled).ToArray();
        foreach (var rule in rules.Where(rule =>
                     Path.GetExtension(rule.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            executionGate.CancelApplicationAuthorization(rule.Path);
        SynchronizeExecutionGates();

        var gracefullyClosed = 0;
        var forciblyTerminated = 0;
        var processesByRule = new Dictionary<Guid, HashSet<int>>();
        var scriptsByRule = new HashSet<Guid>();
        var now = DateTimeOffset.UtcNow;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4 || process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.HasExited || process.SessionId != sessionId) continue;
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var matchingRule = rules.FirstOrDefault(rule =>
                        !ProtectedTarget.IsScript(rule.Path) && PathsEqual(rule.Path, path));
                    if (matchingRule is null
                        && ProtectedTarget.IsPotentialScriptHost(path)
                        && TryGetProcessCommandLine(process, now, out var commandLine)
                        && !string.IsNullOrWhiteSpace(commandLine))
                    {
                        matchingRule = rules.FirstOrDefault(rule => ProtectedTarget.IsScript(rule.Path)
                            && ProtectedTarget.CommandLineReferences(commandLine, rule.Path));
                    }
                    if (matchingRule is null) continue;
                    if (requestGracefulClose)
                    {
                        if (!processesByRule.TryGetValue(matchingRule.Id, out var processIds))
                            processesByRule[matchingRule.Id] = processIds = [];
                        processIds.Add(process.Id);
                        if (ProtectedTarget.IsScript(matchingRule.Path)) scriptsByRule.Add(matchingRule.Id);
                    }
                    else
                    {
                        process.Kill(entireProcessTree: true);
                        forciblyTerminated++;
                    }
                }
                catch { }
            }
        }

        if (requestGracefulClose)
        {
            var closeRequestProcesses = new Dictionary<int, Guid>();
            var rulesThatAcceptedNormalClose = new HashSet<Guid>();
            foreach (var (ruleId, processIds) in processesByRule)
            {
                if (scriptsByRule.Contains(ruleId)) continue;
                foreach (var processId in processIds)
                {
                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        if (!process.HasExited && process.CloseMainWindow())
                        {
                            closeRequestProcesses[processId] = ruleId;
                            rulesThatAcceptedNormalClose.Add(ruleId);
                        }
                    }
                    catch { }
                }
            }

            var deadline = Environment.TickCount64 + ImmediateLockGracePeriodMilliseconds;
            while (closeRequestProcesses.Count > 0 && Environment.TickCount64 < deadline)
            {
                foreach (var processId in closeRequestProcesses.Keys.ToArray())
                {
                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        if (!process.HasExited) continue;
                    }
                    catch { }
                    closeRequestProcesses.Remove(processId);
                }
                if (closeRequestProcesses.Count > 0) Thread.Sleep(100);
            }

            // Chromium-based browsers retain helper processes after their window has accepted
            // a normal close.  Killing those helpers turns a clean exit into a crash and makes
            // the browser offer session recovery.  Force a rule only while a window that
            // accepted the close request is still alive, or when it had no normal window to close.
            var rulesStillAwaitingNormalClose = closeRequestProcesses.Values.ToHashSet();
            var rulesClosedNormally = processesByRule.Keys
                .Where(ruleId => rulesThatAcceptedNormalClose.Contains(ruleId)
                    && !rulesStillAwaitingNormalClose.Contains(ruleId))
                .ToHashSet();
            gracefullyClosed = rulesClosedNormally.Count;

            foreach (var (ruleId, processIds) in processesByRule)
            {
                if (rulesClosedNormally.Contains(ruleId)) continue;
                var terminatedRule = false;
                foreach (var processId in processIds)
                {
                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        if (process.HasExited) continue;
                        process.Kill(entireProcessTree: true);
                        terminatedRule = true;
                        break;
                    }
                    catch { }
                }
                if (terminatedRule) forciblyTerminated++;
            }
        }

        var summary = new ProcessCloseSummary(gracefullyClosed, forciblyTerminated);
        logger.LogInformation(requestGracefulClose
                ? "Autorizaciones revocadas inmediatamente para {Sid}, sesión {SessionId}; {Graceful} cierres normales y {Forced} cierres forzados."
                : "Autorizaciones revocadas inmediatamente para {Sid}, sesión {SessionId}; procesos finalizados {Forced}.",
            userSid, sessionId, summary.GracefulCloseCount, summary.ForcedTerminationCount);
        return summary;
    }

    public int? RevokeRuleAndTerminate(string userSid, int sessionId, Guid ruleId, bool gracefulClose = false)
    {
        var policy = policyStore.GetPolicy(userSid);
        var rule = policy?.Rules.FirstOrDefault(candidate => candidate.Id == ruleId);
        if (rule is null) return null;

        var key = ProcessKey(userSid, sessionId, rule.Path);
        _launchAllowances.TryRemove(key, out _);
        _trustedUntil.TryRemove(key, out _);
        _timedSessions.TryRemove(key, out _);
        _inactiveSessions.TryRemove(key, out _);
        foreach (var pending in _pending.ToArray())
            if (pending.Value.SessionId == sessionId
                && pending.Value.Rule.Id == ruleId
                && SidEquals(pending.Value.UserSid, userSid))
                _pending.TryRemove(pending.Key, out _);

        if (_scriptHostAuthorizations.TryRemove(key, out var removedHostAuthorization))
        {
            var hostsStillRequired = _scriptHostAuthorizations.Values
                .Where(item => item.SessionId == sessionId && SidEquals(item.UserSid, userSid))
                .SelectMany(item => item.HostPaths)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            executionGate.CancelHostAuthorization(removedHostAuthorization.HostPaths
                .Where(path => !hostsStillRequired.Contains(path)));
        }

        if (rule.IsEnabled
            && Path.GetExtension(rule.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            executionGate.CancelApplicationAuthorization(rule.Path);

        var terminatedProcessIds = new HashSet<int>();
        var ruleAuthorizations = _allowedProcesses.ToArray()
            .Where(allowed => allowed.Value.SessionId == sessionId
                && SidEquals(allowed.Value.UserSid, userSid)
                && PathsEqual(allowed.Value.Path, rule.Path))
            .ToArray();
        var descendantProcessIds = FindLaunchedDescendantProcessIds(ruleAuthorizations, sessionId);
        foreach (var allowed in ruleAuthorizations)
        {
            TryTerminate(allowed.Key, terminatedProcessIds, gracefulClose && !ProtectedTarget.IsScript(rule.Path));
            _allowedProcesses.TryRemove(allowed.Key, out _);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4 || process.Id == Environment.ProcessId
                    || terminatedProcessIds.Contains(process.Id)) continue;
                try
                {
                    if (process.HasExited || process.SessionId != sessionId) continue;
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var matches = !ProtectedTarget.IsScript(rule.Path)
                        ? PathsEqual(rule.Path, path)
                        : ProtectedTarget.IsPotentialScriptHost(path)
                            && TryGetProcessCommandLine(process, now, out var commandLine)
                            && ProtectedTarget.CommandLineReferences(commandLine, rule.Path);
                    if (!matches && !descendantProcessIds.Contains(process.Id)) continue;
                    TryTerminate(process.Id, terminatedProcessIds, gracefulClose && !ProtectedTarget.IsScript(rule.Path));
                }
                catch { }
            }
        }

        logger.LogInformation(
            "Autorización revocada para {Rule}, {Sid}, sesión {SessionId}; procesos finalizados {Count}.",
            rule.Name, userSid, sessionId, terminatedProcessIds.Count);
        return terminatedProcessIds.Count;
    }

    public int? EndApplicationSession(string userSid, int sessionId, Guid ruleId)
    {
        var affected = RevokeRuleAndTerminate(userSid, sessionId, ruleId);
        if (affected is not null)
            logger.LogInformation(
                "Sesión visual finalizada para la regla {RuleId}, SID {Sid}, sesión {SessionId}; procesos residuales {Count}.",
                ruleId, userSid, sessionId, affected.Value);
        return affected;
    }

    private static HashSet<int> FindLaunchedDescendantProcessIds(
        IReadOnlyCollection<KeyValuePair<int, AllowedProcess>> authorizations, int sessionId)
    {
        var descendants = new HashSet<int>();
        if (authorizations.Count == 0) return descendants;

        var roots = authorizations.ToDictionary(item => item.Key, item => item.Value.StartUtc);
        var knownParents = new HashSet<int>(roots.Keys);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, SessionId, CreationDate FROM Win32_Process");
            using var results = searcher.Get();
            var candidates = new List<(int ProcessId, int ParentProcessId, DateTimeOffset CreatedUtc)>();
            foreach (ManagementObject item in results)
            {
                if (Convert.ToInt32(item["SessionId"]) != sessionId) continue;
                var processId = Convert.ToInt32(item["ProcessId"]);
                var parentProcessId = Convert.ToInt32(item["ParentProcessId"]);
                var creation = Convert.ToString(item["CreationDate"]);
                var createdUtc = string.IsNullOrWhiteSpace(creation)
                    ? DateTimeOffset.MinValue
                    : new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(creation).ToUniversalTime());
                candidates.Add((processId, parentProcessId, createdUtc));
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var candidate in candidates)
                {
                    if (knownParents.Contains(candidate.ProcessId) || !knownParents.Contains(candidate.ParentProcessId)) continue;
                    if (roots.TryGetValue(candidate.ParentProcessId, out var rootStart)
                        && candidate.CreatedUtc != DateTimeOffset.MinValue
                        && candidate.CreatedUtc < rootStart.AddSeconds(-2)) continue;
                    descendants.Add(candidate.ProcessId);
                    knownParents.Add(candidate.ProcessId);
                    changed = true;
                }
            }
        }
        catch { }
        return descendants;
    }

    private static void TryTerminate(int processId, ISet<int> terminatedProcessIds, bool requestGracefulClose = false)
    {
        if (terminatedProcessIds.Contains(processId)) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return;
            if (requestGracefulClose && process.CloseMainWindow() && process.WaitForExit(GracefulCloseTimeoutMilliseconds))
            {
                terminatedProcessIds.Add(processId);
                return;
            }
            process.Kill(entireProcessTree: true);
            terminatedProcessIds.Add(processId);
        }
        catch { }
    }

    public bool RegisterBlockedAttempt(string userSid, int sessionId, string targetPath)
    {
        string normalized;
        try { normalized = Path.GetFullPath(targetPath); }
        catch { return false; }
        var policy = policyStore.GetPolicy(userSid);
        var rule = policy?.Rules.FirstOrDefault(candidate => candidate.IsEnabled
            && !ProtectedTarget.IsScript(candidate.Path)
            && PathsEqual(candidate.Path, normalized));
        if (rule is null) return false;

        var now = DateTimeOffset.UtcNow;
        var pendingKey = ProcessKey(userSid, sessionId, normalized);
        var schedule = GetScheduleDisposition(rule, now);
        if (schedule == ScheduleDisposition.Block)
        {
            SetPending(pendingKey, userSid, sessionId, rule, now, null,
                GuardianProtocol.PendingBlocked, BuildScheduleBlockMessage(rule));
            logger.LogWarning("Inicio denegado fuera del horario permitido: {Rule} ({Target}), sesión {SessionId}.",
                rule.Name, normalized, sessionId);
            return true;
        }
        if (schedule == ScheduleDisposition.Allow)
        {
            if (LaunchAuthorizedCore(userSid, sessionId, rule, renewTrust: false,
                    out _, out var bypassError, scheduleBypass: true))
            {
                logger.LogInformation("Inicio permitido fuera del horario de protección: {Rule}, sesión {SessionId}.",
                    rule.Name, sessionId);
                return true;
            }
            logger.LogWarning("No se pudo iniciar {Rule} fuera del horario de protección: {Error}",
                rule.Name, bypassError);
            return true;
        }
        SetPending(pendingKey, userSid, sessionId, rule, now);
        string? launchError = null;
        if (HasTrustedAuthorization(pendingKey, now, rule, userSid)
            && LaunchAuthorizedCore(userSid, sessionId, rule, renewTrust: false, out _, out launchError))
        {
            logger.LogInformation("Relanzamiento de confianza sin contraseña: {Rule}, sesión {SessionId}.",
                rule.Name, sessionId);
            return true;
        }
        if (launchError is not null)
        {
            SetPending(pendingKey, userSid, sessionId, rule, now, null,
                GuardianProtocol.PendingError, $"No se pudo iniciar la aplicación: {launchError}");
            logger.LogWarning("No se pudo aplicar el relanzamiento de confianza para {Rule}: {Error}",
                rule.Name, launchError);
        }
        logger.LogWarning("Inicio bloqueado antes de cargar la imagen: {Rule} ({Target}), sesión {SessionId}.",
            rule.Name, normalized, sessionId);
        return true;
    }

    public bool RegisterHostAttempt(string userSid, int sessionId, string hostPath,
        string arguments, string? workingDirectory, out string? error)
    {
        error = null;
        string normalizedHost;
        try { normalizedHost = Path.GetFullPath(hostPath); }
        catch { error = "La ruta del intérprete no es válida."; return false; }
        if (!executionGate.IsPythonHost(userSid, normalizedHost))
        {
            error = "El intérprete no pertenece a una puerta administrada.";
            return false;
        }

        var policy = policyStore.GetPolicy(userSid);
        if (policy is null) { error = "No existe una política para el usuario."; return false; }
        var safeWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Path.GetDirectoryName(normalizedHost);
        var context = $"\"{normalizedHost}\" {arguments} \"{safeWorkingDirectory}\"";
        var rule = policy.Rules.FirstOrDefault(candidate => candidate.IsEnabled
            && Path.GetExtension(candidate.Path).Equals(".py", StringComparison.OrdinalIgnoreCase)
            && ProtectedTarget.CommandLineReferences(context, candidate.Path));
        if (rule is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var pendingKey = ProcessKey(userSid, sessionId, rule.Path);
            // The original host command line is untrusted input intercepted by
            // IFEO. Retaining options such as "-c" or "-m" would turn the
            // authorization dialog for a protected script into permission to
            // execute a different payload. Re-launch only the canonical script.
            var launchContext = new CapturedLaunch(normalizedHost,
                $"\"{Path.GetFullPath(rule.Path)}\"", Path.GetDirectoryName(rule.Path));
            var schedule = GetScheduleDisposition(rule, now);
            if (schedule == ScheduleDisposition.Block)
            {
                SetPending(pendingKey, userSid, sessionId, rule, now, launchContext,
                    GuardianProtocol.PendingBlocked, BuildScheduleBlockMessage(rule));
                logger.LogWarning("Script denegado fuera del horario permitido: {Rule} ({Target}), sesión {SessionId}.",
                    rule.Name, rule.Path, sessionId);
                return true;
            }
            SetPending(pendingKey, userSid, sessionId, rule, now, launchContext);
            if (schedule == ScheduleDisposition.Allow)
            {
                if (LaunchAuthorizedCore(userSid, sessionId, rule, renewTrust: false,
                        out _, out var bypassError, scheduleBypass: true))
                {
                    logger.LogInformation("Script permitido fuera del horario de protección: {Rule}, sesión {SessionId}.",
                        rule.Name, sessionId);
                    return true;
                }
                error = bypassError ?? "No se pudo iniciar el script fuera del horario de protección.";
                SetPending(pendingKey, userSid, sessionId, rule, now, launchContext,
                    GuardianProtocol.PendingError, error);
                return true;
            }
            string? launchError = null;
            if (HasTrustedAuthorization(pendingKey, now, rule, userSid)
                && LaunchAuthorizedCore(userSid, sessionId, rule, renewTrust: false, out _, out launchError))
            {
                logger.LogInformation("Script relanzado dentro del periodo de confianza: {Rule}, sesión {SessionId}.",
                    rule.Name, sessionId);
                return true;
            }
            if (launchError is not null)
            {
                SetPending(pendingKey, userSid, sessionId, rule, now, launchContext,
                    GuardianProtocol.PendingError, $"No se pudo iniciar el script: {launchError}");
                logger.LogWarning("No se pudo relanzar el script de confianza {Rule}: {Error}",
                    rule.Name, launchError);
            }
            logger.LogWarning("Script Python bloqueado antes de iniciar el intérprete: {Rule} ({Target}), sesión {SessionId}.",
                rule.Name, rule.Path, sessionId);
            return true;
        }

        var started = executionGate.WithTemporaryBypass(normalizedHost, () =>
        {
            if (options.DiagnosticMode)
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(normalizedHost, arguments)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = safeWorkingDirectory,
                        CreateNoWindow = true
                    });
                    return (Success: process is not null, Error: process is null ? -1 : 0);
                }
                catch { return (Success: false, Error: -1); }
            }
            return InteractiveProcessLauncher.StartInSession(normalizedHost, arguments, (uint)sessionId,
                    out _, out var win32Error, safeWorkingDirectory)
                ? (Success: true, Error: 0)
                : (Success: false, Error: win32Error);
        });
        if (started.Success) return true;
        error = $"No se pudo reenviar el intérprete. Error Win32: {started.Error}.";
        return false;
    }

    private void ScanProtectedProcesses(DateTimeOffset now)
    {
        var policies = policyStore.GetPolicies();
        if (policies.Count == 0) return;
        var policyBySid = policies.ToDictionary(p => p.UserSid, StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                InspectProcess(process, now, policyBySid);
            }
        }
    }

    private ManagementEventWatcher? StartProcessEventWatcher()
    {
        try
        {
            var watcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            watcher.EventArrived += ProcessStartEventArrived;
            watcher.Start();
            logger.LogInformation("Suscripción WMI Win32_ProcessStartTrace activa.");
            return watcher;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo activar la detección por eventos; continuará el barrido de respaldo.");
            return null;
        }
    }

    private void ProcessStartEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var processName = Convert.ToString(e.NewEvent.Properties["ProcessName"].Value);
            var processId = checked((int)Convert.ToUInt32(e.NewEvent.Properties["ProcessID"].Value));
            var parentProcessId = checked((int)Convert.ToUInt32(e.NewEvent.Properties["ParentProcessID"].Value));
            if (TryRememberAuthorizedDescendant(processId, parentProcessId)) return;
            if (!policyStore.MayProtectProcessName(processName)) return;
            if (!_eventChecks.TryAdd(processId, 0)) return;
            // Congela el candidato dentro del callback para que no pueda crear
            // su interfaz mientras Guardian confirma la ruta o el script.
            var suspended = ProcessSuspender.TrySuspend(processId);
            _ = InspectStartedProcessAsync(processId, suspended);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Evento de proceso no válido."); }
    }

    private bool TryRememberAuthorizedDescendant(int processId, int parentProcessId)
    {
        if (!_allowedProcesses.TryGetValue(parentProcessId, out var parent)) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.SessionId != parent.SessionId) return false;
            TryRememberAllowedProcess(processId, parent.Path, parent.UserSid, parent.SessionId,
                parent.ScheduleBypass);
            logger.LogDebug("Proceso descendiente autorizado: PID {ProcessId}, padre {ParentProcessId}, regla {Path}.",
                processId, parentProcessId, parent.Path);
            return true;
        }
        catch { return false; }
    }

    private async Task InspectStartedProcessAsync(int processId, bool suspended)
    {
        try
        {
            // WMI can publish the event a few milliseconds before MainModule is
            // queryable. Short retries preserve the event-driven latency.
            foreach (var delay in new[] { 0, 5, 15, 35, 75 })
            {
                if (delay > 0) await Task.Delay(delay, _stoppingToken);
                if (_stoppingToken.IsCancellationRequested) return;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    var policies = policyStore.GetPolicies().ToDictionary(p => p.UserSid, StringComparer.OrdinalIgnoreCase);
                    if (InspectProcess(process, DateTimeOffset.UtcNow, policies)) return;
                }
                catch (ArgumentException) { return; }
            }
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogDebug(ex, "No se pudo inspeccionar el PID {ProcessId} desde el evento.", processId); }
        finally
        {
            if (suspended) ProcessSuspender.TryResume(processId);
            _eventChecks.TryRemove(processId, out _);
        }
    }

    private bool InspectProcess(Process process, DateTimeOffset now, IReadOnlyDictionary<string, GuardianPolicy> policyBySid)
    {
        if (process.Id is 0 or 4 || process.Id == Environment.ProcessId) return true;
        string? path;
        int sessionId;
        try
        {
            if (process.HasExited) return true;
            path = process.MainModule?.FileName;
            sessionId = process.SessionId;
        }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (PathsEqual(path, options.AppPath)) return true;
        var sid = GetSessionSid(sessionId, now);
        if (sid is null) return false;
        if (!policyBySid.TryGetValue(sid, out var policy)) return true;
        string? matchedCommandLine = null;
        var rule = policy.Rules.FirstOrDefault(candidate =>
            candidate.IsEnabled && !ProtectedTarget.IsScript(candidate.Path) && PathsEqual(candidate.Path, path));
        if (rule is null
            && ProtectedTarget.IsPotentialScriptHost(path)
            && policy.Rules.Any(candidate => candidate.IsEnabled && ProtectedTarget.IsScript(candidate.Path)))
        {
            if (!TryGetProcessCommandLine(process, now, out matchedCommandLine)) return false;
            rule = policy.Rules.FirstOrDefault(candidate =>
                candidate.IsEnabled
                && ProtectedTarget.IsScript(candidate.Path)
                && ProtectedTarget.CommandLineReferences(matchedCommandLine, candidate.Path));
        }
        if (rule is null) return true;
        var protectedPath = rule.Path;

        var schedule = GetScheduleDisposition(rule, now);
        if (schedule == ScheduleDisposition.Allow)
        {
            TryRememberAllowedProcess(process.Id, protectedPath, sid, sessionId, scheduleBypass: true);
            return true;
        }
        if (schedule == ScheduleDisposition.Block)
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(2000);
                _allowedProcesses.TryRemove(process.Id, out _);
                var pendingKey = ProcessKey(sid, sessionId, protectedPath);
                SetPending(pendingKey, sid, sessionId, rule, now, null,
                    GuardianProtocol.PendingBlocked, BuildScheduleBlockMessage(rule));
                logger.LogWarning("Proceso terminado fuera del horario permitido: {Rule} ({Target}), PID {ProcessId}.",
                    rule.Name, protectedPath, process.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo bloquear fuera de horario {Path}, PID {ProcessId}.",
                    protectedPath, process.Id);
            }
            return true;
        }
        if (_allowedProcesses.TryGetValue(process.Id, out var scheduledProcess) && scheduledProcess.ScheduleBypass)
            _allowedProcesses.TryRemove(process.Id, out _);

        if (Path.GetExtension(protectedPath).Equals(".py", StringComparison.OrdinalIgnoreCase)
            && executionGate.ObservePythonHost(path))
        {
            logger.LogInformation("Nuevo intérprete Python observado y añadido a Gate: {HostPath}.", path);
            SynchronizeExecutionGates();
        }

        if (IsAllowed(process, protectedPath)) return true;
        if (HasActiveApplicationAuthorization(sid, sessionId, protectedPath))
        {
            TryRememberAllowedProcess(process.Id, protectedPath, sid, sessionId);
            return true;
        }
        var launchKey = ProcessKey(sid, sessionId, protectedPath);
        if (_launchAllowances.TryGetValue(launchKey, out var expires) && expires > now)
        {
            TryRememberAllowedProcess(process.Id, protectedPath, sid, sessionId);
            return true;
        }
        if (HasTrustedAuthorization(launchKey, now, rule, sid))
        {
            TryRememberAllowedProcess(process.Id, protectedPath, sid, sessionId);
            return true;
        }

        try
        {
            process.Kill(true);
            process.WaitForExit(2000);
            var pendingKey = ProcessKey(sid, sessionId, protectedPath);
            var capturedLaunch = Path.GetExtension(protectedPath).Equals(".py", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(matchedCommandLine)
                    ? new CapturedLaunch(path, ExtractArguments(matchedCommandLine, path), Path.GetDirectoryName(protectedPath))
                    : null;
            SetPending(pendingKey, sid, sessionId, rule, now, capturedLaunch);
            logger.LogWarning("Ejecución interceptada por SYSTEM: {Rule} ({Target}), host {HostPath}, PID {ProcessId}, sesión {SessionId}.", rule.Name, protectedPath, path, process.Id, sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo terminar {Path}, PID {ProcessId}.", path, process.Id);
        }
        return true;
    }

    private void EnsureInteractiveAgent()
    {
        var sessionId = InteractiveProcessLauncher.GetActiveSessionId();
        if (sessionId is null || !File.Exists(options.AppPath)) return;
        var now = DateTimeOffset.UtcNow;
        if (TamperState.IsAgentHeartbeatFresh(sessionId.Value))
        {
            _reportedMissingAgentSessions.TryRemove(sessionId.Value, out _);
            _agentRecovery.TryRemove(sessionId.Value, out _);
            return;
        }

        var agentRunning = IsAgentRunning(sessionId.Value, options.AppPath);
        if (!_agentRecovery.TryGetValue(sessionId.Value, out var pendingRecovery) && agentRunning)
        {
            // A process can take a few seconds to initialize after sign-in.
            // Only replace it when it never proves that the UI is responsive.
            _agentRecovery[sessionId.Value] = new AgentRecoveryState(0, now, now.AddSeconds(60));
            return;
        }

        if (pendingRecovery is not null && pendingRecovery.NextAttemptUtc > now)
            return;

        if (agentRunning)
        {
            // An old UI can legitimately remain alive while a new Guardian is
            // installed. A missing heartbeat therefore means "recover the
            // interface", never "lock the user's Windows session". The
            // service continues enforcing the protected-app policy meanwhile.
            if (_reportedMissingAgentSessions.TryAdd(sessionId.Value, 0))
                logger.LogWarning("La interfaz de ProtectedApp no envió pulso; Guardian la reemplazará de forma silenciosa.");
            StopUnhealthyInteractiveAgents(sessionId.Value);
        }

        if (InteractiveProcessLauncher.StartInSession(options.AppPath, "--background --service-managed", sessionId.Value, out var processId, out var error))
        {
            var attempt = (pendingRecovery?.Attempt ?? 0) + 1;
            var retryDelay = attempt switch
            {
                1 => TimeSpan.FromSeconds(3),
                2 => TimeSpan.FromSeconds(8),
                3 => TimeSpan.FromSeconds(20),
                _ => TimeSpan.FromSeconds(60)
            };
            _agentRecovery[sessionId.Value] = new AgentRecoveryState(attempt, now, now + retryDelay);
            // StartInSession only confirms process creation. The agent is
            // healthy only after it sends its authenticated heartbeat.
            logger.LogInformation("Interfaz iniciada en sesión {SessionId}, PID {ProcessId}.", sessionId, processId);
        }
        else
        {
            _agentRecovery[sessionId.Value] = new AgentRecoveryState(
                (pendingRecovery?.Attempt ?? 0) + 1, now, now.AddSeconds(10));
            logger.LogWarning("No se pudo iniciar la interfaz en sesión {SessionId}. Error Win32: {Error}.", sessionId, error);
        }
    }

    private string? GetSessionSid(int sessionId, DateTimeOffset now)
    {
        if (_sessionIdentities.TryGetValue(sessionId, out var cached) && cached.ExpiresUtc > now) return cached.Sid;
        var sid = options.DiagnosticMode && sessionId == Process.GetCurrentProcess().SessionId
            ? WindowsIdentity.GetCurrent().User?.Value
            : InteractiveProcessLauncher.GetSessionUserSid((uint)sessionId);
        if (sid is not null) _sessionIdentities[sessionId] = new SessionIdentity(sid, now.AddSeconds(10));
        return sid;
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

    private bool IsAllowed(Process process, string path)
    {
        if (!_allowedProcesses.TryGetValue(process.Id, out var allowed)) return false;
        try
        {
            if (PathsEqual(allowed.Path, path) && process.StartTime.ToUniversalTime() == allowed.StartUtc)
                return true;
        }
        catch { }
        _allowedProcesses.TryRemove(process.Id, out _);
        return false;
    }

    private bool HasActiveApplicationAuthorization(string userSid, int sessionId, string path)
    {
        foreach (var pair in _allowedProcesses)
        {
            var allowed = pair.Value;
            if (allowed.SessionId != sessionId
                || !SidEquals(allowed.UserSid, userSid)
                || !PathsEqual(allowed.Path, path))
                continue;
            try
            {
                using var process = Process.GetProcessById(pair.Key);
                if (!process.HasExited && process.StartTime.ToUniversalTime() == allowed.StartUtc)
                    return true;
            }
            catch { }
            _allowedProcesses.TryRemove(pair.Key, out _);
        }
        return false;
    }

    private void TryRememberAllowedProcess(int processId, string path, string userSid, int sessionId,
        bool scheduleBypass = false)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (scheduleBypass && _allowedProcesses.TryGetValue(processId, out var existing)
                && !existing.ScheduleBypass) return;
            _allowedProcesses[processId] = new AllowedProcess(
                path, process.StartTime.ToUniversalTime(), userSid, sessionId, scheduleBypass);
        }
        catch { }
    }

    private void Cleanup(DateTimeOffset now)
    {
        ClearClosedAutomaticSessions();
        EnforceTimedSessions(now);
        EnforceInactiveSessions(now);
        foreach (var allowance in _launchAllowances)
            if (allowance.Value <= now) _launchAllowances.TryRemove(allowance.Key, out _);
        foreach (var trust in _trustedUntil)
            if (trust.Value.ExpiresUtc <= now) _trustedUntil.TryRemove(trust.Key, out _);
        foreach (var pending in _pending)
            if (now - pending.Value.DetectedUtc > TimeSpan.FromMinutes(10)) _pending.TryRemove(pending.Key, out _);
        foreach (var allowed in _allowedProcesses)
        {
            try { using var process = Process.GetProcessById(allowed.Key); if (!process.HasExited) continue; }
            catch { }
            _allowedProcesses.TryRemove(allowed.Key, out _);
        }
        foreach (var commandLine in _commandLines)
            if (now - commandLine.Value.LastSeenUtc > TimeSpan.FromMinutes(1))
                _commandLines.TryRemove(commandLine.Key, out _);
        var activeAuthorizedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _scriptHostAuthorizations)
        {
            var authorization = pair.Value;
            if (authorization.GraceExpiresUtc > now || HasActiveAuthorizedHostProcess(authorization))
            {
                activeAuthorizedHosts.UnionWith(authorization.HostPaths);
                continue;
            }
            _scriptHostAuthorizations.TryRemove(pair.Key, out _);
        }
        executionGate.ReconcileHostAuthorizations(activeAuthorizedHosts);
        executionGate.ReconcileApplicationAuthorizations(_allowedProcesses.Values
            .Select(allowed => allowed.Path)
            .Where(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)));
    }

    private void EnforceInactiveSessions(DateTimeOffset now)
    {
        foreach (var pair in _inactiveSessions)
        {
            var session = pair.Value;
            var policy = policyStore.GetPolicy(session.UserSid);
            var rule = policy?.Rules.FirstOrDefault(candidate => candidate.Id == session.RuleId
                && candidate.IsEnabled && PathsEqual(candidate.Path, session.Path));
            if (rule is null || rule.ForceCloseAfterMinutes > 0 || rule.ForceCloseAfterInactivityMinutes <= 0)
            {
                _inactiveSessions.TryRemove(pair.Key, out _);
                continue;
            }

            var credential = GetEffectiveCredential(session.UserSid, rule);
            if (!string.Equals(session.PasswordHash, credential.Hash, StringComparison.Ordinal)
                || !string.Equals(session.PasswordSalt, credential.Salt, StringComparison.Ordinal))
            {
                _inactiveSessions.TryRemove(pair.Key, out _);
                continue;
            }

            var expiresUtc = session.LastActivityUtc.AddMinutes(rule.ForceCloseAfterInactivityMinutes);
            if (policy!.CloseWarningNotificationsEnabled && !session.WarningIssued && expiresUtc > now
                && expiresUtc - now <= TimeSpan.FromMinutes(1)
                && _inactiveSessions.TryUpdate(pair.Key, session with { WarningIssued = true }, session))
            {
                SetPending(pair.Key, session.UserSid, session.SessionId, rule, now, null,
                    GuardianProtocol.PendingNotice,
                    $"{rule.Name} se cerrará por inactividad en menos de un minuto.");
            }
            if (expiresUtc > now) continue;
            if (TryRequestInteractiveClose(pair.Key, session, rule, now)) continue;
            if (!((ICollection<KeyValuePair<string, InactiveSession>>)_inactiveSessions).Remove(pair)) continue;
            var terminated = RevokeRuleAndTerminate(session.UserSid, session.SessionId, session.RuleId) ?? 0;
            SynchronizeExecutionGates();
            logger.LogInformation("Cierre por inactividad: {Rule}, sesión {SessionId}, procesos terminados {Count}.",
                session.Name, session.SessionId, terminated);
        }
    }

    private void ClearClosedAutomaticSessions()
    {
        foreach (var pair in _timedSessions)
        {
            var session = pair.Value;
            if (HasActiveApplicationAuthorization(session.UserSid, session.SessionId, session.Path)) continue;
            if (_timedSessions.TryRemove(pair.Key, out _)) RemoveAutomaticClosePending(pair.Key, session.RuleId);
        }

        foreach (var pair in _inactiveSessions)
        {
            var session = pair.Value;
            if (HasActiveApplicationAuthorization(session.UserSid, session.SessionId, session.Path)) continue;
            if (_inactiveSessions.TryRemove(pair.Key, out _)) RemoveAutomaticClosePending(pair.Key, session.RuleId);
        }
    }

    private void RemoveAutomaticClosePending(string key, Guid ruleId)
    {
        if (_pending.TryGetValue(key, out var pending)
            && pending.Rule.Id == ruleId
            && (string.Equals(pending.Kind, GuardianProtocol.PendingNotice, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pending.Kind, GuardianProtocol.PendingGracefulClose, StringComparison.OrdinalIgnoreCase)))
            _pending.TryRemove(key, out _);
    }

    internal void EnforceTimedSessions(DateTimeOffset now)
    {
        foreach (var pair in _timedSessions)
        {
            var session = pair.Value;
            var policy = policyStore.GetPolicy(session.UserSid);
            var rule = policy?.Rules.FirstOrDefault(candidate => candidate.Id == session.RuleId
                && candidate.IsEnabled && PathsEqual(candidate.Path, session.Path));
            if (rule is null || rule.ForceCloseAfterMinutes <= 0)
            {
                _timedSessions.TryRemove(pair.Key, out _);
                continue;
            }

            var credential = GetEffectiveCredential(session.UserSid, rule);
            if (!string.Equals(session.PasswordHash, credential.Hash, StringComparison.Ordinal)
                || !string.Equals(session.PasswordSalt, credential.Salt, StringComparison.Ordinal))
            {
                _timedSessions.TryRemove(pair.Key, out _);
                _trustedUntil.TryRemove(pair.Key, out _);
                continue;
            }

            var expiresUtc = session.GrantedUtc.AddMinutes(rule.ForceCloseAfterMinutes);
            if (policy!.CloseWarningNotificationsEnabled && !session.WarningIssued && expiresUtc > now && expiresUtc - now <= TimeSpan.FromMinutes(1)
                && _timedSessions.TryUpdate(pair.Key, session with { WarningIssued = true }, session))
            {
                SetPending(pair.Key, session.UserSid, session.SessionId, rule, now, null,
                    GuardianProtocol.PendingNotice,
                    $"{rule.Name} se cerrará automáticamente en menos de un minuto.");
                logger.LogInformation("Aviso de cierre automático enviado para {Rule}, sesión {SessionId}.",
                    rule.Name, session.SessionId);
            }
            if (expiresUtc > now) continue;

            if (TryRequestInteractiveClose(pair.Key, session, rule, now)) continue;

            // The user can extend the session from the warning shown during
            // this final minute. Remove only the exact session we evaluated:
            // an unconditional TryRemove could otherwise discard the newer
            // session just written by ExtendTimedSession.
            if (!((ICollection<KeyValuePair<string, TimedSession>>)_timedSessions).Remove(pair)) continue;
            _trustedUntil.TryRemove(pair.Key, out _);
            _launchAllowances.TryRemove(pair.Key, out _);
            _pending.TryRemove(pair.Key, out _);
            if (_scriptHostAuthorizations.TryRemove(pair.Key, out var hostAuthorization))
                executionGate.CancelHostAuthorization(hostAuthorization.HostPaths);

            var terminated = RevokeRuleAndTerminate(session.UserSid, session.SessionId, session.RuleId) ?? 0;
            // Do not wait for the periodic authorization reconciliation. A
            // user can launch the same application immediately after its
            // timed close; its gate must already be armed so that attempt
            // creates a fresh password request instead of disappearing.
            if (Path.GetExtension(rule.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                executionGate.CancelApplicationAuthorization(rule.Path);
            SynchronizeExecutionGates();
            logger.LogInformation("Sesión temporizada finalizada: {Rule}, sesión {SessionId}, procesos terminados {Count}.",
                session.Name, session.SessionId, terminated);
        }
    }

    private bool TryRequestInteractiveClose(string key, TimedSession session, GuardianRule rule, DateTimeOffset now) =>
        TryRequestInteractiveClose(key, session.UserSid, session.SessionId, session.RuleId, session.Path,
            session.GracefulCloseRequestedUtc, requestedUtc => session with { GracefulCloseRequestedUtc = requestedUtc },
            session, rule, now, _timedSessions);

    private bool TryRequestInteractiveClose(string key, InactiveSession session, GuardianRule rule, DateTimeOffset now) =>
        TryRequestInteractiveClose(key, session.UserSid, session.SessionId, session.RuleId, session.Path,
            session.GracefulCloseRequestedUtc, requestedUtc => session with { GracefulCloseRequestedUtc = requestedUtc },
            session, rule, now, _inactiveSessions);

    private bool TryRequestInteractiveClose<TSession>(string key, string userSid, int sessionId, Guid ruleId, string path,
        DateTimeOffset? requestedUtc, Func<DateTimeOffset, TSession> createRequestedSession, TSession currentSession,
        GuardianRule rule, DateTimeOffset now, ConcurrentDictionary<string, TSession> sessions)
        where TSession : class
    {
        if (ProtectedTarget.IsScript(path)) return false;
        if (requestedUtc is { } requested)
            return now - requested < InteractiveCloseGracePeriod;

        var requestedSession = createRequestedSession(now);
        if (!sessions.TryUpdate(key, requestedSession, currentSession)) return true;

        var processIds = GetRuleProcessIds(userSid, sessionId, rule);
        SetPending(key, userSid, sessionId, rule, now, null, GuardianProtocol.PendingGracefulClose,
            "Se ha solicitado el cierre normal de la aplicación.", processIds);
        logger.LogInformation("Cierre normal solicitado para {Rule}, sesión {SessionId}; se forzará en {Seconds} s si sigue abierta.",
            rule.Name, sessionId, InteractiveCloseGracePeriod.TotalSeconds);
        return true;
    }

    private IReadOnlyCollection<int> GetRuleProcessIds(string userSid, int sessionId, GuardianRule rule)
    {
        var authorizations = _allowedProcesses.ToArray()
            .Where(allowed => allowed.Value.SessionId == sessionId
                && SidEquals(allowed.Value.UserSid, userSid)
                && PathsEqual(allowed.Value.Path, rule.Path))
            .ToArray();
        var ids = authorizations.Select(allowed => allowed.Key).ToHashSet();
        ids.UnionWith(FindLaunchedDescendantProcessIds(authorizations, sessionId));
        return ids.ToArray();
    }

    private static bool IsAgentRunning(uint sessionId, string expectedPath)
    {
        foreach (var process in Process.GetProcessesByName("ProtectedApp"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited
                        && process.SessionId == sessionId
                        && PathsEqual(process.MainModule?.FileName ?? string.Empty, expectedPath))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    private void StopUnhealthyInteractiveAgents(uint sessionId)
    {
        foreach (var process in Process.GetProcessesByName("ProtectedApp"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.SessionId != sessionId
                        || !PathsEqual(process.MainModule?.FileName ?? string.Empty, options.AppPath))
                        continue;
                    process.Kill(entireProcessTree: true);
                    logger.LogWarning("Interfaz sin pulso finalizada para recuperarla: PID {ProcessId}, sesión {SessionId}.",
                        process.Id, sessionId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo reemplazar la interfaz sin pulso en la sesión {SessionId}.", sessionId);
                }
            }
        }
    }

    private static string ProcessKey(string sid, int sessionId, string path) =>
        $"{sid}|{sessionId}|{Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)}";

    private static bool TryGetPathFromProcessKey(string key, out string path)
    {
        path = string.Empty;
        var separator = key.IndexOf('|');
        if (separator < 0) return false;
        separator = key.IndexOf('|', separator + 1);
        if (separator < 0 || separator == key.Length - 1) return false;
        path = key[(separator + 1)..];
        return true;
    }
    private bool HasTrustedAuthorization(string key, DateTimeOffset now, GuardianRule rule, string userSid)
    {
        if (rule.UnlockGraceMinutes <= 0 || !_trustedUntil.TryGetValue(key, out var trust)) return false;
        var credential = GetEffectiveCredential(userSid, rule);
        var configuredExpiry = trust.GrantedUtc.AddMinutes(rule.UnlockGraceMinutes);
        return trust.ExpiresUtc > now
            && configuredExpiry > now
            && trust.GraceMinutes == rule.UnlockGraceMinutes
            && string.Equals(trust.PasswordHash, credential.Hash, StringComparison.Ordinal)
            && string.Equals(trust.PasswordSalt, credential.Salt, StringComparison.Ordinal);
    }

    private (string? Hash, string? Salt) GetEffectiveCredential(string userSid, GuardianRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.PasswordHash)) return (rule.PasswordHash, rule.PasswordSalt);
        var policy = policyStore.GetPolicy(userSid);
        return (policy?.MasterPasswordHash, policy?.MasterPasswordSalt);
    }
    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static ScheduleDisposition GetScheduleDisposition(GuardianRule rule, DateTimeOffset now) =>
        ProtectionSchedule.GetDisposition(rule.ScheduleEnabled, rule.ScheduleDays,
            rule.ScheduleStartMinutes, rule.ScheduleEndMinutes, rule.BlockOutsideSchedule,
            now.ToLocalTime());
    private static bool SidEquals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string ExtractArguments(string commandLine, string executable)
    {
        var source = commandLine.TrimStart();
        var quoted = $"\"{executable}\"";
        if (source.StartsWith(quoted, StringComparison.OrdinalIgnoreCase)) return source[quoted.Length..].TrimStart();
        if (source.StartsWith(executable, StringComparison.OrdinalIgnoreCase)) return source[executable.Length..].TrimStart();
        var separator = source.IndexOfAny([' ', '\t']);
        return separator < 0 ? string.Empty : source[(separator + 1)..].TrimStart();
    }
    private static bool HasActiveAuthorizedHostProcess(ScriptHostAuthorization authorization)
    {
        var hosts = authorization.HostPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.SessionId != authorization.SessionId
                        || process.StartTime.ToUniversalTime() < authorization.StartUtc.AddSeconds(-1)) continue;
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && hosts.Contains(Path.GetFullPath(path))) return true;
                }
                catch { }
            }
        }
        return false;
    }
    private static GuardianRule CloneRule(GuardianRule rule) => new()
    {
        Id = rule.Id, Name = rule.Name, Path = rule.Path, Category = rule.Category, IsEnabled = rule.IsEnabled,
        PasswordHash = rule.PasswordHash, PasswordSalt = rule.PasswordSalt,
        UnlockGraceMinutes = rule.UnlockGraceMinutes,
        ForceCloseAfterMinutes = rule.ForceCloseAfterMinutes,
        ForceCloseAfterInactivityMinutes = rule.ForceCloseAfterInactivityMinutes,
        ScheduleEnabled = rule.ScheduleEnabled,
        ScheduleDays = rule.ScheduleDays,
        ScheduleStartMinutes = rule.ScheduleStartMinutes,
        ScheduleEndMinutes = rule.ScheduleEndMinutes,
        BlockOutsideSchedule = rule.BlockOutsideSchedule
    };

    private void SetPending(string key, string userSid, int sessionId, GuardianRule rule,
        DateTimeOffset detectedUtc, CapturedLaunch? launchContext = null,
        string kind = GuardianProtocol.PendingAuthentication, string? message = null,
        IReadOnlyCollection<int>? processIds = null)
    {
        var pendingProcessIds = processIds?.Distinct().ToList() ?? [];
        _pending.AddOrUpdate(key,
            _ => new PendingProcess(userSid, sessionId, CloneRule(rule), detectedUtc, launchContext, kind, message, pendingProcessIds),
            (_, existing) =>
            {
                existing.Rule = CloneRule(rule);
                existing.DetectedUtc = detectedUtc;
                if (launchContext is not null) existing.LaunchContext = launchContext;
                existing.Kind = kind;
                existing.Message = message;
                existing.ProcessIds = pendingProcessIds;
                return existing;
            });
    }

    private static string BuildScheduleBlockMessage(GuardianRule rule)
    {
        var start = TimeSpan.FromMinutes(Math.Clamp(rule.ScheduleStartMinutes, 0, 1_439));
        var end = TimeSpan.FromMinutes(Math.Clamp(rule.ScheduleEndMinutes, 0, 1_439));
        return $"La ejecución está bloqueada fuera del horario permitido ({start:hh\\:mm}–{end:hh\\:mm}). Puedes cambiarlo editando la regla en ProtectedApp.";
    }

    private sealed record AllowedProcess(string Path, DateTime StartUtc, string UserSid, int SessionId,
        bool ScheduleBypass);
    private sealed record ProcessCommandLine(string CommandLine, DateTime StartUtc, DateTimeOffset LastSeenUtc);
    private sealed record AgentRecoveryState(int Attempt, DateTimeOffset LastLaunchUtc,
        DateTimeOffset NextAttemptUtc);
    private sealed record SessionIdentity(string Sid, DateTimeOffset ExpiresUtc);
    private sealed record TrustedAuthorization(DateTimeOffset GrantedUtc, DateTimeOffset ExpiresUtc,
        int GraceMinutes, string? PasswordHash, string? PasswordSalt);
    private sealed record TimedSession(DateTimeOffset GrantedUtc, string UserSid, int SessionId,
        Guid RuleId, string Path, string Name, string? PasswordHash, string? PasswordSalt,
        bool WarningIssued = false, DateTimeOffset? GracefulCloseRequestedUtc = null);
    private sealed record InactiveSession(DateTimeOffset LastActivityUtc, string UserSid, int SessionId,
        Guid RuleId, string Path, string Name, string? PasswordHash, string? PasswordSalt,
        bool WarningIssued = false, DateTimeOffset? GracefulCloseRequestedUtc = null);
    private sealed record ScriptHostAuthorization(string UserSid, int SessionId, DateTime StartUtc,
        DateTimeOffset GraceExpiresUtc, string[] HostPaths);
    internal readonly record struct ProcessCloseSummary(int GracefulCloseCount, int ForcedTerminationCount)
    {
        public int TotalCount => GracefulCloseCount + ForcedTerminationCount;
    }
    private sealed record CapturedLaunch(string Executable, string Arguments, string? WorkingDirectory);
    private sealed class PendingProcess(string userSid, int sessionId, GuardianRule rule,
        DateTimeOffset detectedUtc, CapturedLaunch? launchContext = null,
        string kind = GuardianProtocol.PendingAuthentication, string? message = null,
        List<int>? processIds = null)
    {
        public string UserSid { get; } = userSid;
        public int SessionId { get; } = sessionId;
        public GuardianRule Rule { get; set; } = rule;
        public DateTimeOffset DetectedUtc { get; set; } = detectedUtc;
        public DateTimeOffset ClaimedUntilUtc { get; set; }
        public CapturedLaunch? LaunchContext { get; set; } = launchContext;
        public string Kind { get; set; } = kind;
        public string? Message { get; set; } = message;
        public List<int> ProcessIds { get; set; } = processIds ?? [];
    }
}
