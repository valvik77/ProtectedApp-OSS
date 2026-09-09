using ProtectedApp.Models;
using ProtectedApp.Shared;
using Microsoft.Win32;
using DokanNet;
using DokanNet.Logging;

namespace ProtectedApp.Services;

public sealed class DiagnosticsService
{
    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(
        IReadOnlyCollection<ProtectedApplication> applications)
        => await RunAsync(applications, [], []);

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(
        IReadOnlyCollection<ProtectedApplication> applications,
        IReadOnlyCollection<ProtectedFolder> folders,
        IReadOnlyCollection<VaultContainer> vaults,
        string? guardianToken = null)
    {
        var results = new List<DiagnosticResult>();
        var installed = GuardianServiceDetector.IsInstalled();
        var running = GuardianServiceDetector.IsRunning();

        results.Add(new DiagnosticResult(
            "Servicio Guardian",
            !installed ? "El servicio no está instalado."
            : running ? "Instalado y en ejecución como servicio de Windows."
            : "Está instalado, pero no se encuentra en ejecución.",
            running ? DiagnosticSeverity.Success : DiagnosticSeverity.Error,
            canRepair: !running));

        var guardianClient = new GuardianClient();
        var guardianStatus = await guardianClient.GetStatusAsync();
        var systemStatus = running ? await guardianClient.GetDiagnosticsAsync(guardianToken) : new GuardianResponse();
        var installedVersion = systemStatus.Success
            ? systemStatus.InstalledProtectionVersion
            : GuardianServiceDetector.GetInstalledProtectionVersion();
        results.Add(new DiagnosticResult(
            "Versión del motor",
            installedVersion is null
                ? $"No se encontró la versión instalada; se esperaba {GuardianServiceDetector.ExpectedProtectionVersion}."
                : installedVersion == GuardianServiceDetector.ExpectedProtectionVersion
                    ? LocalizationService.IsEnglish
                        ? $"Guardian {installedVersion} matches this version of ProtectedApp."
                        : $"Guardian {installedVersion} coincide con esta versión de ProtectedApp."
                    : LocalizationService.IsEnglish
                        ? $"Installed: {installedVersion}; expected: {GuardianServiceDetector.ExpectedProtectionVersion}."
                        : $"Instalada: {installedVersion}; esperada: {GuardianServiceDetector.ExpectedProtectionVersion}.",
            installedVersion == GuardianServiceDetector.ExpectedProtectionVersion
                ? DiagnosticSeverity.Success
                : DiagnosticSeverity.Error,
            canRepair: installedVersion != GuardianServiceDetector.ExpectedProtectionVersion));

        results.Add(new DiagnosticResult(
            "Comunicación protegida",
            guardianStatus.Success
                ? "La interfaz se comunica correctamente con Guardian."
                : "Guardian no respondió a través del canal local protegido.",
            guardianStatus.Success ? DiagnosticSeverity.Success : DiagnosticSeverity.Error,
            canRepair: !guardianStatus.Success));

        results.Add(systemStatus.Success
            ? systemStatus.GateHealthy == true
                ? new DiagnosticResult("Puerta preventiva", "Guardian confirma que ProtectedApp.Gate está disponible.", DiagnosticSeverity.Success)
                : new DiagnosticResult("Puerta preventiva", "Guardian no encuentra ProtectedApp.Gate.", DiagnosticSeverity.Error, canRepair: true)
            : new DiagnosticResult("Puerta preventiva", "La versión instalada de Guardian no admite esta comprobación protegida.", DiagnosticSeverity.Warning, canRepair: true));

        results.Add(EvaluateProtectionPolicy(guardianStatus));

        results.Add(systemStatus.Success
            ? systemStatus.HealthTaskHealthy == true
                ? new DiagnosticResult("Tarea de recuperación", "Guardian confirma que la tarea SYSTEM está habilitada y bien configurada.", DiagnosticSeverity.Success)
                : new DiagnosticResult("Tarea de recuperación",
                    systemStatus.HealthTaskDetail ?? "La tarea SYSTEM falta, está deshabilitada o tiene una acción incorrecta.",
                    DiagnosticSeverity.Error, canRepair: true)
            : new DiagnosticResult("Tarea de recuperación", "La cuenta de usuario no puede consultarla; se necesita Guardian actualizado.", DiagnosticSeverity.Warning, canRepair: true));
        results.Add(systemStatus.Success
            ? systemStatus.IntegrityHealthy == true
                ? new DiagnosticResult("Integridad de Guardian",
                    string.IsNullOrWhiteSpace(systemStatus.IntegrityDetail)
                        ? "Los binarios protegidos coinciden con su línea base."
                        : systemStatus.IntegrityDetail,
                    DiagnosticSeverity.Success)
                : new DiagnosticResult("Integridad de Guardian",
                    systemStatus.IntegrityDetail ?? "Guardian no puede comprobar o recuperar uno de sus binarios.",
                    DiagnosticSeverity.Error, canRepair: true)
            : new DiagnosticResult("Integridad de Guardian", "Guardian no respondió para comprobar sus binarios.", DiagnosticSeverity.Warning, canRepair: true));
        results.Add(systemStatus.Success
            ? systemStatus.AuditTrailHealthy == true
                ? new DiagnosticResult("Auditoría de seguridad", "El registro de manipulaciones mantiene una cadena válida.", DiagnosticSeverity.Success)
                : new DiagnosticResult("Auditoría de seguridad", "El registro protegido presenta una discontinuidad.", DiagnosticSeverity.Error, canRepair: true)
            : new DiagnosticResult("Auditoría de seguridad", "Guardian no respondió para comprobar el registro protegido.", DiagnosticSeverity.Warning));
        results.Add(systemStatus.Success
            ? systemStatus.SafeRecoveryActive
                ? new DiagnosticResult("Recuperación segura", "Guardian mantiene las puertas preventivas activas hasta completar una reparación.", DiagnosticSeverity.Error, canRepair: true)
                : new DiagnosticResult("Recuperación segura", "No hay incidencias críticas pendientes de recuperación.", DiagnosticSeverity.Success)
            : new DiagnosticResult("Recuperación segura", "No se pudo consultar el estado de recuperación.", DiagnosticSeverity.Warning));
        results.Add(systemStatus.Success
            ? systemStatus.SignatureIdentityConfigured
                ? new DiagnosticResult("Identidad de firma", "Guardian exige la identidad del certificado de esta instalación.", DiagnosticSeverity.Success)
                : new DiagnosticResult("Identidad de firma", "Esta instalación no tiene una identidad de firma Authenticode verificable.", DiagnosticSeverity.Warning)
            : new DiagnosticResult("Identidad de firma", "Guardian no respondió para comprobar la identidad de firma.", DiagnosticSeverity.Warning));
        results.Add(CheckAutomaticStartup());
        results.AddRange(CheckRules(applications));
        results.Add(CheckDokanyRuntime());
        results.Add(CheckExplorerIntegration());
        results.Add(CheckFolders(folders));
        results.Add(CheckVaults(vaults));
        results.Add(CheckMountedVaultDrives(vaults));
        results.Add(CheckVaultRecovery(vaults));
        return results;
    }

