namespace ProtectedApp.Models;

using ProtectedApp.Services;

public sealed class VaultRecoveryItem
{
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Guid VaultId { get; set; }
    public VaultContainer? Vault { get; set; }
    public bool IsTemporaryOpening { get; set; }
    public bool IsAccessible { get; set; }
    public bool HasUnsafeEntries { get; set; }
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public string? InspectionError { get; set; }

    public bool CanRecover => Vault is not null
        && !IsTemporaryOpening
        && IsAccessible
        && !HasUnsafeEntries
        && !string.IsNullOrWhiteSpace(Vault.VaultFilePath)
        && File.Exists(Vault.VaultFilePath);

    public string DetailLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InspectionError)) return InspectionError;
            var entries = FileCount + DirectoryCount;
            var modified = LastModifiedUtc == default
                ? LocalizationService.T("fecha desconocida")
                : $"{LocalizationService.T("modificado")} {LastModifiedUtc.ToLocalTime():dd/MM/yyyy HH:mm}";
            return $"{entries:N0} elementos · {FormatBytes(SizeBytes)} · {modified}";
        }
    }

    public string StatusLabel => LocalizationService.T(HasUnsafeEntries
        ? "Revisión manual necesaria"
        : IsTemporaryOpening
            ? "Apertura incompleta"
            : Vault is null
                ? "No asociada"
                : string.IsNullOrWhiteSpace(Vault.VaultFilePath) || !File.Exists(Vault.VaultFilePath)
                    ? "Falta el contenedor"
                    : "Lista para recuperar");

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{Math.Max(0, bytes)} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.00} GB"
    };
}

public sealed record VaultRecoveryCheck(bool Success, string Message);
