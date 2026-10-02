using System.Security.Cryptography;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

/// <summary>
/// Dependencias que <see cref="VaultScheduledBackupService"/> comparte con
/// <see cref="VaultService"/>. Son los ayudantes de archivo y criptografía que
/// el resto del contenedor sigue usando, de modo que no pueden mudarse aquí.
/// <c>ReportError</c> es el sumidero de errores: escribe en
/// <see cref="VaultService.LastError"/>, que la interfaz lee justo después de
/// cada llamada, así que esta clase nunca duplica esa propiedad.
/// </summary>
internal sealed record VaultBackupDependencies(
    Action<string?> ReportError,
    Func<VaultContainer, bool> HasPendingJournal,
    Func<string, string, bool, Task<byte[]?>> CopyFileDurablyAsync,
    Func<string, Task<byte[]>> ComputeFileHashAsync,
    Func<string, bool> HasStructurallyValidVaultEnvelope,
    Func<string, string, Task<VaultContainer>> ReadVaultMetadataAuthenticatedAsync,
    Func<string, string, string> CreateUniquePreservedPath,
    Func<string, string, bool> PathsEqual,
    Func<Exception, bool> IsVaultDataException,
    Action<VaultContainer, string> UpdateRuntimeMetadata);

/// <summary>
/// Copias programadas de un contenedor cerrado: creación con retención,
/// inventario, limpieza, validación autenticada y restauración con reversión.
/// Todas trabajan sobre el archivo cerrado, así que no tocan las sesiones
/// montadas ni el ciclo de vida de Dokany.
/// </summary>
internal sealed class VaultScheduledBackupService(VaultBackupDependencies dependencies)
{
    private const string BackupFolderName = "ProtectedApp Vaults";
    private const int MinimumRetention = 1;
    private const int MaximumRetention = 20;

    public async Task<VaultScheduledBackupResult> CreateScheduledBackupAsync(VaultContainer vault,
        string destinationRoot, int retentionCount)
    {
        dependencies.ReportError(null);
        try
        {
            if (vault is null || vault.IsMounted || string.IsNullOrWhiteSpace(vault.VaultFilePath)
                || !File.Exists(vault.VaultFilePath))
                throw new InvalidOperationException("La bóveda debe estar cerrada y tener un contenedor disponible.");

            var sourcePath = Path.GetFullPath(vault.VaultFilePath);
            var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root) || dependencies.PathsEqual(sourcePath, root)
                || sourcePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La carpeta de copias no puede ser la misma bóveda ni contenerla.");

            var vaultFolder = Path.Combine(root, BackupFolderName, vault.Id.ToString("N"));
            Directory.CreateDirectory(vaultFolder);
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var targetPath = Path.Combine(vaultFolder, $"{fileName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pavault");
            byte[]? sourceHash = null;
            byte[]? targetHash = null;
            try
            {
                sourceHash = await dependencies.CopyFileDurablyAsync(sourcePath, targetPath, true);
                targetHash = await dependencies.ComputeFileHashAsync(targetPath);
                if (sourceHash is null || new FileInfo(targetPath).Length != new FileInfo(sourcePath).Length
                    || !CryptographicOperations.FixedTimeEquals(sourceHash, targetHash)
                    || !dependencies.HasStructurallyValidVaultEnvelope(targetPath))
                    throw new IOException("La copia creada no superó la verificación de integridad.");
            }
            catch
            {
                try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
                throw;
            }
            finally
            {
                if (sourceHash is not null) CryptographicOperations.ZeroMemory(sourceHash);
                if (targetHash is not null) CryptographicOperations.ZeroMemory(targetHash);
            }

            retentionCount = Math.Clamp(retentionCount, MinimumRetention, MaximumRetention);
            foreach (var obsolete in Directory.EnumerateFiles(vaultFolder, "*.pavault", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(retentionCount))
            {
                try { File.Delete(obsolete); } catch { }
            }
            return new(true, targetPath, null);
        }
        catch (Exception ex) when (dependencies.IsVaultDataException(ex) || ex is InvalidOperationException)
        {
            dependencies.ReportError(ex.Message);
            return new(false, null, ex.Message);
        }
    }

