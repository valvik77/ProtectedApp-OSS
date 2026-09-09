using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ProtectedApp.Shared;

namespace ProtectedApp.Service;

internal sealed class GuardianIpcServer(
    GuardianOptions options,
    GuardianPolicyStore policyStore,
    GuardianEnforcer enforcer,
    FolderProtectionService folderProtection,
    AuthenticationThrottle throttle,
    ILogger<GuardianIpcServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan InitialRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiagnosticsMinimumInterval = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _clientSlots = new(4, 4);
    private readonly ConcurrentDictionary<string, AuthToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDiagnosticRequests = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = HandleClientAndDisposeAsync(pipe, stoppingToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo aceptar una conexión IPC.");
                await Task.Delay(500, stoppingToken);
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task HandleClientAndDisposeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            try
            {
                using var initialRequestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                initialRequestTimeout.CancelAfter(InitialRequestTimeout);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                // Windows cannot impersonate a named-pipe client until the
                // server has read data from that connection. Gate can connect
                // before its first write reaches us, so reading first also
                // removes a startup race that otherwise leaves the target
                // correctly blocked but without creating a pending prompt.
                var line = await ReadBoundedLineAsync(reader, initialRequestTimeout.Token);
                GuardianResponse response;
                if (line is null || line.Length > GuardianProtocol.MaxMessageCharacters)
                    response = Fail("Solicitud vacía o demasiado grande.");
                else
                {
                    var callerSid = GetCallerSid(pipe);
                    var callerProcessId = GetCallerProcessId(pipe);
                    var callerSessionId = GetProcessSessionId(callerProcessId);
                    var request = JsonSerializer.Deserialize<GuardianRequest>(line, JsonOptions);
                    try
                    {
                        if (request is null || !string.Equals(request.UserSid, callerSid,
                                StringComparison.OrdinalIgnoreCase) || request.SessionId != callerSessionId)
                            response = Fail("Solicitud no válida.");
                        else if (!await _clientSlots.WaitAsync(0, token))
                            response = Fail("Servidor ocupado; inténtalo de nuevo.");
                        else
                        {
                            try { response = HandleRequest(request, callerSid, callerProcessId, callerSessionId); }
                            finally { _clientSlots.Release(); }
                        }
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or UnauthorizedAccessException)
                    {
                        response = Fail(ex.Message);
                    }
                }
                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                responseTimeout.CancelAfter(ClientRequestTimeout);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).WaitAsync(responseTimeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Solicitud IPC agotó el tiempo máximo de lectura o respuesta.");
            }
            catch (Exception ex) { logger.LogWarning(ex, "Solicitud IPC rechazada."); }
        }
    }

    internal static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) return result.Length == 0 ? null : result.ToString();
            var newline = Array.IndexOf(buffer, '\n', 0, read);
            var length = newline < 0 ? read : newline;
            if (result.Length + length > GuardianProtocol.MaxMessageCharacters)
                throw new InvalidDataException("IPC message exceeds the allowed size.");
            result.Append(buffer, 0, length);
            if (newline >= 0) return result.ToString().TrimEnd('\r');
        }
    }

    private GuardianResponse HandleRequest(GuardianRequest request, string callerSid, int callerPid, int callerSessionId)
    {
        CleanupTokens();
        if (!string.Equals(request.UserSid, callerSid, StringComparison.OrdinalIgnoreCase))
            return Fail("La identidad del proceso cliente no coincide.");
        if (request.SessionId != callerSessionId)
            return Fail("La sesión del proceso cliente no coincide.");

        switch (request.Type)
        {
            case GuardianProtocol.Status:
                return new GuardianResponse { Success = true, PolicyConfigured = policyStore.HasPolicy(callerSid) };

            case GuardianProtocol.AgentHeartbeat:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                TamperState.MarkAgentHeartbeat((uint)callerSessionId);
                return Ok();

            case GuardianProtocol.Diagnostics:
                if (!IsTrustedInteractiveAgent(callerPid) || !ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("El diagnóstico requiere una sesión autenticada de ProtectedApp.");
                if (!TryAcquireDiagnosticsSlot(callerSid, callerPid))
                    return Fail("El diagnóstico se ejecutó hace unos segundos; espera antes de repetirlo.");
                var taskDiagnostic = GuardianSystemDiagnostics.CheckHealthTask(
                    Environment.ProcessPath ?? string.Empty, options.AppPath);
                // IPC diagnostics is strictly read-only. Only the service and
                // the SYSTEM supervisor may restore integrity automatically.
                var integrity = GuardianIntegrity.Verify(Environment.ProcessPath ?? string.Empty);
                return new GuardianResponse
                {
                    Success = true,
                    PolicyConfigured = policyStore.HasPolicy(callerSid),
                    GateHealthy = File.Exists(Path.Combine(GuardianConstants.StateFolder, "ProtectedApp.Gate.exe")),
                    HealthTaskHealthy = taskDiagnostic.Healthy,
                    HealthTaskDetail = taskDiagnostic.Detail,
                    IntegrityHealthy = !integrity.Detected || integrity.Repaired,
                    IntegrityDetail = integrity.Detail,
                    AuditTrailHealthy = TamperState.IsAuditTrailValid(),
                    SafeRecoveryActive = File.Exists(GuardianConstants.SafeRecoveryPath),
                    SignatureIdentityConfigured = File.Exists(GuardianConstants.SignerIdentityPath),
                    InstalledProtectionVersion = GuardianSystemDiagnostics.GetInstalledProtectionVersion()
                };

            case GuardianProtocol.BootstrapPolicy:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (request.Policy is null) return Fail("La política inicial no es válida.");
                if (!policyStore.ValidateBootstrapSecret(request.Token))
                    return Fail("La autorización de instalación no es válida.");
                request.Policy.UserSid = callerSid;
                // The installer deliberately preserves the existing policy and
                // folder ACL recovery data until this replacement succeeds.
                // ApplyPolicyTransactional restores the prior policy if any
                // part of synchronization fails.
                ApplyPolicyTransactional(request.Policy, callerSessionId);
                policyStore.ClearBootstrapSecret();
                logger.LogInformation("Política inicial creada para {Sid} con {Count} aplicaciones y {FolderCount} carpetas.",
                    callerSid, request.Policy.Rules.Count, request.Policy.FolderRules.Count);
                return Ok();

            case GuardianProtocol.AuthenticateMaster:
            case GuardianProtocol.RecoverPolicy:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                var tokenPolicy = policyStore.GetPolicy(callerSid);
                if (request.Type == GuardianProtocol.RecoverPolicy
                    && !string.IsNullOrWhiteSpace(request.Token)
                    && ValidateToken(request.Token, callerSid, callerPid))
                {
                    if (tokenPolicy is null) return Fail("No existe una política protegida para este usuario.");
                    return new GuardianResponse { Success = true, Token = request.Token, Policy = tokenPolicy };
                }
                var masterThrottleKey = MasterThrottleKey(callerSid);
                var masterThrottle = throttle.Check(masterThrottleKey);
                if (masterThrottle.LockoutEnded)
                    logger.LogInformation("Bloqueo temporal de contraseña maestra finalizado para {Sid}.", callerSid);
                if (masterThrottle.IsLimited) return RateLimited(masterThrottle);
                var authPolicy = policyStore.GetPolicy(callerSid);
                if (authPolicy is null) return Fail("No existe una política para este usuario.");
                if (!GuardianPassword.Verify(request.Password, authPolicy.MasterPasswordHash, authPolicy.MasterPasswordSalt))
                {
                    var failure = throttle.RegisterFailure(masterThrottleKey);
                    logger.LogWarning("Contraseña maestra incorrecta para {Sid}, operación {Operation}.", callerSid, request.Type);
                    if (failure.LockoutStarted)
                        logger.LogWarning("Bloqueo temporal de contraseña maestra iniciado para {Sid}: {Seconds} segundos tras {Count} fallos.",
                            callerSid, failure.RetryAfterSeconds, failure.FailureCount);
                    return Rejected("Contraseña maestra incorrecta.", failure, masterThrottle.LockoutEnded);
                }
                throttle.Clear(masterThrottleKey);
                var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                _tokens[token] = new AuthToken(callerSid, callerPid, DateTimeOffset.UtcNow.AddHours(8),
                    ProcessStartedUtc: GetProcessStartedUtc(callerPid));
                return new GuardianResponse
                {
                    Success = true,
                    LockoutEnded = masterThrottle.LockoutEnded,
                    Token = token,
                    Policy = request.Type == GuardianProtocol.RecoverPolicy ? authPolicy : null
                };

            case GuardianProtocol.SyncPolicy:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (!ValidateToken(request.Token, callerSid, callerPid) || request.Policy is null)
                    return Fail("Autorización de administración no válida.");
                request.Policy.UserSid = callerSid;
                ApplyPolicyTransactional(request.Policy, callerSessionId);
                logger.LogInformation("Política actualizada para {Sid} con {Count} aplicaciones y {FolderCount} carpetas.",
                    callerSid, request.Policy.Rules.Count, request.Policy.FolderRules.Count);
                return Ok();

            case GuardianProtocol.GetTamperWebhook:
                if (!IsTrustedInteractiveAgent(callerPid) || !ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("Autorización de administración no válida.");
                var webhookStatus = TamperWebhookNotifier.GetStatus();
                return new GuardianResponse
                {
                    Success = true,
                    WebhookEnabled = webhookStatus.Enabled,
                    WebhookUrl = webhookStatus.Url,
                    WebhookUseHmac = webhookStatus.UseHmac,
                    WebhookInstallationId = webhookStatus.InstallationId
                };

            case GuardianProtocol.ConfigureTamperWebhook:
                if (!IsTrustedInteractiveAgent(callerPid) || !ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("Autorización de administración no válida.");
                TamperWebhookNotifier.Configure(request.WebhookEnabled, request.WebhookUrl, request.WebhookUseHmac, request.WebhookSecret);
                return Ok();

            case GuardianProtocol.LockAll:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (!ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("Autorización de administración no válida.");
                var affected = enforcer.RevokeAllAndTerminate(callerSid, callerSessionId, requestGracefulClose: true);
                var lockedFolders = folderProtection.LockAll(callerSid);
                logger.LogWarning("Bloqueo inmediato solicitado para {Sid}, sesión {SessionId}: {Graceful} cierres normales y {Forced} forzados.",
                    callerSid, callerSessionId, affected.GracefulCloseCount, affected.ForcedTerminationCount);
                return new GuardianResponse
                {
                    Success = true,
                    AffectedProcessCount = affected.TotalCount,
                    GracefulCloseCount = affected.GracefulCloseCount,
                    ForcedTerminationCount = affected.ForcedTerminationCount,
                    AffectedFolderCount = lockedFolders
                };

            case GuardianProtocol.EmergencyLock:
                // Emergency lock is deliberately one-way: a verified agent can
                // revoke access without holding a reusable administration token.
                // It cannot unlock, change policy or disclose protected data.
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                var emergencyAffected = enforcer.RevokeAllAndTerminate(callerSid, callerSessionId, requestGracefulClose: true);
                var emergencyLockedFolders = folderProtection.LockAll(callerSid);
                logger.LogWarning("Bloqueo inmediato por atajo para {Sid}, sesión {SessionId}: {Graceful} cierres normales y {Forced} forzados.",
                    callerSid, callerSessionId, emergencyAffected.GracefulCloseCount, emergencyAffected.ForcedTerminationCount);
                return new GuardianResponse
                {
                    Success = true,
                    AffectedProcessCount = emergencyAffected.TotalCount,
                    GracefulCloseCount = emergencyAffected.GracefulCloseCount,
                    ForcedTerminationCount = emergencyAffected.ForcedTerminationCount,
                    AffectedFolderCount = emergencyLockedFolders
                };

            case GuardianProtocol.LockRule:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (!ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("Autorización de administración no válida.");
                if (request.RuleId is null) return Fail("Falta el identificador de regla.");
                var selectivelyAffected = enforcer.RevokeRuleAndTerminate(
                    callerSid, callerSessionId, request.RuleId.Value);
                if (selectivelyAffected is null) return Fail("La regla protegida no existe.");
                logger.LogWarning(
                    "Bloqueo selectivo solicitado para {Sid}, sesión {SessionId}, regla {RuleId}: {Count} procesos finalizados.",
                    callerSid, callerSessionId, request.RuleId.Value, selectivelyAffected.Value);
                return new GuardianResponse
                {
                    Success = true,
                    AffectedProcessCount = selectivelyAffected.Value
                };

            case GuardianProtocol.EndApplicationSession:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (request.RuleId is null) return Fail("Falta el identificador de regla.");
                var endedProcessCount = enforcer.EndApplicationSession(
                    callerSid, callerSessionId, request.RuleId.Value);
                if (endedProcessCount is null) return Fail("La regla protegida no existe.");
                return new GuardianResponse
                {
                    Success = true,
                    AffectedProcessCount = endedProcessCount.Value
                };

            case GuardianProtocol.RegisterBlockedAttempt:
                if (!IsTrustedGate(callerPid)) return Fail("El origen del bloqueo no es válido.");
                if (string.IsNullOrWhiteSpace(request.TargetPath)) return Fail("Falta la ruta bloqueada.");
                return enforcer.RegisterBlockedAttempt(callerSid, callerSessionId, request.TargetPath)
                    ? Ok()
                    : Fail("El destino no pertenece a una regla activa.");

            case GuardianProtocol.LockFolder:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (!ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("Autorización de administración no válida.");
                if (request.RuleId is null) return Fail("Falta el identificador de carpeta.");
                var lockPolicy = policyStore.GetPolicy(callerSid);
                if (lockPolicy?.FolderRules.All(rule => rule.Id != request.RuleId.Value || !rule.IsEnabled) != false)
                    return Fail("La carpeta protegida no existe o está desactivada.");
                folderProtection.Lock(callerSid, request.RuleId.Value);
                return Ok();

            case GuardianProtocol.RestoreOrphanedFolderLock:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (!ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("Autorización de administración no válida.");
                if (string.IsNullOrWhiteSpace(request.TargetPath)) return Fail("Falta la ruta de carpeta.");
                folderProtection.RestoreOrphanedGuardianLock(callerSid, request.TargetPath);
                logger.LogWarning("Se restauró un bloqueo de carpeta huérfano para {Sid}: {Folder}.", callerSid, request.TargetPath);
                return Ok();

            case GuardianProtocol.UnlockFolder:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (request.RuleId is null) return Fail("Falta el identificador de carpeta.");
                var folderPolicy = policyStore.GetPolicy(callerSid);
                var folderRule = folderPolicy?.FolderRules.FirstOrDefault(item =>
                    item.Id == request.RuleId.Value && item.IsEnabled);
                if (folderPolicy is null || folderRule is null)
                    return Fail("La carpeta protegida no existe o está desactivada.");
                var folderThrottleKey = FolderThrottleKey(callerSid, folderRule.Id);
                var folderThrottle = throttle.Check(folderThrottleKey);
                if (folderThrottle.IsLimited) return RateLimited(folderThrottle);
                var folderTokenValid = !string.IsNullOrWhiteSpace(request.Token)
                    && ValidateToken(request.Token, callerSid, callerPid);
                var folderHash = string.IsNullOrWhiteSpace(folderRule.PasswordHash)
                    ? folderPolicy.MasterPasswordHash : folderRule.PasswordHash;
                var folderSalt = string.IsNullOrWhiteSpace(folderRule.PasswordHash)
                    ? folderPolicy.MasterPasswordSalt : folderRule.PasswordSalt;
                if (!folderTokenValid && !GuardianPassword.Verify(request.Password, folderHash, folderSalt))
                {
                    var failure = throttle.RegisterFailure(folderThrottleKey);
                    logger.LogWarning("Contraseña incorrecta para la carpeta {Folder}, SID {Sid}.", folderRule.Name, callerSid);
                    return Rejected("Contraseña incorrecta.", failure, folderThrottle.LockoutEnded);
                }
                throttle.Clear(folderThrottleKey);
                var unlockedUntil = folderProtection.Unlock(callerSid, folderRule);
                return new GuardianResponse
                {
                    Success = true,
                    PasswordAccepted = true,
                    LockoutEnded = folderThrottle.LockoutEnded,
                    UnlockedUntilUtc = unlockedUntil
                };

            case GuardianProtocol.PrepareUninstall:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (!ValidateToken(request.Token, callerSid, callerPid))
                    return Fail("Autorización de administración no válida.");
                folderProtection.PrepareForUninstall();
                logger.LogWarning("Desinstalación autorizada: permisos originales de carpetas restaurados para {Sid}.", callerSid);
                return Ok();

            case GuardianProtocol.RegisterHostAttempt:
                if (!IsTrustedGate(callerPid)) return Fail("El origen del intérprete no es válido.");
                if (string.IsNullOrWhiteSpace(request.HostPath)) return Fail("Falta la ruta del intérprete.");
                return enforcer.RegisterHostAttempt(callerSid, callerSessionId, request.HostPath,
                    request.HostArguments ?? string.Empty, request.WorkingDirectory, out var hostError)
                    ? Ok()
                    : Fail(hostError ?? "No se pudo procesar el intérprete.");

            case GuardianProtocol.ClaimPending:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                return new GuardianResponse
                {
                    Success = true,
                    Pending = enforcer.ClaimPending(callerSid, callerSessionId)
                };

            case GuardianProtocol.DismissPending:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (request.RuleId is null) return Fail("Falta el identificador de regla.");
                enforcer.DismissPending(callerSid, callerSessionId, request.RuleId.Value);
                return Ok();

            case GuardianProtocol.AuthorizeAndLaunch:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (request.RuleId is null) return Fail("Falta el identificador de regla.");
                var policy = policyStore.GetPolicy(callerSid);
                var rule = policy?.Rules.FirstOrDefault(item => item.Id == request.RuleId.Value && item.IsEnabled);
                if (policy is null || rule is null) return Fail("La regla ya no está disponible.");
                var ruleThrottleKey = RuleThrottleKey(callerSid, rule.Id);
                var ruleThrottle = throttle.Check(ruleThrottleKey);
                if (ruleThrottle.LockoutEnded)
                    logger.LogInformation("Bloqueo temporal finalizado para la regla {Rule}, SID {Sid}.", rule.Name, callerSid);
                if (ruleThrottle.IsLimited) return RateLimited(ruleThrottle);
                var isTokenValid = !string.IsNullOrWhiteSpace(request.Token) && ValidateToken(request.Token, callerSid, callerPid);
                var hash = string.IsNullOrWhiteSpace(rule.PasswordHash) ? policy.MasterPasswordHash : rule.PasswordHash;
                var salt = string.IsNullOrWhiteSpace(rule.PasswordHash) ? policy.MasterPasswordSalt : rule.PasswordSalt;
                var isPasswordValid = GuardianPassword.Verify(request.Password, hash, salt);
                if (!isTokenValid && !isPasswordValid)
                {
                    var failure = throttle.RegisterFailure(ruleThrottleKey);
                    logger.LogWarning("Contraseña incorrecta para la regla {Rule}, SID {Sid}.", rule.Name, callerSid);
                    if (failure.LockoutStarted)
                        logger.LogWarning("Bloqueo temporal iniciado para la regla {Rule}, SID {Sid}: {Seconds} segundos tras {Count} fallos.",
                            rule.Name, callerSid, failure.RetryAfterSeconds, failure.FailureCount);
                    return Rejected("Contraseña incorrecta.", failure, ruleThrottle.LockoutEnded);
                }
                throttle.Clear(ruleThrottleKey);
                if (enforcer.LaunchAuthorized(callerSid, callerSessionId, rule, out var processId, out var error))
                {
                    var closeMinutes = Math.Max(rule.ForceCloseAfterMinutes, rule.ForceCloseAfterInactivityMinutes);
                    var timedSessionToken = closeMinutes > 0
                        ? CreateTimedSessionToken(callerSid, callerPid, rule.Id, closeMinutes)
                        : null;
                    return new GuardianResponse { Success = true, PasswordAccepted = true, ProcessId = processId,
                        TimedSessionToken = timedSessionToken,
                        LockoutEnded = ruleThrottle.LockoutEnded };
                }
                return new GuardianResponse { PasswordAccepted = true, Error = error ?? "No se pudo iniciar la aplicación.",
                        LockoutEnded = ruleThrottle.LockoutEnded };

            case GuardianProtocol.ExtendTimedSession:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (request.RuleId is null)
                    return Fail("Falta el identificador de regla.");
                var hasTimedSessionAuthorization = ValidateTimedSessionToken(request.TimedSessionToken, callerSid,
                    callerPid, request.RuleId.Value);
                var hasMasterAuthorization = ValidateToken(request.Token, callerSid, callerPid);
                if (!hasTimedSessionAuthorization && !hasMasterAuthorization)
                    return Fail("La autorización para ampliar esta sesión ya no es válida.");
                if (!enforcer.ExtendTimedSession(callerSid, callerSessionId, request.RuleId.Value, out var extensionError))
                    return Fail(extensionError ?? "No se pudo ampliar el cierre automático.");
                var extendedPolicy = policyStore.GetPolicy(callerSid);
                var extendedRule = extendedPolicy?.Rules.FirstOrDefault(item => item.Id == request.RuleId.Value);
                return new GuardianResponse
                {
                    Success = true,
                    TimedSessionToken = CreateTimedSessionToken(callerSid, callerPid, request.RuleId.Value,
                        extendedRule?.ForceCloseAfterMinutes ?? 1)
                };

            case GuardianProtocol.ReportApplicationActivity:
                if (request.RuleId is null) return Fail("Falta el identificador de regla.");
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen del pulso de actividad no es válido.");
                if (!enforcer.RecordApplicationActivity(callerSid, callerSessionId, request.RuleId.Value))
                    return Fail("La aplicación no tiene una sesión de inactividad activa.");
                var activityPolicy = policyStore.GetPolicy(callerSid);
                var activityRule = activityPolicy?.Rules.FirstOrDefault(item => item.Id == request.RuleId.Value);
                return new GuardianResponse
                {
                    Success = true,
                    TimedSessionToken = CreateTimedSessionToken(callerSid, callerPid, request.RuleId.Value,
                        activityRule?.ForceCloseAfterInactivityMinutes ?? 1)
                };

            case GuardianProtocol.ExtendInactiveSession:
                if (!IsTrustedInteractiveAgent(callerPid)) return Fail("El origen de la solicitud no es válido.");
                if (request.RuleId is null) return Fail("Falta el identificador de regla.");
                var hasInactiveSessionAuthorization = ValidateTimedSessionToken(request.TimedSessionToken, callerSid,
                    callerPid, request.RuleId.Value);
                var hasInactiveMasterAuthorization = ValidateToken(request.Token, callerSid, callerPid);
                if (!hasInactiveSessionAuthorization && !hasInactiveMasterAuthorization)
                    return Fail("La autorización para ampliar esta sesión ya no es válida.");
                if (!enforcer.ExtendInactiveSession(callerSid, callerSessionId, request.RuleId.Value, out var inactiveExtensionError))
                    return Fail(inactiveExtensionError ?? "No se pudo ampliar el cierre por inactividad.");
                var inactivePolicy = policyStore.GetPolicy(callerSid);
                var inactiveRule = inactivePolicy?.Rules.FirstOrDefault(item => item.Id == request.RuleId.Value);
                return new GuardianResponse
                {
                    Success = true,
                    TimedSessionToken = CreateTimedSessionToken(callerSid, callerPid, request.RuleId.Value,
                        inactiveRule?.ForceCloseAfterInactivityMinutes ?? 1)
                };

            default:
                return Fail("Operación desconocida.");
        }
    }

    private void ApplyPolicyTransactional(GuardianPolicy policy, int sessionId)
    {
        var previous = policyStore.GetPolicy(policy.UserSid);
        var candidatePolicies = policyStore.GetPolicies()
            .Where(existing => !string.Equals(existing.UserSid, policy.UserSid, StringComparison.OrdinalIgnoreCase))
            .Append(policy)
            .ToArray();
        folderProtection.ValidatePolicy(candidatePolicies);
        try
        {
            policyStore.SetPolicy(policy);
            enforcer.RevokeDisabledRuleAuthorizations(policy.UserSid);
            folderProtection.Synchronize(policyStore.GetPolicies());
            enforcer.SynchronizeExecutionGates();
            enforcer.SynchronizeTimedSessions(policy.UserSid, sessionId);
        }
        catch
        {
            if (previous is null) policyStore.RemovePolicy(policy.UserSid);
            else policyStore.SetPolicy(previous);
            try { folderProtection.Synchronize(policyStore.GetPolicies()); } catch { }
            enforcer.SynchronizeExecutionGates();
            throw;
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User
            ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(GuardianProtocol.PipeName, PipeDirection.InOut, 8,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private static string GetCallerSid(NamedPipeServerStream pipe)
    {
        string? sid = null;
        pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
        return sid ?? throw new UnauthorizedAccessException("No se pudo identificar al cliente.");
    }

    private static int GetCallerProcessId(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return checked((int)pid);
    }

    private static int GetProcessSessionId(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.SessionId;
    }

    private bool ValidateToken(string? token, string sid, int processId) =>
        token is not null && _tokens.TryGetValue(token, out var auth)
        && IsTokenValid(auth, sid, processId, DateTimeOffset.UtcNow, GetProcessStartedUtc(processId));

    internal static bool IsTokenValid(AuthToken? auth, string sid, int processId, DateTimeOffset now) =>
        auth is not null && auth.ExpiresUtc > now && auth.ProcessId == processId
        && string.Equals(auth.UserSid, sid, StringComparison.OrdinalIgnoreCase);

    private static bool IsTokenValid(AuthToken? auth, string sid, int processId, DateTimeOffset now,
        DateTime processStartedUtc) =>
        IsTokenValid(auth, sid, processId, now)
        && (auth!.ProcessStartedUtc is null || auth.ProcessStartedUtc == processStartedUtc);

    private static DateTime GetProcessStartedUtc(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime();
    }

    private bool TryAcquireDiagnosticsSlot(string sid, int processId)
    {
        var key = $"{sid}|{processId}";
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            if (!_lastDiagnosticRequests.TryGetValue(key, out var previous))
            {
                if (_lastDiagnosticRequests.TryAdd(key, now)) return true;
                continue;
            }
            if (now - previous < DiagnosticsMinimumInterval) return false;
            if (_lastDiagnosticRequests.TryUpdate(key, now, previous)) return true;
        }
    }

    private bool IsTrustedInteractiveAgent(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(path)
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(options.AppPath),
                    StringComparison.OrdinalIgnoreCase)
                && GuardianAgentIdentity.Matches(path);
        }
        catch { return false; }
    }

    private static bool IsTrustedGate(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            var installedGate = Path.Combine(GuardianConstants.StateFolder, "ProtectedApp.Gate.exe");
            return !string.IsNullOrWhiteSpace(path)
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(installedGate),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private string CreateTimedSessionToken(string sid, int processId, Guid ruleId, int closeAfterMinutes)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new AuthToken(sid, processId,
            DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, closeAfterMinutes) + 1), ruleId,
            GetProcessStartedUtc(processId));
        return token;
    }

    private bool ValidateTimedSessionToken(string? token, string sid, int processId, Guid ruleId) =>
        token is not null && _tokens.TryGetValue(token, out var auth) && auth.ExpiresUtc > DateTimeOffset.UtcNow
        && auth.ProcessId == processId && auth.TimedRuleId == ruleId
        && (auth.ProcessStartedUtc is null || auth.ProcessStartedUtc == GetProcessStartedUtc(processId))
        && string.Equals(auth.UserSid, sid, StringComparison.OrdinalIgnoreCase);

    private void CleanupTokens()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var token in _tokens)
            if (token.Value.ExpiresUtc <= now) _tokens.TryRemove(token.Key, out _);
        foreach (var diagnostic in _lastDiagnosticRequests)
            if (now - diagnostic.Value > TimeSpan.FromMinutes(2))
                _lastDiagnosticRequests.TryRemove(diagnostic.Key, out _);
    }

    private static string MasterThrottleKey(string sid) => $"{sid}|master";
    private static string RuleThrottleKey(string sid, Guid ruleId) => $"{sid}|rule|{ruleId:N}";
    private static string FolderThrottleKey(string sid, Guid ruleId) => $"{sid}|folder|{ruleId:N}";
    private static GuardianResponse RateLimited(ThrottleDecision decision) => new()
    {
        Error = "Demasiados intentos. Espera antes de volver a intentarlo.",
        RateLimited = true,
        RetryAfterSeconds = decision.RetryAfterSeconds,
        FailureCount = decision.FailureCount,
        LockoutEnded = decision.LockoutEnded
    };
    private static GuardianResponse Rejected(string error, ThrottleDecision decision, bool lockoutEnded) => new()
    {
        PasswordRejected = true,
        Error = error,
        RateLimited = decision.IsLimited,
        RetryAfterSeconds = decision.RetryAfterSeconds,
        FailureCount = decision.FailureCount,
        LockoutStarted = decision.LockoutStarted,
        LockoutEnded = lockoutEnded
    };
    private static GuardianResponse Ok() => new() { Success = true };
    private static GuardianResponse Fail(string error) => new() { Success = false, Error = error };

    internal sealed record AuthToken(string UserSid, int ProcessId, DateTimeOffset ExpiresUtc,
        Guid? TimedRuleId = null, DateTime? ProcessStartedUtc = null);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
