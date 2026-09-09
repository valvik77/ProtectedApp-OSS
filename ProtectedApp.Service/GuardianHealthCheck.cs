using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProtectedApp.Shared;

namespace ProtectedApp.Service;

internal static class GuardianHealthCheck
{
    private static readonly DateTimeOffset SupervisorStartedUtc = DateTimeOffset.UtcNow;
    private static readonly TimeSpan StartupRecoveryGrace = TimeSpan.FromMinutes(2);

    public static async Task RunContinuouslyAsync(string appPath)
    {
        var nextIntegrityCheck = DateTimeOffset.MinValue;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var verifyIntegrity = now >= nextIntegrityCheck;
            if (verifyIntegrity) nextIntegrityCheck = now.AddMinutes(1);
            await RunAsync(appPath, verifyIntegrity);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    public static Task RunAsync(string appPath) => RunAsync(appPath, verifyIntegrity: true);

    private static async Task RunAsync(string appPath, bool verifyIntegrity)
    {
        if (TamperState.IsMaintenanceActive()) return;

        try
        {
            if (verifyIntegrity)
            {
                var integrity = GuardianIntegrity.VerifyAndRepair(Environment.ProcessPath ?? string.Empty);
                if (integrity.Detected)
                {
                    if (integrity.Repaired) TamperState.ExitSafeRecovery();
                    else
                    {
                        RestoreEmergencyProtection();
                        TamperState.EnterSafeRecovery(
                            "El supervisor SYSTEM no pudo restaurar un componente de Guardian y mantiene las puertas preventivas activas.");
                    }
                    TamperState.SignalTamper(integrity.Repaired
                        ? $"El supervisor SYSTEM detectó y restauró binarios modificados de Guardian: {integrity.Detail}"
                        : $"El supervisor SYSTEM detectó binarios modificados de Guardian que no pudo restaurar: {integrity.Detail}",
                        code: integrity.Repaired ? TamperEventCode.TamperDetected : TamperEventCode.RecoveryFailed);
                }
                if (!TamperState.IsAuditTrailValid())
                {
                    RestoreEmergencyProtection();
                    TamperState.EnterSafeRecovery(
                        "El registro de seguridad de Guardian presenta una discontinuidad y el supervisor mantiene las puertas preventivas activas.");
                }
            }

            if (ServiceConfiguration.EnsureExpectedConfiguration(Environment.ProcessPath, appPath))
                TamperState.SignalTamper("La configuración de Guardian fue modificada y el supervisor SYSTEM restauró su inicio, binario o recuperación automática.");

            using var service = new ServiceController(GuardianConstants.ServiceName);
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Running)
            {
                TryClearIncident();
                return;
            }

            // Automatic services can still be starting when the SYSTEM task
            // begins. In particular, Fast Startup preserves TickCount across
            // sign-ins, so system uptime cannot be used to classify this as a
            // stopped/tampered service. Give SCM a chance to finish first.
            if (service.Status == ServiceControllerStatus.StartPending)
            {
                try { service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
                catch { }
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Running)
                {
                    TryClearIncident();
                    return;
                }
            }

            // Authorization can temporarily remove an IFEO debugger while a
            // complex application is alive. The independent SYSTEM supervisor
            // must restore those gates before attempting service recovery.
            var terminatedProcesses = RestoreEmergencyProtection();

            // En el arranque de Windows el supervisor puede iniciarse antes
            // que el servicio automático. Recuperamos Guardian de inmediato,
            // pero evitamos bloquear una segunda vez la sesión recién abierta.
            var startupGrace = DateTimeOffset.UtcNow - SupervisorStartedUtc < StartupRecoveryGrace;
            var firstAttempt = !File.Exists(GuardianConstants.IncidentPath);
            if (firstAttempt && !startupGrace)
            {
                if (HasConfiguredGuardianPolicy())
                {
                    Directory.CreateDirectory(GuardianConstants.StateFolder);
                    File.WriteAllText(GuardianConstants.IncidentPath, DateTimeOffset.UtcNow.ToString("O"));
                    var terminationDetail = terminatedProcesses > 0
                        ? $" y finalizó {terminatedProcesses} proceso(s) protegido(s)"
                        : string.Empty;
                    TamperState.SignalTamper($"El servicio Guardian dejó de ejecutarse; la comprobación SYSTEM rearmó las puertas{terminationDetail} y solicitó su recuperación.");
                    TryLockInteractiveSessionOncePerBoot();
                }
            }

            if (service.Status == ServiceControllerStatus.StopPending)
            {
                try { service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(3)); }
                catch { }
                service.Refresh();
            }

            if (TamperState.IsMaintenanceActive() || service.Status != ServiceControllerStatus.Stopped) return;
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            TryClearIncident();
            if (!startupGrace)
                TamperState.WriteRecoveryEvent("El servicio Guardian se reinició automáticamente con las puertas preventivas restauradas.");
        }
        catch (InvalidOperationException)
        {
            // Authorized uninstall/update keeps the maintenance flag active.
            // Outside maintenance, a missing service must still fail closed
            // even though this watchdog deliberately does not recreate it.
            if (TamperState.IsMaintenanceActive()) return;
            var terminatedProcesses = RestoreEmergencyProtection();
            if (!File.Exists(GuardianConstants.IncidentPath) && HasConfiguredGuardianPolicy())
            {
                Directory.CreateDirectory(GuardianConstants.StateFolder);
                File.WriteAllText(GuardianConstants.IncidentPath, DateTimeOffset.UtcNow.ToString("O"));
                TamperState.SignalTamper(terminatedProcesses > 0
                    ? $"El servicio Guardian fue eliminado; se rearmaron las puertas y se finalizaron {terminatedProcesses} proceso(s) protegido(s)."
                    : "El servicio Guardian fue eliminado; las puertas permanecen rearmadas en modo seguro.");
                TryLockInteractiveSessionOncePerBoot();
            }
        }
        catch (Exception ex)
        {
            if (!File.Exists(GuardianConstants.IncidentPath) && HasConfiguredGuardianPolicy())
            {
                Directory.CreateDirectory(GuardianConstants.StateFolder);
                File.WriteAllText(GuardianConstants.IncidentPath, DateTimeOffset.UtcNow.ToString("O"));
                TamperState.SignalTamper($"No se pudo recuperar el servicio Guardian: {ex.Message}", code: TamperEventCode.RecoveryFailed);
                TryLockInteractiveSessionOncePerBoot();
            }
            await Task.CompletedTask;
        }
    }

    private static void TryClearIncident()
    {
        try { File.Delete(GuardianConstants.IncidentPath); }
        catch { }
    }

    private static int RestoreEmergencyProtection()
    {
        try
        {
            var store = new GuardianPolicyStore();
            var policies = store.GetPolicies();
            var gate = new ExecutionGateManager(
                new GuardianOptions(string.Empty, DiagnosticMode: false),
                NullLogger<ExecutionGateManager>.Instance);
            gate.Synchronize(policies);

            var rules = policies.SelectMany(policy => policy.Rules)
                .Where(rule => rule.IsEnabled)
                .ToArray();
            var terminated = 0;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id is 0 or 4 || process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        var path = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        var protectedExecutable = rules.Any(rule =>
                            !ProtectedTarget.IsScript(rule.Path) && PathsEqual(rule.Path, path));
                        var protectedScript = false;
                        if (!protectedExecutable
                            && ProtectedTarget.IsPotentialScriptHost(path)
                            && TryReadCommandLine(process.Id, out var commandLine))
                        {
                            protectedScript = rules.Any(rule => ProtectedTarget.IsScript(rule.Path)
                                && ProtectedTarget.CommandLineReferences(commandLine, rule.Path));
                        }
                        if (!protectedExecutable && !protectedScript) continue;
                        process.Kill(entireProcessTree: true);
                        terminated++;
                    }
                    catch { }
                }
            }
            return terminated;
        }
        catch (Exception ex)
        {
            TamperState.SignalTamper($"No se pudieron rearmar las puertas durante la recuperación: {ex.Message}", code: TamperEventCode.RecoveryFailed);
            return 0;
        }
    }

    private static bool HasConfiguredGuardianPolicy()
    {
        try
        {
            var authPolicies = new GuardianPolicyStore().GetPolicies();
            return authPolicies.Any(policy =>
                policy.Rules.Any(rule => rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.Path))
                || policy.FolderRules.Any(rule => rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.Path)));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadCommandLine(int processId, out string commandLine)
    {
        commandLine = string.Empty;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                commandLine = Convert.ToString(item["CommandLine"]) ?? string.Empty;
                return commandLine.Length > 0;
            }
        }
        catch { }
        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryLockInteractiveSessionOncePerBoot()
    {
        try
        {
            var sessionId = InteractiveProcessLauncher.GetActiveSessionId();
            if (sessionId is null) return;

            var bootUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var markerPath = Path.Combine(GuardianConstants.StateFolder, "guardian-boot-lock.json");
            Directory.CreateDirectory(GuardianConstants.StateFolder);

            using var mutex = new Mutex(false, @"Global\ProtectedAppGuardian.LockInteractiveSession");
            var lockTaken = false;
            try
            {
                try { lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { lockTaken = true; }

                if (File.Exists(markerPath))
                {
                    try
                    {
                        var previous = JsonSerializer.Deserialize<BootLockMarker>(File.ReadAllText(markerPath));
                        if (previous is not null && Math.Abs((previous.BootUtc - bootUtc).TotalMinutes) < 2)
                            return;
                    }
                    catch { }
                }

                File.WriteAllText(markerPath, JsonSerializer.Serialize(new BootLockMarker(bootUtc, DateTimeOffset.UtcNow)));
                LockInteractiveSession();
            }
            finally
            {
                if (lockTaken) mutex.ReleaseMutex();
            }
        }
        catch { }
    }

    private static void LockInteractiveSession()
    {
        try
        {
            var sessionId = InteractiveProcessLauncher.GetActiveSessionId();
            if (sessionId is null) return;
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
            InteractiveProcessLauncher.StartInSession(executable, "user32.dll,LockWorkStation", sessionId.Value,
                out _, out _);
        }
        catch { }
    }

    private sealed record BootLockMarker(DateTimeOffset BootUtc, DateTimeOffset TriggeredUtc);
}