    public VaultScheduledBackupInfo InspectScheduledBackups(VaultContainer vault, string? destinationRoot)
    {
        try
        {
            if (vault is null || string.IsNullOrWhiteSpace(destinationRoot)) return new(0, null, 0);
            var folder = GetVaultFolder(vault, destinationRoot);
            if (!Directory.Exists(folder)) return new(0, null, 0);
            var files = Directory.EnumerateFiles(folder, "*.pavault", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path)).ToArray();
            return new(files.Length, files.Length == 0 ? null : files.MaxBy(file => file.LastWriteTimeUtc)!.LastWriteTimeUtc,
                files.Sum(file => file.Length));
        }
        catch { return new(0, null, 0); }
    }

    public VaultScheduledBackupCleanupResult CleanupScheduledBackups(VaultContainer vault, string destinationRoot, int retainCount)
    {
        if (vault is null || string.IsNullOrWhiteSpace(destinationRoot)) return new(0, 0, 0);
        try
        {
            var folder = GetVaultFolder(vault, destinationRoot);
            if (!Directory.Exists(folder)) return new(0, 0, 0);
            var deleted = 0;
            var freed = 0L;
            var failed = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.pavault", SearchOption.TopDirectoryOnly)
                         .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(Math.Clamp(retainCount, MinimumRetention, MaximumRetention)))
            {
                try { var length = file.Length; file.Delete(); deleted++; freed += length; }
                catch { failed++; }
            }
            return new(deleted, freed, failed);
        }
        catch { return new(0, 0, 1); }
    }

    public IReadOnlyList<VaultScheduledBackupVersion> ListScheduledBackups(VaultContainer vault, string? destinationRoot)
    {
        try
        {
            if (vault is null || string.IsNullOrWhiteSpace(destinationRoot)) return [];
            var folder = GetVaultFolder(vault, destinationRoot);
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFiles(folder, "*.pavault", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new VaultScheduledBackupVersion(file.FullName, file.LastWriteTimeUtc, file.Length,
                    dependencies.HasStructurallyValidVaultEnvelope(file.FullName)))
                .ToArray();
        }
        catch { return []; }
    }

    public async Task<VaultBackupValidation> ValidateScheduledBackupAsync(VaultContainer vault, string backupPath, string password)
    {
        dependencies.ReportError(null);
        try
        {
            if (vault is null || string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
                return new(false, false, null, "La versión seleccionada ya no existe.");
            var backupVault = await dependencies.ReadVaultMetadataAuthenticatedAsync(backupPath, password);
            if (backupVault.Id != vault.Id)
                return new(false, false, null, "La versión seleccionada pertenece a otra bóveda.");

            var primaryValid = false;
            if (!string.IsNullOrWhiteSpace(vault.VaultFilePath) && File.Exists(vault.VaultFilePath))
            {
                try
                {
                    var primaryVault = await dependencies.ReadVaultMetadataAuthenticatedAsync(vault.VaultFilePath, password);
                    primaryValid = primaryVault.Id == vault.Id;
                }
                catch (Exception ex) when (dependencies.IsVaultDataException(ex)) { }
            }
            return new(true, primaryValid, backupVault, primaryValid
                ? "La versión y el contenedor actual son válidos."
                : "La versión es válida y puede sustituir al contenedor ausente o dañado.");
        }
        catch (Exception ex) when (dependencies.IsVaultDataException(ex))
        {
            dependencies.ReportError(ex.Message);
            return new(false, false, null, "La contraseña no abre la versión seleccionada o esta ha sido modificada.");
        }
    }

    public async Task<VaultBackupRestoreResult> RestoreScheduledBackupAsync(VaultContainer vault, string backupPath, string password)
    {
        dependencies.ReportError(null);
        if (dependencies.HasPendingJournal(vault))
        {
            const string message = "Abre y bloquea la bóveda para consolidar sus cambios pendientes antes de restaurar una copia.";
            dependencies.ReportError(message);
            return new(false, null, message);
        }
        var validation = await ValidateScheduledBackupAsync(vault, backupPath, password);
        if (!validation.BackupValid || validation.BackupVault is null) return new(false, null, validation.Message);
        if (string.IsNullOrWhiteSpace(vault.VaultFilePath)) return new(false, null, "La bóveda no tiene un contenedor principal.");
        var primaryPath = Path.GetFullPath(vault.VaultFilePath);
        var directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return new(false, null, "La carpeta del contenedor principal no existe.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(primaryPath)}.restore-{Guid.NewGuid():N}.tmp");
        var preservedPath = File.Exists(primaryPath)
            ? dependencies.CreateUniquePreservedPath(primaryPath, validation.PrimaryValid ? "replaced" : "corrupt") : null;
        var primaryMoved = false;
        var replacementInstalled = false;
        try
        {
            await dependencies.CopyFileDurablyAsync(backupPath, temporaryPath, false);
            var verifiedCopy = await dependencies.ReadVaultMetadataAuthenticatedAsync(temporaryPath, password);
            if (verifiedCopy.Id != vault.Id) throw new InvalidDataException("La copia temporal pertenece a otra bóveda.");
            if (File.Exists(primaryPath) && preservedPath is not null) { File.Move(primaryPath, preservedPath); primaryMoved = true; }
            File.Move(temporaryPath, primaryPath);
            replacementInstalled = true;
            var finalVerification = await dependencies.ReadVaultMetadataAuthenticatedAsync(primaryPath, password);
            if (finalVerification.Id != vault.Id) throw new InvalidDataException("El contenedor restaurado no superó la verificación final.");
            var restored = validation.BackupVault;
            vault.Name = restored.Name;
            vault.Description = restored.Description;
            vault.AutoLockMinutes = restored.AutoLockMinutes;
            vault.InactivityAutoLockMinutes = restored.InactivityAutoLockMinutes;
            vault.CreatedUtc = restored.CreatedUtc;
            vault.ModifiedUtc = restored.ModifiedUtc;
            dependencies.UpdateRuntimeMetadata(vault, primaryPath);
            return new(true, preservedPath, null);
        }
        catch (Exception ex) when (dependencies.IsVaultDataException(ex) || ex is InvalidOperationException)
        {
            try
            {
                if (replacementInstalled && File.Exists(primaryPath)) File.Delete(primaryPath);
                if (primaryMoved && preservedPath is not null && File.Exists(preservedPath)) File.Move(preservedPath, primaryPath);
            }
            catch (Exception rollbackFailure)
            {
                // The restore failed and putting the original container back
                // failed too, so the vault now lives at preservedPath only. The
                // caller reports the original error, which would leave the user
                // with no idea where their vault went; name the file that holds
                // it, and keep the detail in the log.
                AppDiagnosticLog.Append("vault-restore.log",
                    $"{DateTimeOffset.Now:O} No se pudo deshacer la restauración de {primaryPath} " +
                    $"({rollbackFailure.GetType().Name}: {rollbackFailure.Message}). " +
                    $"La bóveda original se conserva en {preservedPath}.{Environment.NewLine}");
                var combined = $"{ex.Message} Además, no se pudo restaurar el contenedor original: " +
                    $"conserva «{preservedPath}», que contiene la bóveda anterior.";
                dependencies.ReportError(combined);
                return new(false, preservedPath, combined);
            }
            dependencies.ReportError(ex.Message);
            return new(false, preservedPath, ex.Message);
        }
        finally
        {
            if (File.Exists(temporaryPath)) try { File.Delete(temporaryPath); } catch { }
        }
    }

    public bool DeleteScheduledBackup(VaultContainer vault, string destinationRoot, string backupPath)
    {
        try
        {
            var folder = GetVaultFolder(vault, destinationRoot);
            var path = Path.GetFullPath(backupPath);
            if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".pavault", StringComparison.OrdinalIgnoreCase)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    private static string GetVaultFolder(VaultContainer vault, string destinationRoot) =>
        Path.Combine(Path.GetFullPath(destinationRoot), BackupFolderName, vault.Id.ToString("N"));
}