    internal static IReadOnlyList<DiagnosticResult> CheckRules(
        IReadOnlyCollection<ProtectedApplication> applications)
    {
        var results = new List<DiagnosticResult>();
        var active = applications.Where(application => application.IsEnabled).ToArray();
        var missing = active.Where(application => !File.Exists(application.Path)).ToArray();
        var unsupported = active.Where(application => !ProtectedTarget.IsSupported(application.Path)).ToArray();
        results.Add(new DiagnosticResult(
            "Integridad de las reglas",
            missing.Length == 0 && unsupported.Length == 0
                ? active.Length == 0 ? "No hay reglas activas que comprobar."
                    : LocalizationService.IsEnglish ? $"{active.Length} active rules point to valid files." : $"{active.Length} reglas activas apuntan a archivos válidos."
                : $"{missing.Length} archivos no existen y {unsupported.Length} tienen un formato no compatible.",
            missing.Length == 0 && unsupported.Length == 0 ? DiagnosticSeverity.Success : DiagnosticSeverity.Error));

        var duplicateGroups = applications
            .Where(application => !string.IsNullOrWhiteSpace(application.Path))
            .GroupBy(application => NormalizePath(application.Path), StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);
        results.Add(new DiagnosticResult(
            "Reglas duplicadas",
            duplicateGroups == 0
                ? "No hay rutas protegidas más de una vez."
                : $"Hay {duplicateGroups} rutas repetidas; conviene conservar una sola regla por archivo.",
            duplicateGroups == 0 ? DiagnosticSeverity.Success : DiagnosticSeverity.Warning));
        return results;
    }

