namespace ProtectedApp.Services;

public enum VaultSecurityTrigger
{
    SessionLocked,
    SessionDisconnected,
    UserSigningOut,
    SystemSuspending,
    SystemShuttingDown
}

public static class WindowsSecurityMessageRouter
{
    public const uint SessionChangeMessage = 0x02B1;
    public const uint PowerBroadcastMessage = 0x0218;
    public const uint QueryEndSessionMessage = 0x0011;
    public const uint EndSessionMessage = 0x0016;

    public static bool TryResolve(uint message, nuint parameter, out VaultSecurityTrigger trigger)
    {
        trigger = default;
        if (message == SessionChangeMessage)
        {
            trigger = parameter switch
            {
                2 or 4 => VaultSecurityTrigger.SessionDisconnected,
                6 => VaultSecurityTrigger.UserSigningOut,
                7 => VaultSecurityTrigger.SessionLocked,
                _ => default
            };
            return parameter is 2 or 4 or 6 or 7;
        }
        if (message == PowerBroadcastMessage && parameter is 4 or 5)
        {
            trigger = VaultSecurityTrigger.SystemSuspending;
            return true;
        }
        if (message == QueryEndSessionMessage || message == EndSessionMessage && parameter != 0)
        {
            trigger = VaultSecurityTrigger.SystemShuttingDown;
            return true;
        }
        return false;
    }

    public static string GetActivityLabel(VaultSecurityTrigger trigger) => trigger switch
    {
        VaultSecurityTrigger.SessionLocked => "el bloqueo de la sesión de Windows",
        VaultSecurityTrigger.SessionDisconnected => "el cambio o la desconexión de la sesión",
        VaultSecurityTrigger.UserSigningOut => "el cierre de sesión del usuario",
        VaultSecurityTrigger.SystemSuspending => "la suspensión o hibernación del sistema",
        VaultSecurityTrigger.SystemShuttingDown => "el apagado o reinicio del sistema",
        _ => "un evento de seguridad de Windows"
    };

    internal static void RunRegressionProbe()
    {
        if (!TryResolve(SessionChangeMessage, 7, out var trigger) || trigger != VaultSecurityTrigger.SessionLocked)
            throw new InvalidOperationException("El bloqueo de sesión debe proteger las bóvedas.");
        if (TryResolve(SessionChangeMessage, 8, out _))
            throw new InvalidOperationException("El desbloqueo de sesión no debe provocar un segundo bloqueo.");
        if (!TryResolve(PowerBroadcastMessage, 4, out trigger) || trigger != VaultSecurityTrigger.SystemSuspending)
            throw new InvalidOperationException("La suspensión debe proteger las bóvedas.");
    }
}
