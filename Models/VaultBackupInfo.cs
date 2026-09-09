namespace ProtectedApp.Models;

using ProtectedApp.Services;

public sealed record VaultBackupInfo(
    string PrimaryPath,
    string BackupPath,
    bool PrimaryExists,
    bool PrimaryEnvelopeValid,
    bool BackupExists,
    bool BackupEnvelopeValid,
    long PrimarySizeBytes,
    long BackupSizeBytes,
    DateTime? PrimaryModifiedUtc,
    DateTime? BackupModifiedUtc)
{
    public bool RequiresAttention => BackupExists &&
        (!BackupEnvelopeValid || !PrimaryExists || !PrimaryEnvelopeValid);

    public bool CanAttemptRestore => BackupExists && BackupEnvelopeValid;

    public string StatusLabel => LocalizationService.T(!BackupExists
        ? "No hay copia anterior"
        : !BackupEnvelopeValid
            ? "La copia anterior está dañada o incompleta"
            : !PrimaryExists
                ? "Falta el contenedor principal; la copia puede recuperarse"
                : !PrimaryEnvelopeValid
                    ? "El contenedor principal está dañado; la copia puede recuperarse"
                    : "Copia cifrada anterior disponible");
}

public sealed record VaultBackupValidation(
    bool BackupValid,
    bool PrimaryValid,
    VaultContainer? BackupVault,
    string Message);

public sealed record VaultBackupRestoreResult(
    bool Success,
    string? PreservedPrimaryPath,
    string? Error);

public sealed record VaultIntegrityValidation(
    bool PasswordVerified,
    bool IsValid,
    int FileCount,
    int ChunkCount,
    long VerifiedBytes,
    string Message);

public sealed record VaultScheduledBackupResult(bool Success, string? Path, string? Error);

public sealed record VaultScheduledBackupInfo(int Count, DateTime? LastCreatedUtc, long TotalSizeBytes);

public sealed record VaultScheduledBackupCleanupResult(int DeletedCount, long FreedBytes, int FailedCount);

public sealed record VaultPermanentDeleteResult(bool PrimaryDeleted, int DeletedCopies, int FailedCopies, string? Error);

public sealed record VaultScheduledBackupVersion(string Path, DateTime CreatedUtc, long SizeBytes, bool EnvelopeValid);

public enum VaultScheduledBackupHealth
{
    NotConfigured,
    Pending,
    Overdue,
    Failed,
    Healthy
}