    internal static DiagnosticResult EvaluateProtectionPolicy(GuardianResponse guardianStatus) =>
        guardianStatus.Success && guardianStatus.PolicyConfigured
            ? new DiagnosticResult("Política de protección", "Guardian ha cargado correctamente la política cifrada de este usuario.", DiagnosticSeverity.Success)
            : new DiagnosticResult("Política de protección", "Guardian no tiene una política válida para este usuario.",
                guardianStatus.Success ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, canRepair: true);

    private static DiagnosticResult CheckAutomaticStartup()
    {
        var command = StartupService.GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(command))
            return new DiagnosticResult("Inicio con Windows", "Desactivado por decisión del usuario.", DiagnosticSeverity.Success);
        return command.Contains("--background", StringComparison.OrdinalIgnoreCase)
            ? new DiagnosticResult("Inicio con Windows", "Configurado en modo silencioso.", DiagnosticSeverity.Success)
            : new DiagnosticResult("Inicio con Windows", "La entrada existe, pero no utiliza el modo silencioso.", DiagnosticSeverity.Warning);
    }

    private static DiagnosticResult CheckExplorerIntegration()
    {
        var vaultAssociation = Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\.pavault", null, null) as string;
        var folderCommand = Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\Directory\shell\ProtectedApp.Encrypt\command", null, null) as string;
        var vaultCommand = Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\ProtectedApp.Vault\shell\ProtectedApp.Mount\command", null, null) as string;
        var driveCommand = Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\Drive\shell\ProtectedApp.UnmountVault\command", null, null) as string;
        var valid = string.Equals(vaultAssociation, "ProtectedApp.Vault", StringComparison.OrdinalIgnoreCase)
            && folderCommand?.Contains("ProtectedApp.exe", StringComparison.OrdinalIgnoreCase) == true
            && vaultCommand?.Contains("ProtectedApp.exe", StringComparison.OrdinalIgnoreCase) == true
            && driveCommand?.Contains("--unmount-vault-drive", StringComparison.OrdinalIgnoreCase) == true;
        return new DiagnosticResult(
            "Integración con Explorador",
            valid
                ? "La asociación .pavault y los comandos contextuales de carpetas, bóvedas y unidades están registrados."
                : "Falta la asociación de bóvedas o algún comando contextual. Reinstala ProtectedApp para recuperarlos.",
            valid ? DiagnosticSeverity.Success : DiagnosticSeverity.Warning);
    }

    private static DiagnosticResult CheckDokanyRuntime()
    {
        try
        {
            using var dokan = new Dokan(new NullLogger());
            var runtimeVersion = dokan.Version;
            var driverVersion = dokan.DriverVersion;
            return runtimeVersion > 0 && driverVersion > 0
                ? new DiagnosticResult("Unidad virtual Dokany", LocalizationService.IsEnglish
                    ? $"Runtime {FormatDokanyVersion(runtimeVersion)} and driver {FormatDokanyVersion(driverVersion)} available."
                    : $"Runtime {FormatDokanyVersion(runtimeVersion)} y controlador {FormatDokanyVersion(driverVersion)} disponibles.",
                    DiagnosticSeverity.Success)
                : new DiagnosticResult("Unidad virtual Dokany",
                    "El runtime se cargó, pero el controlador de unidades virtuales no responde. Reinicia Windows o reinstala ProtectedApp.",
                    DiagnosticSeverity.Error, canRepair: true);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or DokanException
                                   or TypeInitializationException)
        {
            return new DiagnosticResult("Unidad virtual Dokany",
                "El componente necesario para montar bóvedas no está disponible. Reinstala ProtectedApp para instalar Dokany.",
                DiagnosticSeverity.Error, canRepair: true);
        }
    }

    private static DiagnosticResult CheckFolders(IReadOnlyCollection<ProtectedFolder> folders)
    {
        var missing = folders.Where(folder => folder.IsEnabled && !Directory.Exists(folder.Path)).ToArray();
        return new DiagnosticResult(
            "Integridad de carpetas",
            missing.Length == 0
                ? folders.Count == 0 ? "No hay carpetas protegidas que comprobar." : $"{folders.Count} carpetas protegidas disponibles."
                : $"{missing.Length} carpetas protegidas ya no existen o no están disponibles.",
            missing.Length == 0 ? DiagnosticSeverity.Success : DiagnosticSeverity.Warning);
    }

    private static DiagnosticResult CheckVaults(IReadOnlyCollection<VaultContainer> vaults)
    {
        var missing = vaults.Where(vault => !string.IsNullOrWhiteSpace(vault.VaultFilePath)
            && !File.Exists(vault.VaultFilePath)).ToArray();
        var pending = vaults.Count(vault => vault.HasPendingJournal);
        var severity = missing.Length > 0 ? DiagnosticSeverity.Error
            : pending > 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Success;
        var detail = missing.Length > 0
            ? $"{missing.Length} archivos .pavault no están disponibles."
            : pending > 0
                ? $"{pending} bóvedas tienen cambios pendientes de comprobar antes de abrirse."
                : vaults.Count == 0 ? "No hay bóvedas registradas que comprobar." : $"{vaults.Count} bóvedas disponibles.";
        return new DiagnosticResult("Integridad de bóvedas", detail, severity);
    }

    private static DiagnosticResult CheckMountedVaultDrives(IReadOnlyCollection<VaultContainer> vaults)
    {
        var mounted = vaults.Where(vault => vault.IsMounted && !string.IsNullOrWhiteSpace(vault.MountPath)).ToArray();
        if (mounted.Length == 0)
            return new DiagnosticResult("Unidades virtuales montadas", "No hay bóvedas abiertas como unidad virtual.", DiagnosticSeverity.Success);

        var unavailable = new List<string>();
        var unexpectedFileSystem = new List<string>();
        foreach (var vault in mounted)
        {
            var mountPath = vault.MountPath!;
            if (!Directory.Exists(mountPath))
            {
                unavailable.Add(vault.Name);
                continue;
            }
            try
            {
                var drive = new DriveInfo(mountPath);
                if (!string.Equals(drive.DriveFormat, "PAVLT003", StringComparison.OrdinalIgnoreCase))
                    unexpectedFileSystem.Add(vault.Name);
            }
            catch (IOException) { unavailable.Add(vault.Name); }
            catch (UnauthorizedAccessException) { unavailable.Add(vault.Name); }
        }

        if (unavailable.Count > 0)
            return new DiagnosticResult("Unidades virtuales montadas",
                $"{unavailable.Count} bóveda(s) figuran como abiertas, pero su unidad no responde: {string.Join(", ", unavailable)}.",
                DiagnosticSeverity.Error);
        if (unexpectedFileSystem.Count > 0)
            return new DiagnosticResult("Unidades virtuales montadas",
                $"{unexpectedFileSystem.Count} unidad(es) no exponen el sistema de archivos PAVLT003: {string.Join(", ", unexpectedFileSystem)}.",
                DiagnosticSeverity.Warning);
        return new DiagnosticResult("Unidades virtuales montadas",
            $"{mounted.Length} bóveda(s) abierta(s) y accesible(s) mediante PAVLT003.", DiagnosticSeverity.Success);
    }

    private static DiagnosticResult CheckVaultRecovery(IReadOnlyCollection<VaultContainer> vaults)
    {
        var journals = vaults.Count(vault => vault.HasPendingJournal);
        var backups = vaults.Count(vault => vault.HasRecoveryBackup);
        if (journals > 0)
            return new DiagnosticResult("Recuperación de bóvedas",
                $"{journals} bóveda(s) conservan un diario de una sesión anterior. Ábrelas y vuelve a bloquearlas para consolidar o revisar los cambios.",
                DiagnosticSeverity.Warning);
        return new DiagnosticResult("Recuperación de bóvedas",
            backups == 0
                ? "No hay diarios ni copias de recuperación pendientes."
                : $"No hay recuperación pendiente. {backups} bóveda(s) tienen una copia cifrada anterior disponible.",
            DiagnosticSeverity.Success);
    }

    private static string FormatDokanyVersion(int version) =>
        $"{version / 100}.{version / 10 % 10}.{version % 10}";

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return path.Trim(); }
    }
}
