using System.Diagnostics;

namespace ProtectedApp.Service;

internal sealed class GuardianWorker(
    GuardianOptions options,
    ILogger<GuardianWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var leaseId = TamperState.BeginServiceLease(logger);
        var nextConfigurationCheck = DateTimeOffset.MinValue;
        logger.LogInformation("ProtectedApp Guardian iniciado. Agente: {AppPath}", options.AppPath);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTimeOffset.UtcNow >= nextConfigurationCheck)
                    {
                        nextConfigurationCheck = DateTimeOffset.UtcNow.AddSeconds(10);
                        if (!TamperState.IsMaintenanceActive()
                            && ServiceConfiguration.EnsureExpectedConfiguration(Environment.ProcessPath, options.AppPath))
                            TamperState.SignalTamper("La configuración de Guardian fue modificada y el propio servicio restauró su inicio, binario o recuperación automática.", logger);
                    }

                    var sessionId = InteractiveProcessLauncher.GetActiveSessionId();
                    if (sessionId is not null && File.Exists(options.AppPath) && GuardianAgentIdentity.Matches(options.AppPath)
                        && !IsAgentRunning(sessionId.Value, options.AppPath))
                    {
                        if (InteractiveProcessLauncher.StartInSession(options.AppPath, "--background --service-managed", sessionId.Value, out var processId, out var error))
                            logger.LogInformation("Agente iniciado en la sesión {SessionId}, PID {ProcessId}.", sessionId, processId);
                        else
                            logger.LogWarning("No se pudo iniciar el agente en la sesión {SessionId}. Error Win32: {Error}.", sessionId, error);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error al supervisar ProtectedApp.");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        finally { TamperState.EndServiceLease(leaseId); }
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
                        && string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                            Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }
}
