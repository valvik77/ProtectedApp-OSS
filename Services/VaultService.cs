using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Dokan = DokanNet.Dokan;
using DokanException = DokanNet.DokanException;
using DokanInstance = DokanNet.DokanInstance;
using DokanInstanceBuilder = DokanNet.DokanInstanceBuilder;
using DokanOptions = DokanNet.DokanOptions;
using NullLogger = DokanNet.Logging.NullLogger;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

/// <summary>
/// Contenedor cifrado autenticado. PAVLT003 se expone mediante unidades Dokany
/// virtuales; la edición usa un journal cifrado y un reemplazo atómico.
/// PAVLT002 conserva temporalmente el flujo antiguo únicamente para migrarse.
/// </summary>
public sealed class VaultService : IDisposable
{
    /// <summary>
    /// Preferencia para el siguiente montaje virtual. Nunca sustituye una
    /// letra ya utilizada por Windows.
    /// </summary>
    public char? PreferredVirtualDriveLetter { get; set; }

    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("PAVLT002");
    private const byte FormatVersion = 2;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 600_000;
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const string ManifestEntryName = "__protectedapp_vault.json";
    private const string FilesPrefix = "files/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 32
    };

    private readonly ConcurrentDictionary<Guid, VaultSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, VirtualVaultSession> _virtualSessions = new();
    public string? LastError { get; private set; }
    public bool LastMountUsedJournal { get; private set; }

    internal static async Task RunReadWriteMountProbeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mount-probe.pavault");
        var vault = new VaultContainer { Name = "MountProbe", AutoLockMinutes = 5, VaultFilePath = path };
        const string password = "ProtectedApp-Mount-Probe";
        var service = new VaultService();
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, path, password, createRecoveryBackup: false);
            var mount = await service.MountReadWriteVaultAsync(vault, password)
                ?? throw new IOException(service.LastError ?? "No se pudo montar la prueba editable.");
            var folder = Path.Combine(mount, "Documentos");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "prueba.txt"), "0123456789", new UTF8Encoding(false));
            if (!service.HasPendingJournal(vault))
                throw new InvalidDataException("La sesión editable no creó un diario cifrado recuperable.");

            // Simula una terminación inesperada: la unidad desaparece sin consolidar
            // el contenedor principal. La siguiente instancia debe abrir el diario.
            service.Dispose();
            vault.IsMounted = false;
            vault.IsReadOnlyMounted = false;
            vault.MountPath = null;
            vault.SessionExpiresUtc = null;
            service = new VaultService();

            mount = await service.MountReadWriteVaultAsync(vault, password)
                ?? throw new IOException(service.LastError ?? "No se pudo volver a montar la prueba editable.");
            if (!service.LastMountUsedJournal)
                throw new InvalidDataException("La sesión interrumpida no se abrió desde el diario cifrado.");
            var originalFile = Path.Combine(mount, "Documentos", "prueba.txt");
            await using (var stream = new FileStream(originalFile, FileMode.Open, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous))
            {
                stream.Position = 4;
                await stream.WriteAsync(Encoding.UTF8.GetBytes("XYZ"));
                stream.SetLength(6);
                stream.SetLength(12);
            }
            File.Move(originalFile, Path.Combine(mount, "Documentos", "renombrada.txt"));
            var disposableFolder = Path.Combine(mount, "Temporal");
            Directory.CreateDirectory(disposableFolder);
            await File.WriteAllTextAsync(Path.Combine(disposableFolder, "eliminar.txt"), "temporal", Encoding.UTF8);
            File.Delete(Path.Combine(disposableFolder, "eliminar.txt"));
            Directory.Delete(disposableFolder);
            if (!await service.UnmountVaultAsync(vault))
                throw new IOException(service.LastError ?? "No se pudo guardar la modificación por bloques.");
            if (service.HasPendingJournal(vault))
                throw new InvalidDataException("El diario cifrado no se consolidó después del bloqueo.");

            using var verification = await VaultFormatV3.OpenAsync(path, password);
            var actual = await VaultFormatV3.ReadFileRangeAsync(verification, "Documentos/renombrada.txt", 0, 128);
            try
            {
                var expected = Encoding.UTF8.GetBytes("0123XY").Concat(new byte[6]).ToArray();
                if (!actual.SequenceEqual(expected))
                    throw new InvalidDataException("La unidad editable no guardó el truncado y la ampliación esperados.");
                if (verification.Index.Entries.Any(entry => entry.Path.StartsWith("Temporal/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("La unidad editable no eliminó la carpeta temporal.");
            }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally
        {
            try { if (vault.IsMounted) await service.UnmountVaultAsync(vault); } catch { }
            service.Dispose();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); } catch { }
            try
            {
                var journal = GetJournalPath(vault.Id);
                if (File.Exists(journal)) File.Delete(journal);
                if (File.Exists(journal + ".next")) File.Delete(journal + ".next");
            }
            catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: false); } catch { }
        }
    }

    public async Task<bool> SaveVaultAsync(VaultContainer vault, string vaultFilePath, string password)
    {
        LastError = null;
        var existingContainer = !string.IsNullOrWhiteSpace(vaultFilePath) && File.Exists(vaultFilePath)
            && new FileInfo(vaultFilePath).Length > 0;
        if (vault is null || string.IsNullOrWhiteSpace(vaultFilePath) ||
            string.IsNullOrEmpty(password) || (!existingContainer && password.Length < PasswordService.MinimumPasswordLength))
        {
            LastError = "Los datos de la bóveda o la contraseña no son válidos.";
            return false;
        }

        byte[]? archiveBytes = null;
        byte[]? key = null;
        byte[]? salt = null;
        try
        {
            if (vault.IsMounted && _sessions.TryGetValue(vault.Id, out var session))
            {
                await VaultFormatV3.WriteFromDirectoryAsync(vault, session.WorkingDirectory, vaultFilePath,
                    session.Key, session.Salt, session.DataKey, createRecoveryBackup: true);
            }
            else if (VaultFormatV3.IsFormat(vaultFilePath))
            {
                using var existing = await VaultFormatV3.OpenAsync(vaultFilePath, password);
                if (existing.Vault.Id != vault.Id)
                    throw new InvalidDataException("El contenedor pertenece a otra bóveda.");
                await VaultFormatV3.RewriteMetadataAsync(existing, vault, vaultFilePath,
                    createRecoveryBackup: true);
            }
            else if (File.Exists(vaultFilePath) && new FileInfo(vaultFilePath).Length > 0)
            {
                using var existing = await ReadEncryptedVaultAsync(vaultFilePath, password);
                archiveBytes = await ReplaceManifestAsync(existing.ArchiveBytes, vault);
                key = existing.Key.ToArray();
                salt = existing.Salt.ToArray();
                await WriteEncryptedVaultAtomicAsync(vaultFilePath, archiveBytes, key, salt);
            }
            else
            {
                await VaultFormatV3.WriteNewAsync(vault, vaultFilePath, password,
                    sourceDirectory: null, createRecoveryBackup: false);
            }

            UpdateRuntimeMetadata(vault, vaultFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or CryptographicException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Error guardando bóveda: {ex.Message}");
            LastError = ex.Message;
            return false;
        }
        finally
        {
            if (archiveBytes is not null) CryptographicOperations.ZeroMemory(archiveBytes);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
        }
    }

    public async Task<VaultContainer?> LoadVaultAsync(string vaultFilePath, string password)
    {
        if (VaultFormatV3.IsFormat(vaultFilePath))
        {
            try
            {
                using var opened = await VaultFormatV3.OpenAsync(vaultFilePath, password);
                UpdateRuntimeMetadata(opened.Vault, vaultFilePath);
                return opened.Vault;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidDataException or CryptographicException or JsonException or ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine($"Error cargando bóveda PAVLT003: {ex.Message}");
                return null;
            }
        }

        VaultReadResult? read = null;
        try
        {
            read = await ReadEncryptedVaultAsync(vaultFilePath, password);
            UpdateRuntimeMetadata(read.Vault, vaultFilePath);
            return read.Vault;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or CryptographicException or JsonException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Error cargando bóveda: {ex.Message}");
            return null;
        }
        finally
        {
            read?.Dispose();
        }
    }

    public async Task<bool> VerifyVaultPasswordAsync(string vaultFilePath, string password) =>
        await LoadVaultAsync(vaultFilePath, password) is not null;

    public async Task<VaultIntegrityValidation> VerifyVaultIntegrityAsync(VaultContainer vault, string password)
    {
        LastError = null;
        if (vault is null || string.IsNullOrWhiteSpace(vault.VaultFilePath) || !File.Exists(vault.VaultFilePath))
            return new(false, false, 0, 0, 0, "No se encuentra el contenedor cifrado.");

        try
        {
            if (!VaultFormatV3.IsFormat(vault.VaultFilePath))
            {
                using var legacy = await ReadEncryptedVaultAsync(vault.VaultFilePath, password);
                if (legacy.Vault.Id != vault.Id)
                    return new(true, false, 0, 0, 0, "El contenedor pertenece a otra bóveda.");
                return new(true, true, 0, 0, 0,
                    "El contenedor antiguo se ha autenticado correctamente. Se verificará por bloques después de migrarlo a PAVLT003.");
            }

            VaultFormatV3.OpenedVault opened;
            try
            {
                opened = await VaultFormatV3.OpenAsync(vault.VaultFilePath, password);
            }
            catch (Exception ex) when (IsVaultDataException(ex))
            {
                LastError = ex.Message;
                return new(false, false, 0, 0, 0, "La contraseña es incorrecta o la cabecera de la bóveda no es válida.");
            }

            using (opened)
            {
                if (opened.Vault.Id != vault.Id)
                    return new(true, false, 0, 0, 0, "El contenedor pertenece a otra bóveda.");
                try
                {
                    var result = await VaultFormatV3.VerifyIntegrityAsync(opened);
                    return new(true, true, result.FileCount, result.ChunkCount, result.VerifiedBytes,
                        $"Integridad comprobada: {result.FileCount:N0} archivos y {result.ChunkCount:N0} bloques autenticados.");
                }
                catch (Exception ex) when (IsVaultDataException(ex))
                {
                    LastError = ex.Message;
                    return new(true, false, 0, 0, 0,
                        "La contraseña es válida, pero uno o más bloques de la bóveda no superaron la comprobación de integridad.");
                }
            }
        }
        catch (Exception ex) when (IsVaultDataException(ex))
        {
            LastError = ex.Message;
            return new(false, false, 0, 0, 0, "No se pudo comprobar el contenedor cifrado.");
        }
    }

    /// <summary>
    /// Creates and verifies a new encrypted container from an existing directory.
    /// The source is deliberately never changed here: the caller may remove it
    /// only after this method returns success and after an explicit confirmation.
    /// </summary>
    public async Task<bool> CreateVaultFromFolderAsync(VaultContainer vault, string sourceDirectory,
        string vaultFilePath, string password)
    {
        LastError = null;
        if (vault is null || string.IsNullOrWhiteSpace(sourceDirectory) ||
            string.IsNullOrWhiteSpace(vaultFilePath) || string.IsNullOrEmpty(password) || password.Length < PasswordService.MinimumPasswordLength)
        {
            LastError = "Los datos de la bóveda, la carpeta o la contraseña no son válidos.";
            return false;
        }

        try
        {
            var source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destination = Path.GetFullPath(vaultFilePath);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("La carpeta de origen ya no existe.");
            // FileSavePicker reserves a new selection by creating a zero-byte
            // placeholder. It is safe to replace that placeholder, but never
            // overwrite a real existing container or another file.
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                throw new IOException("Ya existe un archivo en la ubicación elegida para la bóveda.");
            if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La bóveda no puede guardarse dentro de la carpeta que se va a cifrar.");

            await VaultFormatV3.WriteNewAsync(vault, destination, password, source,
                createRecoveryBackup: false);
            using var verification = await VaultFormatV3.OpenAsync(destination, password);
            if (verification.Vault.Id != vault.Id)
                throw new InvalidDataException("La verificación final devolvió una bóveda distinta.");
            UpdateRuntimeMetadata(vault, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or CryptographicException or JsonException or ArgumentException)
        {
            LastError = ex.Message;
            return false;
        }
    }

    internal static async Task RunScheduledBackupProbeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "scheduled-probe.pavault");
        var backupRoot = Path.Combine(root, "backups");
        var vault = new VaultContainer { Name = "ScheduledBackupProbe", AutoLockMinutes = 5, VaultFilePath = vaultPath };
        const string password = "ProtectedApp-ScheduledBackup-Probe";
        var service = new VaultService();
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, vaultPath, password, createRecoveryBackup: false);
            var first = await service.CreateScheduledBackupAsync(vault, backupRoot, retentionCount: 2);
            if (!first.Success) throw new IOException(first.Error ?? "No se pudo crear la primera copia programada.");
            await Task.Delay(1100);
            var second = await service.CreateScheduledBackupAsync(vault, backupRoot, retentionCount: 2);
            if (!second.Success) throw new IOException(second.Error ?? "No se pudo crear la segunda copia programada.");

            var beforeCleanup = service.InspectScheduledBackups(vault, backupRoot);
            if (beforeCleanup.Count != 2 || beforeCleanup.TotalSizeBytes <= 0)
                throw new InvalidDataException("Las copias programadas no conservaron las dos versiones esperadas.");
            var version = service.ListScheduledBackups(vault, backupRoot).FirstOrDefault()
                ?? throw new InvalidDataException("No se pudo enumerar el historial de copias programadas.");
            if (!version.EnvelopeValid) throw new InvalidDataException("La versión programada no superó la comprobación estructural.");
            var validation = await service.ValidateScheduledBackupAsync(vault, version.Path, password);
            if (!validation.BackupValid || !validation.PrimaryValid)
                throw new InvalidDataException("La versión programada no superó la validación autenticada.");

            var cleanup = service.CleanupScheduledBackups(vault, backupRoot, retainCount: 1);
            if (cleanup.DeletedCount != 1 || cleanup.FreedBytes <= 0 || cleanup.FailedCount != 0)
                throw new InvalidDataException("La limpieza de versiones programadas no conservó solo la versión reciente.");
            var remaining = service.ListScheduledBackups(vault, backupRoot).SingleOrDefault()
                ?? throw new InvalidDataException("No quedó una versión recuperable tras la limpieza.");
            var restore = await service.RestoreScheduledBackupAsync(vault, remaining.Path, password);
            if (!restore.Success || string.IsNullOrWhiteSpace(restore.PreservedPrimaryPath) || !File.Exists(restore.PreservedPrimaryPath))
                throw new InvalidDataException("La restauración de una versión programada no preservó el contenedor actual.");
        }
        finally
        {
            service.Dispose();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public async Task<VaultScheduledBackupResult> CreateScheduledBackupAsync(VaultContainer vault,
        string destinationRoot, int retentionCount)
    {
        LastError = null;
        try
        {
            if (vault is null || vault.IsMounted || string.IsNullOrWhiteSpace(vault.VaultFilePath)
                || !File.Exists(vault.VaultFilePath))
                throw new InvalidOperationException("La bóveda debe estar cerrada y tener un contenedor disponible.");

            var sourcePath = Path.GetFullPath(vault.VaultFilePath);
            var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root) || PathsEqual(sourcePath, root)
                || sourcePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La carpeta de copias no puede ser la misma bóveda ni contenerla.");

            var vaultFolder = Path.Combine(root, "ProtectedApp Vaults", vault.Id.ToString("N"));
            Directory.CreateDirectory(vaultFolder);
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var targetPath = Path.Combine(vaultFolder, $"{fileName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pavault");
            await CopyFileDurablyAsync(sourcePath, targetPath);
            if (new FileInfo(targetPath).Length != new FileInfo(sourcePath).Length
                || !HasStructurallyValidVaultEnvelope(targetPath))
                throw new IOException("La copia creada no superó la verificación estructural.");

            retentionCount = Math.Clamp(retentionCount, 1, 20);
            foreach (var obsolete in Directory.EnumerateFiles(vaultFolder, "*.pavault", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(retentionCount))
            {
                try { File.Delete(obsolete); } catch { }
            }
            return new(true, targetPath, null);
        }
        catch (Exception ex) when (IsVaultDataException(ex) || ex is InvalidOperationException)
        {
            LastError = ex.Message;
            return new(false, null, ex.Message);
        }
    }

    public VaultScheduledBackupInfo InspectScheduledBackups(VaultContainer vault, string? destinationRoot)
    {
        try
        {
            if (vault is null || string.IsNullOrWhiteSpace(destinationRoot)) return new(0, null, 0);
            var folder = Path.Combine(Path.GetFullPath(destinationRoot), "ProtectedApp Vaults", vault.Id.ToString("N"));
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
            var folder = Path.Combine(Path.GetFullPath(destinationRoot), "ProtectedApp Vaults", vault.Id.ToString("N"));
            if (!Directory.Exists(folder)) return new(0, 0, 0);
            var deleted = 0;
            var freed = 0L;
            var failed = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.pavault", SearchOption.TopDirectoryOnly)
                         .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(Math.Clamp(retainCount, 1, 20)))
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
            var folder = Path.Combine(Path.GetFullPath(destinationRoot), "ProtectedApp Vaults", vault.Id.ToString("N"));
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFiles(folder, "*.pavault", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new VaultScheduledBackupVersion(file.FullName, file.LastWriteTimeUtc, file.Length,
                    HasStructurallyValidVaultEnvelope(file.FullName)))
                .ToArray();
        }
        catch { return []; }
    }

    public async Task<VaultBackupValidation> ValidateScheduledBackupAsync(VaultContainer vault, string backupPath, string password)
    {
        LastError = null;
        try
        {
            if (vault is null || string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
                return new(false, false, null, "La versión seleccionada ya no existe.");
            var backupVault = await ReadVaultMetadataAuthenticatedAsync(backupPath, password);
            if (backupVault.Id != vault.Id)
                return new(false, false, null, "La versión seleccionada pertenece a otra bóveda.");

            var primaryValid = false;
            if (!string.IsNullOrWhiteSpace(vault.VaultFilePath) && File.Exists(vault.VaultFilePath))
            {
                try
                {
                    var primaryVault = await ReadVaultMetadataAuthenticatedAsync(vault.VaultFilePath, password);
                    primaryValid = primaryVault.Id == vault.Id;
                }
                catch (Exception ex) when (IsVaultDataException(ex)) { }
            }
            return new(true, primaryValid, backupVault, primaryValid
                ? "La versión y el contenedor actual son válidos."
                : "La versión es válida y puede sustituir al contenedor ausente o dañado.");
        }
        catch (Exception ex) when (IsVaultDataException(ex))
        {
            LastError = ex.Message;
            return new(false, false, null, "La contraseña no abre la versión seleccionada o esta ha sido modificada.");
        }
    }

    public async Task<VaultBackupRestoreResult> RestoreScheduledBackupAsync(VaultContainer vault, string backupPath, string password)
    {
        LastError = null;
        var validation = await ValidateScheduledBackupAsync(vault, backupPath, password);
        if (!validation.BackupValid || validation.BackupVault is null) return new(false, null, validation.Message);
        if (string.IsNullOrWhiteSpace(vault.VaultFilePath)) return new(false, null, "La bóveda no tiene un contenedor principal.");
        var primaryPath = Path.GetFullPath(vault.VaultFilePath);
        var directory = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return new(false, null, "La carpeta del contenedor principal no existe.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(primaryPath)}.restore-{Guid.NewGuid():N}.tmp");
        var preservedPath = File.Exists(primaryPath)
            ? CreateUniquePreservedPath(primaryPath, validation.PrimaryValid ? "replaced" : "corrupt") : null;
        var primaryMoved = false;
        var replacementInstalled = false;
        try
        {
            await CopyFileDurablyAsync(backupPath, temporaryPath);
            var verifiedCopy = await ReadVaultMetadataAuthenticatedAsync(temporaryPath, password);
            if (verifiedCopy.Id != vault.Id) throw new InvalidDataException("La copia temporal pertenece a otra bóveda.");
            if (File.Exists(primaryPath) && preservedPath is not null) { File.Move(primaryPath, preservedPath); primaryMoved = true; }
            File.Move(temporaryPath, primaryPath);
            replacementInstalled = true;
            var finalVerification = await ReadVaultMetadataAuthenticatedAsync(primaryPath, password);
            if (finalVerification.Id != vault.Id) throw new InvalidDataException("El contenedor restaurado no superó la verificación final.");
            var restored = validation.BackupVault;
            vault.Name = restored.Name;
            vault.Description = restored.Description;
            vault.AutoLockMinutes = restored.AutoLockMinutes;
            vault.InactivityAutoLockMinutes = restored.InactivityAutoLockMinutes;
            vault.InactivityAutoLockMinutes = restored.InactivityAutoLockMinutes;
            vault.CreatedUtc = restored.CreatedUtc;
            vault.ModifiedUtc = restored.ModifiedUtc;
            UpdateRuntimeMetadata(vault, primaryPath);
            return new(true, preservedPath, null);
        }
        catch (Exception ex) when (IsVaultDataException(ex) || ex is InvalidOperationException)
        {
            try
            {
                if (replacementInstalled && File.Exists(primaryPath)) File.Delete(primaryPath);
                if (primaryMoved && preservedPath is not null && File.Exists(preservedPath)) File.Move(preservedPath, primaryPath);
            }
            catch { }
            LastError = ex.Message;
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
            var folder = Path.Combine(Path.GetFullPath(destinationRoot), "ProtectedApp Vaults", vault.Id.ToString("N"));
            var path = Path.GetFullPath(backupPath);
            if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".pavault", StringComparison.OrdinalIgnoreCase)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> ChangeVaultPasswordAsync(string vaultFilePath, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < PasswordService.MinimumPasswordLength) return false;
        if (VaultFormatV3.IsFormat(vaultFilePath))
        {
            try
            {
                using (var opened = await VaultFormatV3.OpenAsync(vaultFilePath, oldPassword))
                    if (_sessions.ContainsKey(opened.Vault.Id) || _virtualSessions.ContainsKey(opened.Vault.Id))
                        return false;
                await VaultFormatV3.ChangePasswordAsync(vaultFilePath, oldPassword, newPassword);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or CryptographicException or JsonException or ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine($"Error cambiando contraseña PAVLT003: {ex.Message}");
                return false;
            }
        }

        VaultReadResult? read = null;
        byte[]? newKey = null;
        byte[]? newSalt = null;
        try
        {
            read = await ReadEncryptedVaultAsync(vaultFilePath, oldPassword);
            if (_sessions.ContainsKey(read.Vault.Id) || _virtualSessions.ContainsKey(read.Vault.Id)) return false;
            newSalt = RandomNumberGenerator.GetBytes(SaltSize);
            newKey = DeriveKey(newPassword, newSalt);
            await WriteEncryptedVaultAtomicAsync(vaultFilePath, read.ArchiveBytes, newKey, newSalt);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or CryptographicException or JsonException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Error cambiando contraseña de bóveda: {ex.Message}");
            return false;
        }
        finally
        {
            read?.Dispose();
            if (newKey is not null) CryptographicOperations.ZeroMemory(newKey);
            if (newSalt is not null) CryptographicOperations.ZeroMemory(newSalt);
        }
    }

    public async Task<string?> MountReadOnlyVaultAsync(VaultContainer vault, string password)
    {
        LastError = null;
        LastMountUsedJournal = false;
        if (vault is null || string.IsNullOrWhiteSpace(vault.VaultFilePath))
        {
            LastError = "La bóveda no tiene un contenedor cifrado asociado.";
            return null;
        }
        if (_virtualSessions.TryGetValue(vault.Id, out var existingVirtualSession))
        {
            if (vault.IsMounted && vault.IsReadOnlyMounted && existingVirtualSession.IsRunning
                && !string.IsNullOrWhiteSpace(vault.MountPath) && Directory.Exists(vault.MountPath))
                return vault.MountPath;

            // Windows can remove a drive independently (session end, runtime
            // restart, manual Dokany unmount). Do not leave a stale logical
            // session that prevents the vault from being opened again.
            existingVirtualSession.Dispose();
            _virtualSessions.TryRemove(vault.Id, out _);
            vault.IsMounted = false;
            vault.IsReadOnlyMounted = false;
            vault.MountPath = null;
            vault.SessionExpiresUtc = null;
        }
        if (vault.IsMounted || _sessions.ContainsKey(vault.Id) || _virtualSessions.ContainsKey(vault.Id))
        {
            LastError = "La bóveda ya tiene una sesión abierta.";
            return null;
        }
        if (!VaultFormatV3.IsFormat(vault.VaultFilePath))
        {
            LastError = "El montaje virtual necesita PAVLT003. Abre esta bóveda para editar y bloquéala una vez para migrarla.";
            return null;
        }

        VaultFormatV3.OpenedVault? opened = null;
        Dokan? dokan = null;
        DokanInstance? instance = null;
        try
        {
            var journalPath = GetJournalPath(vault.Id);
            var alternateJournalPath = journalPath + ".next";
            (opened, var sourcePath) = await OpenNewestUsableVaultAsync(vault, password, journalPath,
                alternateJournalPath);
            LastMountUsedJournal = !PathsEqual(sourcePath, vault.VaultFilePath);

            var driveLetter = FindAvailableVirtualDriveLetter();
            if (driveLetter is null)
                throw new IOException("No hay ninguna letra de unidad disponible para montar la bóveda.");
            var mountPoint = $"{driveLetter}:\\";
            var operations = new VaultReadOnlyFileSystem(opened, () => vault.LastAccessUtc = DateTime.UtcNow);
            (dokan, instance) = await Task.Run(() =>
            {
                var mountDokan = new Dokan(new NullLogger());
                var mountInstance = new DokanInstanceBuilder(mountDokan)
                    .ConfigureOptions(options =>
                    {
                        options.MountPoint = mountPoint;
                        options.Options = DokanOptions.WriteProtection | DokanOptions.CurrentSession;
                        options.TimeOut = TimeSpan.FromSeconds(30);
                        options.AllocationUnitSize = 4096;
                        options.SectorSize = 512;
                    })
                    .Build(operations);
                return (mountDokan, mountInstance);
            });
            if (!instance.IsFileSystemRunning())
                throw new IOException("Dokany no pudo iniciar la unidad virtual.");

            var session = new VirtualVaultSession(mountPoint, opened, dokan, instance);
            opened = null;
            dokan = null;
            instance = null;
            if (!_virtualSessions.TryAdd(vault.Id, session))
            {
                session.Dispose();
                LastError = "Ya existe un montaje virtual para esta bóveda.";
                return null;
            }

            vault.MountPath = mountPoint;
            vault.IsReadOnlyMounted = true;
            vault.IsMounted = true;
            vault.LastAccessUtc = DateTime.UtcNow;
            vault.SessionExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, vault.AutoLockMinutes));
            return mountPoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or CryptographicException or JsonException or ArgumentException
                                   or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                                   or DokanException)
        {
            System.Diagnostics.Debug.WriteLine($"Error montando bóveda virtual: {ex.Message}");
            LastError = ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                ? "Falta el componente Dokany requerido para crear la unidad virtual. Reinstala ProtectedApp 1.4.52 o posterior."
                : ex.Message;
            return null;
        }
        finally
        {
            try { instance?.Dispose(); } catch { }
            try { dokan?.Dispose(); } catch { }
            opened?.Dispose();
        }
    }

    public async Task<string?> MountReadWriteVaultAsync(VaultContainer vault, string password)
    {
        LastError = null;
        LastMountUsedJournal = false;
        if (vault is null || string.IsNullOrWhiteSpace(vault.VaultFilePath))
        {
            LastError = "La bóveda no tiene un contenedor cifrado asociado.";
            return null;
        }
        if (!VaultFormatV3.IsFormat(vault.VaultFilePath))
        {
            LastError = "La edición virtual necesita PAVLT003. Abre y bloquea esta bóveda una vez para migrarla.";
            return null;
        }
        // A previous interrupted commit can leave a sidecar with this exact
        // pattern. It is never an authoritative vault and must not survive
        // into the next mount attempt.
        VaultFormatV3.DeleteStaleWriteTemporaries(vault.VaultFilePath);
        if (vault.IsMounted || _sessions.ContainsKey(vault.Id) || _virtualSessions.ContainsKey(vault.Id))
        {
            LastError = "La bóveda ya tiene una sesión abierta.";
            return null;
        }

        VaultFormatV3.OpenedVault? opened = null;
        Dokan? dokan = null;
        DokanInstance? instance = null;
        try
        {
            var journalPath = GetJournalPath(vault.Id);
            var alternateJournalPath = journalPath + ".next";
            var (usableVault, sourcePath) = await OpenNewestUsableVaultAsync(vault, password, journalPath,
                alternateJournalPath);
            opened = usableVault;
            LastMountUsedJournal = !PathsEqual(sourcePath, vault.VaultFilePath);
            var journalWritePath = PathsEqual(sourcePath, journalPath) ? alternateJournalPath : journalPath;
            var driveLetter = FindAvailableVirtualDriveLetter()
                ?? throw new IOException("No hay ninguna letra de unidad disponible para montar la bóveda.");
            var mountPoint = $"{driveLetter}:\\";
            var openedVault = opened;
            var operations = new VaultReadWriteFileSystem(openedVault, () => vault.LastAccessUtc = DateTime.UtcNow);
            operations.JournalWriter = async sources =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(journalWritePath)!);
                await Task.Run(() => VaultFormatV3.WriteFromVirtualEntriesAsync(vault, sources, journalWritePath,
                    openedVault.PasswordKey, openedVault.Salt, openedVault.DataKey, createRecoveryBackup: false));
            };
            (dokan, instance) = await Task.Run(() =>
            {
                var mountDokan = new Dokan(new NullLogger());
                var mountInstance = new DokanInstanceBuilder(mountDokan)
                    .ConfigureOptions(options =>
                    {
                        options.MountPoint = mountPoint;
                        options.Options = DokanOptions.CurrentSession;
                        options.TimeOut = TimeSpan.FromSeconds(30);
                        options.AllocationUnitSize = 4096;
                        options.SectorSize = 512;
                    })
                    .Build(operations);
                return (mountDokan, mountInstance);
            });
            if (!instance.IsFileSystemRunning()) throw new IOException("Dokany no pudo iniciar la unidad virtual editable.");

            var session = new VirtualVaultSession(mountPoint, opened, dokan, instance, operations,
                vault.VaultFilePath, journalPath, alternateJournalPath);
            opened = null;
            dokan = null;
            instance = null;
            if (!_virtualSessions.TryAdd(vault.Id, session))
            {
                session.Dispose();
                LastError = "Ya existe un montaje virtual para esta bóveda.";
                return null;
            }
            vault.MountPath = mountPoint;
            vault.IsReadOnlyMounted = false;
            vault.IsMounted = true;
            vault.LastAccessUtc = DateTime.UtcNow;
            vault.SessionExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, vault.AutoLockMinutes));
            return mountPoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or CryptographicException or JsonException or ArgumentException
                                   or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                                   or DokanException)
        {
            LastError = ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                ? "Falta el componente Dokany requerido para crear la unidad virtual."
                : ex.Message;
            return null;
        }
        finally
        {
            try { instance?.Dispose(); } catch { }
            try { dokan?.Dispose(); } catch { }
            opened?.Dispose();
        }
    }

    public async Task<string?> MountVaultAsync(VaultContainer vault, string password)
    {
        LastError = null;
        LastMountUsedJournal = false;
        if (vault is null || string.IsNullOrWhiteSpace(vault.VaultFilePath))
        {
            LastError = "La bóveda no tiene un contenedor cifrado asociado.";
            return null;
        }
        if (vault.IsMounted && Directory.Exists(vault.MountPath)) return vault.MountPath;

        VaultReadResult? read = null;
        VaultFormatV3.OpenedVault? openedV3 = null;
        var workingDirectory = GetWorkingDirectory(vault.Id);
        var stagingDirectory = workingDirectory + ".opening-" + Guid.NewGuid().ToString("N");
        try
        {
            var recoverExistingWork = Directory.Exists(workingDirectory)
                && Directory.EnumerateFileSystemEntries(workingDirectory).Any();
            if (Directory.Exists(workingDirectory) && !recoverExistingWork)
                Directory.Delete(workingDirectory, true);

            if (VaultFormatV3.IsFormat(vault.VaultFilePath))
            {
                openedV3 = await VaultFormatV3.OpenAsync(vault.VaultFilePath, password);
                if (openedV3.Vault.Id != vault.Id) return null;
                VaultFormatV3.DeleteStaleWriteTemporaries(vault.VaultFilePath);
                if (!recoverExistingWork)
                {
                    Directory.CreateDirectory(stagingDirectory);
                    await VaultFormatV3.ExtractAsync(openedV3, stagingDirectory);
                    Directory.Move(stagingDirectory, workingDirectory);
                }
            }
            else
            {
                read = await ReadEncryptedVaultAsync(vault.VaultFilePath, password);
                if (read.Vault.Id != vault.Id) return null;
                if (!recoverExistingWork)
                {
                    Directory.CreateDirectory(stagingDirectory);
                    await ExtractArchiveAsync(read.ArchiveBytes, stagingDirectory);
                    Directory.Move(stagingDirectory, workingDirectory);
                }
            }

            var sessionKey = openedV3?.PasswordKey.ToArray() ?? read!.Key.ToArray();
            var sessionSalt = openedV3?.Header.Salt.ToArray() ?? read!.Salt.ToArray();
            var sessionDataKey = openedV3?.DataKey.ToArray();

            var session = new VaultSession(vault.VaultFilePath, workingDirectory,
                sessionKey, sessionSalt, sessionDataKey);
            if (!_sessions.TryAdd(vault.Id, session))
            {
                session.Dispose();
                LastError = "Ya existe una operación abierta para esta bóveda.";
                return null;
            }

            vault.MountPath = workingDirectory;
            vault.IsMounted = true;
            vault.LastAccessUtc = DateTime.UtcNow;
            vault.SessionExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, vault.AutoLockMinutes));
            return workingDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or CryptographicException or JsonException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Error abriendo bóveda: {ex.Message}");
            LastError = ex.Message;
            return null;
        }
        finally
        {
            read?.Dispose();
            openedV3?.Dispose();
            if (Directory.Exists(stagingDirectory))
            {
                try { Directory.Delete(stagingDirectory, true); } catch { }
            }
        }
    }

    public async Task<bool> UnmountVaultAsync(VaultContainer vault)
    {
        LastError = null;
        if (vault is not null && _virtualSessions.TryGetValue(vault.Id, out var virtualSession))
        {
            try
            {
                if (virtualSession.IsWritable)
                    await virtualSession.CommitAndDisposeAsync(vault);
                else
                    await virtualSession.DisposeAsync();
                _virtualSessions.TryRemove(vault.Id, out _);
                vault.IsMounted = false;
                vault.IsReadOnlyMounted = false;
                vault.MountPath = null;
                vault.SessionExpiresUtc = null;
                if (!string.IsNullOrWhiteSpace(vault.VaultFilePath) && File.Exists(vault.VaultFilePath))
                    UpdateRuntimeMetadata(vault, vault.VaultFilePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DokanException
                                       or ObjectDisposedException or InvalidDataException
                                       or CryptographicException or JsonException)
            {
                _virtualSessions.TryRemove(vault.Id, out _);
                vault.IsMounted = false;
                vault.IsReadOnlyMounted = false;
                vault.MountPath = null;
                vault.SessionExpiresUtc = null;
                LastError = $"No se pudo desmontar la unidad virtual: {ex.Message}";
                return false;
            }
        }
        if (vault is null || !_sessions.TryGetValue(vault.Id, out var session))
        {
            LastError = "No existe una sesión abierta para esta bóveda.";
            return false;
        }
        try
        {
            if (!Directory.Exists(session.WorkingDirectory))
            {
                LastError = "La carpeta de trabajo ya no existe.";
                return false;
            }
            await VaultFormatV3.WriteFromDirectoryAsync(vault, session.WorkingDirectory,
                session.VaultFilePath, session.Key, session.Salt, session.DataKey, createRecoveryBackup: true);

            Directory.Delete(session.WorkingDirectory, true);
            _sessions.TryRemove(vault.Id, out _);
            session.Dispose();
            vault.IsMounted = false;
            vault.IsReadOnlyMounted = false;
            vault.MountPath = null;
            vault.SessionExpiresUtc = null;
            UpdateRuntimeMetadata(vault, session.VaultFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or CryptographicException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Error cerrando bóveda: {ex.Message}");
            LastError = ex.Message;
            return false;
        }
    }

    public string GetWorkingDirectory(Guid vaultId) => Path.Combine(GetWorkingRoot(), vaultId.ToString("N"));

    private static string GetJournalPath(Guid vaultId)
    {
        var overrideFolder = Environment.GetEnvironmentVariable("PROTECTEDAPP_DATA_DIR");
        var protectedAppFolder = string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp")
            : Path.GetFullPath(overrideFolder);
        var journalDirectory = Path.Combine(protectedAppFolder, "VaultJournal");
        EnsurePrivateVaultDirectory(journalDirectory);
        return Path.Combine(journalDirectory, vaultId.ToString("N") + ".pavault");
    }

    private static string? GetNewestExistingPath(params string[] paths) => paths
        .Where(File.Exists)
        .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
        .FirstOrDefault();

    public bool HasPendingJournal(VaultContainer vault)
    {
        if (vault is null || vault.Id == Guid.Empty) return false;
        var journalPath = GetJournalPath(vault.Id);
        return File.Exists(journalPath) || File.Exists(journalPath + ".next");
    }

    private static async Task<(VaultFormatV3.OpenedVault Opened, string Path)> OpenNewestUsableVaultAsync(
        VaultContainer vault, string password, string journalPath, string alternateJournalPath)
    {
        Exception? lastError = null;
        var paths = new List<string> { alternateJournalPath, journalPath };
        if (!string.IsNullOrWhiteSpace(vault.VaultFilePath)) paths.Add(vault.VaultFilePath);
        var candidates = paths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        foreach (var path in candidates)
        {
            VaultFormatV3.OpenedVault? opened = null;
            try
            {
                opened = await VaultFormatV3.OpenAsync(path, password);
                if (opened.Vault.Id != vault.Id)
                    throw new InvalidDataException("El contenedor pertenece a otra bóveda.");
                VaultFormatV3.DeleteStaleWriteTemporaries(path);
                return (opened, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or CryptographicException or JsonException)
            {
                opened?.Dispose();
                lastError = ex;
            }
        }

        throw lastError ?? new FileNotFoundException("No se encontró ningún contenedor de bóveda válido.");
    }

    public VaultBackupInfo InspectVaultBackup(VaultContainer vault)
    {
        var primaryPath = string.IsNullOrWhiteSpace(vault.VaultFilePath)
            ? string.Empty
            : Path.GetFullPath(vault.VaultFilePath);
        var backupPath = primaryPath.Length == 0 ? string.Empty : primaryPath + ".bak";
        var primary = GetFileSnapshot(primaryPath);
        var backup = GetFileSnapshot(backupPath);
        return new VaultBackupInfo(
            primaryPath,
            backupPath,
            primary.Exists,
            primary.Exists && HasStructurallyValidVaultEnvelope(primaryPath),
            backup.Exists,
            backup.Exists && HasStructurallyValidVaultEnvelope(backupPath),
            primary.SizeBytes,
            backup.SizeBytes,
            primary.ModifiedUtc,
            backup.ModifiedUtc);
    }

    public async Task<VaultBackupValidation> ValidateVaultBackupAsync(VaultContainer vault, string password)
    {
        LastError = null;
        var info = InspectVaultBackup(vault);
        if (!info.BackupExists)
            return new(false, false, null, "No existe una copia cifrada anterior.");

        try
        {
            var backupVault = await ReadVaultMetadataAuthenticatedAsync(info.BackupPath, password);
            if (backupVault.Id != vault.Id)
                return new(false, false, null, "La copia pertenece a otra bóveda y no puede utilizarse.");

            var primaryValid = false;
            if (info.PrimaryExists)
            {
                try
                {
                    var primaryVault = await ReadVaultMetadataAuthenticatedAsync(info.PrimaryPath, password);
                    primaryValid = primaryVault.Id == vault.Id;
                }
                catch (Exception ex) when (IsVaultDataException(ex))
                {
                    primaryValid = false;
                }
            }

            return new(true, primaryValid, backupVault,
                primaryValid
                    ? "La copia y el contenedor actual son válidos. Restaurar volverá a la versión anterior."
                    : "La copia anterior es válida y puede sustituir al contenedor dañado o ausente.");
        }
        catch (Exception ex) when (IsVaultDataException(ex))
        {
            LastError = ex.Message;
            return new(false, false, null, "La contraseña no abre la copia anterior o esta ha sido modificada.");
        }
    }

    public async Task<VaultBackupRestoreResult> RestoreVaultBackupAsync(VaultContainer vault, string password)
    {
        LastError = null;
        var validation = await ValidateVaultBackupAsync(vault, password);
        if (!validation.BackupValid || validation.BackupVault is null)
        {
            LastError = validation.Message;
            return new(false, null, validation.Message);
        }

        var info = InspectVaultBackup(vault);
        var directory = Path.GetDirectoryName(info.PrimaryPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new(false, null, "La carpeta del contenedor principal no existe.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(info.PrimaryPath)}.restore-{Guid.NewGuid():N}.tmp");
        var preservedPath = info.PrimaryExists
            ? CreateUniquePreservedPath(info.PrimaryPath, validation.PrimaryValid ? "replaced" : "corrupt")
            : null;
        var primaryMoved = false;
        var replacementInstalled = false;
        try
        {
            await CopyFileDurablyAsync(info.BackupPath, temporaryPath);
            var verifiedCopy = await ReadVaultMetadataAuthenticatedAsync(temporaryPath, password);
            if (verifiedCopy.Id != vault.Id)
                throw new InvalidDataException("La copia verificada pertenece a otra bóveda.");

            if (info.PrimaryExists && preservedPath is not null)
            {
                File.Move(info.PrimaryPath, preservedPath);
                primaryMoved = true;
            }
            File.Move(temporaryPath, info.PrimaryPath);
            replacementInstalled = true;

            var finalVerification = await ReadVaultMetadataAuthenticatedAsync(info.PrimaryPath, password);
            if (finalVerification.Id != vault.Id)
                throw new InvalidDataException("El contenedor restaurado no superó la verificación final.");

            var restored = validation.BackupVault;
            vault.Name = restored.Name;
            vault.Description = restored.Description;
            vault.AutoLockMinutes = restored.AutoLockMinutes;
            vault.CreatedUtc = restored.CreatedUtc;
            vault.ModifiedUtc = restored.ModifiedUtc;
            UpdateRuntimeMetadata(vault, info.PrimaryPath);
            try { File.Delete(info.BackupPath); } catch { }
            return new(true, preservedPath, null);
        }
        catch (Exception ex) when (IsVaultDataException(ex) || ex is InvalidOperationException)
        {
            var rollbackError = string.Empty;
            try
            {
                if (replacementInstalled && File.Exists(info.PrimaryPath)) File.Delete(info.PrimaryPath);
                if (primaryMoved && preservedPath is not null && File.Exists(preservedPath))
                    File.Move(preservedPath, info.PrimaryPath);
            }
            catch (Exception rollbackException)
            {
                rollbackError = $" No se pudo restaurar automáticamente el archivo anterior: {rollbackException.Message}";
            }
            LastError = ex.Message + rollbackError;
            return new(false, preservedPath, LastError);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public bool DeleteVaultBackup(VaultContainer vault)
    {
        LastError = null;
        try
        {
            var info = InspectVaultBackup(vault);
            if (!info.BackupExists) return true;
            if ((File.GetAttributes(info.BackupPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("La copia es un enlace o punto de análisis y no se eliminará automáticamente.");
            File.Delete(info.BackupPath);
            return true;
        }
        catch (Exception ex) when (IsVaultDataException(ex))
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool HasPendingVirtualChanges(VaultContainer vault) =>
        vault is not null
        && _virtualSessions.TryGetValue(vault.Id, out var session)
        && session.HasPendingChanges;

    public VaultPermanentDeleteResult DeleteVaultPermanently(VaultContainer vault, bool deleteCopies,
        string? scheduledBackupRoot)
    {
        LastError = null;
        if (vault is null || vault.IsMounted || string.IsNullOrWhiteSpace(vault.VaultFilePath))
            return new(false, 0, 0, "La bóveda debe estar cerrada y tener un contenedor asociado.");

        try
        {
            var primaryPath = Path.GetFullPath(vault.VaultFilePath);
            if (!File.Exists(primaryPath)) return new(false, 0, 0, "El contenedor cifrado ya no existe.");
            if ((File.GetAttributes(primaryPath) & FileAttributes.ReparsePoint) != 0)
                return new(false, 0, 0, "El contenedor es un enlace o punto de análisis y no se eliminará automáticamente.");

            var copies = new List<string>();
            if (deleteCopies)
            {
                var backup = InspectVaultBackup(vault);
                if (backup.BackupExists) copies.Add(backup.BackupPath);
                copies.AddRange(ListScheduledBackups(vault, scheduledBackupRoot).Select(item => item.Path));
            }
            foreach (var copy in copies.Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(copy) && (File.GetAttributes(copy) & FileAttributes.ReparsePoint) != 0)
                    return new(false, 0, 0, "Una copia es un enlace o punto de análisis y no se eliminará automáticamente.");

            File.Delete(primaryPath);
            var deletedCopies = 0;
            var failedCopies = 0;
            foreach (var copy in copies.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(copy)) continue;
                    // Re-check at the point of use. A path may have been replaced
                    // after the preflight validation, particularly in a user-writable
                    // scheduled-backup location.
                    if ((File.GetAttributes(copy) & FileAttributes.ReparsePoint) != 0)
                    {
                        failedCopies++;
                        continue;
                    }
                    File.Delete(copy);
                    deletedCopies++;
                }
                catch { failedCopies++; }
            }
            return new(true, deletedCopies, failedCopies, failedCopies == 0 ? null
                : "El contenedor se eliminó, pero una o más copias no pudieron eliminarse.");
        }
        catch (Exception ex) when (IsVaultDataException(ex) || ex is InvalidOperationException)
        {
            LastError = ex.Message;
            return new(false, 0, 0, ex.Message);
        }
    }

    public IReadOnlyList<VaultRecoveryItem> FindPendingRecoveryWork(IEnumerable<VaultContainer> vaults)
    {
        var result = new List<VaultRecoveryItem>();
        var root = GetWorkingRoot();
        if (!Directory.Exists(root)) return result;

        var knownVaults = vaults
            .Where(vault => vault.Id != Guid.Empty)
            .GroupBy(vault => vault.Id)
            .ToDictionary(group => group.Key, group => group.First());
        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { return result; }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var openingMarker = name.IndexOf(".opening-", StringComparison.OrdinalIgnoreCase);
            var idText = openingMarker >= 0 ? name[..openingMarker] : name;
            if (!Guid.TryParseExact(idText, "N", out var vaultId)) continue;
            var temporary = openingMarker >= 0;
            knownVaults.TryGetValue(vaultId, out var vault);
            if (!temporary && vault?.IsMounted == true) continue;

            var inspection = InspectWorkingDirectory(directory);
            if (inspection.FileCount == 0 && inspection.DirectoryCount == 0
                && inspection.IsAccessible && !inspection.HasUnsafeEntries) continue;
            result.Add(new VaultRecoveryItem
            {
                WorkingDirectory = Path.GetFullPath(directory),
                DisplayName = temporary
                    ? $"{vault?.Name ?? "Bóveda desconocida"} · apertura incompleta"
                    : vault?.Name ?? $"Trabajo no asociado ({vaultId:N})",
                VaultId = vaultId,
                Vault = vault,
                IsTemporaryOpening = temporary,
                IsAccessible = inspection.IsAccessible,
                HasUnsafeEntries = inspection.HasUnsafeEntries,
                FileCount = inspection.FileCount,
                DirectoryCount = inspection.DirectoryCount,
                SizeBytes = inspection.SizeBytes,
                LastModifiedUtc = inspection.LastModifiedUtc,
                InspectionError = inspection.Error
            });
        }

        return result.OrderByDescending(item => item.LastModifiedUtc).ToArray();
    }

    public VaultRecoveryCheck ValidateRecovery(VaultRecoveryItem item)
    {
        LastError = null;
        if (item.Vault is null || item.IsTemporaryOpening)
            return new(false, "Este trabajo no tiene una bóveda asociada recuperable. Puedes revisar sus archivos o descartarlo.");
        if (item.Vault.Id != item.VaultId || !PathsEqual(item.WorkingDirectory, GetWorkingDirectory(item.VaultId)))
            return new(false, "La carpeta de trabajo no coincide con la ubicación privada esperada.");
        if (string.IsNullOrWhiteSpace(item.Vault.VaultFilePath) || !File.Exists(item.Vault.VaultFilePath))
            return new(false, "No se encuentra el contenedor cifrado asociado.");

        var inspection = InspectWorkingDirectory(item.WorkingDirectory);
        if (!inspection.IsAccessible)
            return new(false, inspection.Error ?? "No se puede leer la carpeta de trabajo.");
        if (inspection.HasUnsafeEntries)
            return new(false, "La carpeta contiene enlaces o puntos de montaje y requiere revisión manual.");
        if (inspection.FileCount + inspection.DirectoryCount > MaximumEntries)
            return new(false, $"Hay más de {MaximumEntries:N0} elementos y esta versión no puede guardarlos.");
        if (inspection.SizeBytes > MaximumExpandedBytes)
            return new(false, "El contenido supera el límite de 1 GB de esta versión.");

        var destination = Path.GetDirectoryName(Path.GetFullPath(item.Vault.VaultFilePath));
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
            return new(false, "La carpeta que contiene el archivo cifrado ya no existe.");
        var requiredBytes = Math.Min(MaximumExpandedBytes + 64L * 1024 * 1024,
            inspection.SizeBytes + 64L * 1024 * 1024);
        try
        {
            var driveRoot = Path.GetPathRoot(destination);
            if (!string.IsNullOrWhiteSpace(driveRoot))
            {
                var drive = new DriveInfo(driveRoot);
                if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                    return new(false,
                        $"No hay espacio suficiente: se necesitan aproximadamente {VaultRecoveryItem.FormatBytes(requiredBytes)} y hay {VaultRecoveryItem.FormatBytes(drive.AvailableFreeSpace)} libres.");
            }

            var probe = Path.Combine(destination, $".protectedapp-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
                stream.WriteByte(0);
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"No se puede escribir junto al contenedor cifrado: {ex.Message}");
        }

        return new(true,
            $"Se guardarán {inspection.FileCount:N0} archivos ({VaultRecoveryItem.FormatBytes(inspection.SizeBytes)}) en el contenedor cifrado.");
    }

    public async Task<bool> RecoverWorkingDirectoryAsync(VaultRecoveryItem item, string password)
    {
        var check = ValidateRecovery(item);
        if (!check.Success)
        {
            LastError = check.Message;
            return false;
        }
        var vault = item.Vault!;
        if (await MountVaultAsync(vault, password) is null) return false;
        if (await UnmountVaultAsync(vault)) return true;
        LastError ??= "No se pudo reemplazar el contenedor cifrado; la carpeta de trabajo se ha conservado.";
        return false;
    }

    public bool DiscardRecoveryWork(VaultRecoveryItem item)
    {
        LastError = null;
        try
        {
            if (!IsDirectRecoveryDirectory(item.WorkingDirectory))
                throw new InvalidDataException("La ruta no pertenece al área privada de recuperación.");
            var inspection = InspectWorkingDirectory(item.WorkingDirectory);
            if (!inspection.IsAccessible) throw new IOException(inspection.Error ?? "No se puede inspeccionar la carpeta.");
            if (inspection.HasUnsafeEntries)
                throw new InvalidDataException("La carpeta contiene enlaces o puntos de montaje y no puede eliminarse automáticamente.");
            if (item.Vault?.IsMounted == true)
                throw new InvalidOperationException("La bóveda está abierta y no se puede descartar su trabajo.");
            Directory.Delete(item.WorkingDirectory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or InvalidOperationException or ArgumentException)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private static string GetWorkingRoot()
    {
        var overrideFolder = Environment.GetEnvironmentVariable("PROTECTEDAPP_DATA_DIR");
        var protectedAppFolder = string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp")
            : Path.GetFullPath(overrideFolder);
        var workingRoot = Path.Combine(protectedAppFolder, "VaultWork");
        EnsurePrivateVaultDirectory(workingRoot);
        return workingRoot;
    }

    private static void EnsurePrivateVaultDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null) throw new InvalidOperationException("No se pudo identificar al usuario propietario de la bóveda.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[]
                 {
                     user,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        }
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        HardenExistingVaultEntries(path, user);
    }

    private static void HardenExistingVaultEntries(string root, SecurityIdentifier user)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(pending.Pop()).ToArray(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var directorySecurity = CreatePrivateVaultDirectorySecurity(user);
                        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(entry), directorySecurity);
                        pending.Push(entry);
                    }
                    else FileSystemAclExtensions.SetAccessControl(new FileInfo(entry), CreatePrivateVaultFileSecurity(user));
                }
                catch { }
            }
        }
    }

    private static FileSecurity CreatePrivateVaultFileSecurity(SecurityIdentifier user)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static DirectorySecurity CreatePrivateVaultDirectorySecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool IsDirectRecoveryDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullPath);
        var root = Path.GetFullPath(GetWorkingRoot()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(fullPath);
        var marker = name.IndexOf(".opening-", StringComparison.OrdinalIgnoreCase);
        return Guid.TryParseExact(marker >= 0 ? name[..marker] : name, "N", out _);
    }

    private static WorkingDirectoryInspection InspectWorkingDirectory(string path)
    {
        if (!IsDirectRecoveryDirectory(path))
            return new(false, true, 0, 0, 0, default, "La ruta de recuperación no es válida.");
        try
        {
            if (!Directory.Exists(path))
                return new(false, false, 0, 0, 0, default, "La carpeta de trabajo ya no existe.");
            var rootInfo = new DirectoryInfo(path);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return new(true, true, 0, 0, 0, rootInfo.LastWriteTimeUtc,
                    "La carpeta de trabajo es un enlace o punto de montaje.");

            var stack = new Stack<string>();
            stack.Push(path);
            var files = 0;
            var directories = 0;
            long bytes = 0;
            var lastModified = rootInfo.LastWriteTimeUtc;
            var unsafeEntries = false;
            while (stack.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(stack.Pop(), "*", SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        unsafeEntries = true;
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories++;
                        var info = new DirectoryInfo(entry);
                        if (info.LastWriteTimeUtc > lastModified) lastModified = info.LastWriteTimeUtc;
                        stack.Push(entry);
                    }
                    else
                    {
                        files++;
                        var info = new FileInfo(entry);
                        bytes = checked(bytes + info.Length);
                        if (info.LastWriteTimeUtc > lastModified) lastModified = info.LastWriteTimeUtc;
                    }
                    if (files + directories > MaximumEntries + 1) break;
                }
                if (files + directories > MaximumEntries + 1) break;
            }
            return new(true, unsafeEntries, files, directories, bytes, lastModified, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or OverflowException)
        {
            return new(false, false, 0, 0, 0, default, $"No se pudo inspeccionar: {ex.Message}");
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private char? FindAvailableVirtualDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        var preferred = PreferredVirtualDriveLetter is { } configured
            ? char.ToUpperInvariant(configured)
            : '\0';
        if (preferred >= 'D' && preferred <= 'Z' && !used.Contains(preferred))
            return preferred;
        for (var candidate = 'Z'; candidate >= 'E'; candidate--)
            if (!used.Contains(candidate)) return candidate;
        if (!used.Contains('D')) return 'D';
        return null;
    }

    private sealed record WorkingDirectoryInspection(bool IsAccessible, bool HasUnsafeEntries, int FileCount,
        int DirectoryCount, long SizeBytes, DateTime LastModifiedUtc, string? Error);

    private static FileSnapshot GetFileSnapshot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new(false, 0, null);
            var info = new FileInfo(path);
            return new(true, info.Length, info.LastWriteTimeUtc);
        }
        catch { return new(false, 0, null); }
    }

    private static bool HasStructurallyValidVaultEnvelope(string path)
    {
        if (VaultFormatV3.IsFormat(path)) return VaultFormatV3.HasStructurallyValidEnvelope(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var minimum = Signature.Length + 1 + sizeof(int) + SaltSize + NonceSize + TagSize + sizeof(long) + 1;
            if (stream.Length < minimum || stream.Length > MaximumArchiveBytes + 1024) return false;
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(Signature.Length).SequenceEqual(Signature) || reader.ReadByte() != FormatVersion)
                return false;
            if (reader.ReadInt32() != Iterations) return false;
            if (reader.ReadBytes(SaltSize).Length != SaltSize
                || reader.ReadBytes(NonceSize).Length != NonceSize
                || reader.ReadBytes(TagSize).Length != TagSize) return false;
            var ciphertextLength = reader.ReadInt64();
            return ciphertextLength > 0
                && ciphertextLength <= MaximumArchiveBytes
                && ciphertextLength == stream.Length - stream.Position;
        }
        catch { return false; }
    }

    private static async Task CopyFileDurablyAsync(string sourcePath, string destinationPath)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination);
        await destination.FlushAsync();
        destination.Flush(flushToDisk: true);
    }

    private static string CreateUniquePreservedPath(string primaryPath, string label)
    {
        var directory = Path.GetDirectoryName(primaryPath)!;
        var extension = Path.GetExtension(primaryPath);
        var stem = Path.GetFileNameWithoutExtension(primaryPath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        for (var suffix = 0; ; suffix++)
        {
            var discriminator = suffix == 0 ? string.Empty : $"-{suffix}";
            var candidate = Path.Combine(directory, $"{stem}.{label}-{timestamp}{discriminator}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static bool IsVaultDataException(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidDataException or CryptographicException or JsonException or ArgumentException
        or NotSupportedException;

    private sealed record FileSnapshot(bool Exists, long SizeBytes, DateTime? ModifiedUtc);

    private static async Task<VaultReadResult> ReadEncryptedVaultAsync(string vaultFilePath, string password)
    {
        if (!File.Exists(vaultFilePath) || string.IsNullOrEmpty(password))
            throw new InvalidDataException("La bóveda no existe o no se indicó contraseña.");
        var info = new FileInfo(vaultFilePath);
        if (info.Length <= 0 || info.Length > MaximumArchiveBytes + 1024)
            throw new InvalidDataException("El tamaño de la bóveda no es válido.");

        byte[]? fileBytes = null;
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            fileBytes = await File.ReadAllBytesAsync(vaultFilePath);
            var minimum = Signature.Length + 1 + sizeof(int) + SaltSize + NonceSize + TagSize + sizeof(long);
            if (fileBytes.Length < minimum) throw new InvalidDataException("La cabecera de la bóveda está incompleta.");

            using var stream = new MemoryStream(fileBytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(Signature.Length).SequenceEqual(Signature) || reader.ReadByte() != FormatVersion)
                throw new InvalidDataException("El formato de la bóveda no es compatible.");
            var iterations = reader.ReadInt32();
            if (iterations != Iterations) throw new InvalidDataException("La derivación de clave no es compatible.");
            var salt = ReadExact(reader, SaltSize);
            var nonce = ReadExact(reader, NonceSize);
            var tag = ReadExact(reader, TagSize);
            var ciphertextLength = reader.ReadInt64();
            if (ciphertextLength <= 0 || ciphertextLength > MaximumArchiveBytes || ciphertextLength != stream.Length - stream.Position)
                throw new InvalidDataException("La longitud cifrada no es válida.");
            var ciphertext = ReadExact(reader, checked((int)ciphertextLength));

            key = DeriveKey(password, salt);
            plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildAdditionalData(salt));

            var vault = ReadManifest(plaintext);
            var result = new VaultReadResult(vault, plaintext, key, salt);
            plaintext = null;
            key = null;
            return result;
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("La contraseña es incorrecta o la bóveda ha sido modificada.");
        }
        finally
        {
            if (fileBytes is not null) CryptographicOperations.ZeroMemory(fileBytes);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<VaultContainer> ReadVaultMetadataAuthenticatedAsync(string vaultFilePath,
        string password)
    {
        if (VaultFormatV3.IsFormat(vaultFilePath))
        {
            using var opened = await VaultFormatV3.OpenAsync(vaultFilePath, password);
            return opened.Vault;
        }

        using var read = await ReadEncryptedVaultAsync(vaultFilePath, password);
        return read.Vault;
    }

    private static async Task WriteEncryptedVaultAtomicAsync(string path, byte[] archiveBytes, byte[] key, byte[] salt,
        bool createRecoveryBackup = true)
    {
        if (archiveBytes.LongLength <= 0 || archiveBytes.LongLength > MaximumArchiveBytes)
            throw new InvalidDataException("La bóveda supera el tamaño máximo admitido en esta versión.");
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[archiveBytes.Length];
        var tag = new byte[TagSize];
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("La ruta de la bóveda no es válida.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = path + ".bak";
        try
        {
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, archiveBytes, ciphertext, tag, BuildAdditionalData(salt));
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(Signature);
                writer.Write(FormatVersion);
                writer.Write(Iterations);
                writer.Write(salt);
                writer.Write(nonce);
                writer.Write(tag);
                writer.Write((long)ciphertext.Length);
                writer.Write(ciphertext);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                if (createRecoveryBackup)
                {
                    try { File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true); }
                    catch (PlatformNotSupportedException) { File.Move(temporaryPath, path, true); }
                }
                else
                {
                    File.Move(temporaryPath, path, true);
                }
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static async Task<byte[]> CreateEmptyArchiveAsync(VaultContainer vault)
    {
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            await WriteManifestAsync(archive, vault);
        return output.ToArray();
    }

    private static async Task<byte[]> CreateArchiveFromDirectoryAsync(VaultContainer vault, string root)
    {
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteManifestAsync(archive, vault);
            var count = 0;
            long expandedBytes = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if (++count > MaximumEntries) throw new InvalidDataException("La bóveda contiene demasiados elementos.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Las bóvedas no admiten enlaces ni puntos de montaje.");
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(path))
                {
                    if (!Directory.EnumerateFileSystemEntries(path).Any()) archive.CreateEntry(FilesPrefix + relative + "/");
                    continue;
                }
                var length = new FileInfo(path).Length;
                expandedBytes = checked(expandedBytes + length);
                if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("El contenido de la bóveda es demasiado grande.");
                var entry = archive.CreateEntry(FilesPrefix + relative, CompressionLevel.Optimal);
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                await using var target = entry.Open();
                await source.CopyToAsync(target);
            }
        }
        if (output.Length > MaximumArchiveBytes) throw new InvalidDataException("La bóveda cifrada es demasiado grande.");
        return output.ToArray();
    }

    private static async Task<byte[]> ReplaceManifestAsync(byte[] archiveBytes, VaultContainer vault)
    {
        await using var copy = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(archiveBytes, writable: false), ZipArchiveMode.Read))
        using (var target = new ZipArchive(copy, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteManifestAsync(target, vault);
            foreach (var entry in source.Entries.Where(entry => entry.FullName != ManifestEntryName))
            {
                var replacement = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                if (entry.FullName.EndsWith('/')) continue;
                await using var input = entry.Open();
                await using var output = replacement.Open();
                await input.CopyToAsync(output);
            }
        }
        return copy.ToArray();
    }

    private static async Task ExtractArchiveAsync(byte[] archiveBytes, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(new MemoryStream(archiveBytes, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumEntries + 1) throw new InvalidDataException("La bóveda contiene demasiados elementos.");
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == ManifestEntryName) continue;
            if (!entry.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("La bóveda contiene una entrada desconocida.");
            var relative = entry.FullName[FilesPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!targetPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La bóveda contiene una ruta no segura.");
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("El contenido expandido es demasiado grande.");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            await input.CopyToAsync(output);
        }
    }

    private static async Task WriteManifestAsync(ZipArchive archive, VaultContainer vault)
    {
        var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, VaultManifest.From(vault), JsonOptions);
    }

    private static VaultContainer ReadManifest(byte[] archiveBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(archiveBytes, writable: false), ZipArchiveMode.Read);
        var entry = archive.GetEntry(ManifestEntryName) ?? throw new InvalidDataException("La bóveda no contiene metadatos.");
        if (entry.Length <= 0 || entry.Length > 64 * 1024) throw new InvalidDataException("Los metadatos de la bóveda no son válidos.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<VaultManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("No se pudieron leer los metadatos de la bóveda.");
        if (manifest.Id == Guid.Empty || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("Los metadatos de la bóveda están incompletos.");
        return manifest.ToVault();
    }

    private static byte[] BuildAdditionalData(byte[] salt)
    {
        var result = new byte[Signature.Length + 1 + sizeof(int) + salt.Length];
        Signature.CopyTo(result, 0);
        result[Signature.Length] = FormatVersion;
        BitConverter.GetBytes(Iterations).CopyTo(result, Signature.Length + 1);
        salt.CopyTo(result, Signature.Length + 1 + sizeof(int));
        return result;
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        return bytes.Length == length ? bytes : throw new InvalidDataException("El archivo está truncado.");
    }

    private static void UpdateRuntimeMetadata(VaultContainer vault, string path)
    {
        vault.VaultFilePath = Path.GetFullPath(path);
        vault.SizeBytes = new FileInfo(path).Length;
        vault.LastAccessUtc = DateTime.UtcNow;
    }

    public void Dispose()
    {
        foreach (var session in _virtualSessions.Values)
        {
            try { session.Dispose(); } catch { }
        }
        _virtualSessions.Clear();
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }

    private sealed class VaultReadResult(VaultContainer vault, byte[] archiveBytes, byte[] key, byte[] salt) : IDisposable
    {
        public VaultContainer Vault { get; } = vault;
        public byte[] ArchiveBytes { get; } = archiveBytes;
        public byte[] Key { get; } = key;
        public byte[] Salt { get; } = salt;
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(ArchiveBytes);
            CryptographicOperations.ZeroMemory(Key);
            CryptographicOperations.ZeroMemory(Salt);
        }
    }

    private sealed class VaultSession(string vaultFilePath, string workingDirectory, byte[] key, byte[] salt,
        byte[]? dataKey) : IDisposable
    {
        public string VaultFilePath { get; } = vaultFilePath;
        public string WorkingDirectory { get; } = workingDirectory;
        public byte[] Key { get; } = key;
        public byte[] Salt { get; } = salt;
        public byte[]? DataKey { get; } = dataKey;
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            CryptographicOperations.ZeroMemory(Salt);
            if (DataKey is not null) CryptographicOperations.ZeroMemory(DataKey);
        }
    }

    private sealed class VirtualVaultSession(string mountPoint, VaultFormatV3.OpenedVault opened,
        Dokan dokan, DokanInstance instance, VaultReadWriteFileSystem? writableOperations = null,
        string? destinationPath = null, string? journalPath = null,
        string? alternateJournalPath = null) : IDisposable
    {
        private int _disposed;
        public string MountPoint { get; } = mountPoint;
        public bool IsWritable => writableOperations is not null;
        public bool HasPendingChanges => writableOperations?.HasChanges == true;
        public bool IsRunning
        {
            get
            {
                try { return Volatile.Read(ref _disposed) == 0 && instance.IsFileSystemRunning(); }
                catch { return false; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { instance.Dispose(); } catch { }
            try { dokan.RemoveMountPoint(MountPoint); } catch { }
            try { dokan.Dispose(); } catch { }
            try { writableOperations?.Dispose(); } catch { }
            // The opened vault owns the decrypted data-encryption key. Always
            // zero it, including after an externally disconnected drive.
            opened.Dispose();
        }

        public Task DisposeAsync() => Task.Run(Dispose);

        public async Task CommitAndDisposeAsync(VaultContainer vault)
        {
            if (writableOperations is null) { Dispose(); return; }
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                throw new ObjectDisposedException(nameof(VirtualVaultSession));
            try
            {
                // Persist a recovery journal before asking Dokany to close.
                // Cleanup and FlushFileBuffers are invoked by Dokany during
                // disposal; letting them synchronously write the same journal
                // could deadlock the unmount and leave a zero-byte .v3tmp.
                await writableOperations.SaveJournalAsync();
                writableOperations.SuspendJournalCallbacks();

                // Dokany waits for Explorer handles to close. Running that
                // wait on the UI thread made a contextual unmount freeze the
                // complete management panel until Explorer released them.
                await Task.Run(() =>
                {
                    try { instance.Dispose(); } catch { }
                    try { dokan.RemoveMountPoint(MountPoint); } catch { }
                });
                var sources = writableOperations.CreateSnapshot();
                await Task.Run(() => VaultFormatV3.WriteFromVirtualEntriesAsync(vault, sources,
                    destinationPath ?? opened.Path, opened.PasswordKey, opened.Salt, opened.DataKey,
                    createRecoveryBackup: true));
                try
                {
                    if (!string.IsNullOrWhiteSpace(journalPath) && File.Exists(journalPath)) File.Delete(journalPath);
                    if (!string.IsNullOrWhiteSpace(alternateJournalPath) && File.Exists(alternateJournalPath)) File.Delete(alternateJournalPath);
                }
                catch { /* El contenedor principal ya quedó verificado y es autoritativo. */ }
            }
            finally
            {
                try { dokan.Dispose(); } catch { }
                try { writableOperations.Dispose(); } catch { }
                opened.Dispose();
            }
        }
    }

    private sealed class VaultManifest
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int AutoLockMinutes { get; set; }
        public int InactivityAutoLockMinutes { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ModifiedUtc { get; set; }

        public static VaultManifest From(VaultContainer vault) => new()
        {
            Id = vault.Id,
            Name = vault.Name,
            Description = vault.Description,
            AutoLockMinutes = Math.Clamp(vault.AutoLockMinutes, 1, 10_080),
            InactivityAutoLockMinutes = Math.Clamp(vault.InactivityAutoLockMinutes, 0, 10_080),
            CreatedUtc = vault.CreatedUtc,
            ModifiedUtc = DateTime.UtcNow
        };

        public VaultContainer ToVault() => new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            AutoLockMinutes = Math.Clamp(AutoLockMinutes, 1, 10_080),
            InactivityAutoLockMinutes = Math.Clamp(InactivityAutoLockMinutes, 0, 10_080),
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }
}
