namespace ProtectedApp.Services;

public static class AppLaunchMode
{
    private static readonly string[] SilentArguments =
        ["--background", "--service-managed", "--recovered"];

    public static bool IsSilent(IEnumerable<string> commandLine) =>
        commandLine.Any(argument =>
            SilentArguments.Contains(argument, StringComparer.OrdinalIgnoreCase)
            || argument.Equals("--open-folder", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--folder-action", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--vault-action", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--unmount-vault-drive", StringComparison.OrdinalIgnoreCase));

    public static StartupUiAction ResolveStartupUiAction(
        bool silentLaunch,
        bool hasMasterPassword,
        bool guardianManaged)
    {
        if (silentLaunch) return StartupUiAction.KeepHidden;
        if (!hasMasterPassword && guardianManaged) return StartupUiAction.RecoverProtectedState;
        if (!hasMasterPassword) return StartupUiAction.CreateMasterPassword;
        return StartupUiAction.UnlockManagementPanel;
    }

    internal static void RunRegressionProbe()
    {
        Assert(IsSilent(["--background"]), "El inicio en segundo plano debe permanecer silencioso.");
        Assert(IsSilent(["--service-managed"]), "El inicio gestionado por Guardian debe permanecer silencioso.");
        Assert(IsSilent(["--open-folder", "C:\\Temporal"]), "Una activación contextual no debe abrir el panel.");
        Assert(IsSilent(["--unmount-vault-drive", "V:\\"]), "El desmontaje contextual debe permanecer silencioso.");
        Assert(!IsSilent(["ProtectedApp.exe"]), "Un inicio manual no debe clasificarse como silencioso.");
        Assert(ResolveStartupUiAction(true, true, true) == StartupUiAction.KeepHidden,
            "El inicio silencioso no debe solicitar credenciales.");
        Assert(ResolveStartupUiAction(false, true, true) == StartupUiAction.UnlockManagementPanel,
            "El inicio manual debe solicitar el desbloqueo del panel.");
        Assert(ResolveStartupUiAction(false, false, true) == StartupUiAction.RecoverProtectedState,
            "Guardian debe recuperar el estado antes de mostrar un panel sin credenciales.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public enum StartupUiAction
{
    KeepHidden,
    RecoverProtectedState,
    CreateMasterPassword,
    UnlockManagementPanel
}
