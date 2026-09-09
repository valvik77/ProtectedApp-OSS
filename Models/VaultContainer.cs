using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ProtectedApp.Services;

namespace ProtectedApp.Models;

/// <summary>
/// Representa un contenedor de bóveda cifrada.
/// Estructura: Metadatos + Contenido cifrado con AES-256-GCM
/// </summary>
public sealed class VaultContainer : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private bool _isEnabled = true;
    private string? _passwordHash;
    private string? _passwordSalt;
    private int _autoLockMinutes = 30;
    private int _inactivityAutoLockMinutes;
    private DateTime _createdUtc = DateTime.UtcNow;
    private DateTime _modifiedUtc = DateTime.UtcNow;
    private string? _vaultFilePath;
    private string? _mountPath;
    private bool _isMounted;
    private bool _isReadOnlyMounted;
    private bool _isClosing;
    private long _sizeBytes;
    private DateTime? _sessionExpiresUtc;
    private bool _hasRecoveryBackup;
    private bool _backupNeedsAttention;
    private bool _backupCanRestore;
    private bool _hasPendingJournal;
    private bool _scheduledBackupConfigured;
    private int _scheduledBackupCount;
    private DateTime? _lastScheduledBackupUtc;
    private string? _scheduledBackupDirectory;
    private long _scheduledBackupSizeBytes;
    private VaultScheduledBackupHealth _scheduledBackupHealth;
    private string? _scheduledBackupError;

    /// <summary>Identificador único de la bóveda</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Nombre de la bóveda (ej: "Documentos Privados")</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>Descripción opcional</summary>
    public string Description
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    /// <summary>Indica si la bóveda está activa</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    /// <summary>Hash PBKDF2-SHA256 de la contraseña de la bóveda (Base64)</summary>
    public string? PasswordHash
    {
        get => _passwordHash;
        set => SetField(ref _passwordHash, value);
    }

    /// <summary>Salt para PBKDF2 (Base64)</summary>
    public string? PasswordSalt
    {
        get => _passwordSalt;
        set => SetField(ref _passwordSalt, value);
    }

    /// <summary>Minutos para auto-bloqueo (5, 15, 30, 60, personalizado)</summary>
    public int AutoLockMinutes
    {
        get => _autoLockMinutes;
        set => SetField(ref _autoLockMinutes, value);
    }

    /// <summary>
    /// Minutos sin operaciones reales en la unidad virtual antes de desmontar.
    /// Cero desactiva este cierre adicional.
    /// </summary>
    public int InactivityAutoLockMinutes
    {
        get => _inactivityAutoLockMinutes;
        set => SetField(ref _inactivityAutoLockMinutes, Math.Clamp(value, 0, 10_080));
    }

    /// <summary>Ruta donde se almacena el archivo .vault (ej: C:\Users\User\Documents\MiBoveda.vault)</summary>
    public string? VaultFilePath
    {
        get => _vaultFilePath;
        set => SetField(ref _vaultFilePath, value, updateModifiedTime: false);
    }

    /// <summary>Ruta temporal donde se monta la bóveda (ej: AppData\Temp\ProtectedApp\Vault_GUID)</summary>
    [JsonIgnore]
    public string? MountPath
    {
        get => _mountPath;
        set => SetField(ref _mountPath, value, updateModifiedTime: false);
    }

    /// <summary>Indica si la bóveda está actualmente montada</summary>
    [JsonIgnore]
    public bool IsMounted
    {
        get => _isMounted;
        set => SetField(ref _isMounted, value, updateModifiedTime: false);
    }

    /// <summary>Indica que la unidad virtual está montada en modo de solo lectura.</summary>
    [JsonIgnore]
    public bool IsReadOnlyMounted
    {
        get => _isReadOnlyMounted;
        set => SetField(ref _isReadOnlyMounted, value, updateModifiedTime: false);
    }

    /// <summary>Indica que la sesión se está guardando y bloqueando.</summary>
    [JsonIgnore]
    public bool IsClosing
    {
        get => _isClosing;
        set => SetField(ref _isClosing, value, updateModifiedTime: false);
    }

    /// <summary>Fecha de creación UTC</summary>
    public DateTime CreatedUtc
    {
        get => _createdUtc;
        set => SetField(ref _createdUtc, value);
    }

    /// <summary>Fecha de última modificación UTC</summary>
    public DateTime ModifiedUtc
    {
        get => _modifiedUtc;
        set => SetField(ref _modifiedUtc, value);
    }

    /// <summary>Último acceso UTC (para control de auto-bloqueo)</summary>
    [JsonIgnore]
    public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Tamaño del contenedor cifrado en bytes</summary>
    [JsonIgnore]
    public long SizeBytes
    {
        get => _sizeBytes;
        set => SetField(ref _sizeBytes, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public DateTime? SessionExpiresUtc
    {
        get => _sessionExpiresUtc;
        set => SetField(ref _sessionExpiresUtc, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public bool HasRecoveryBackup
    {
        get => _hasRecoveryBackup;
        set => SetField(ref _hasRecoveryBackup, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public bool BackupNeedsAttention
    {
        get => _backupNeedsAttention;
        set => SetField(ref _backupNeedsAttention, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public bool BackupCanRestore
    {
        get => _backupCanRestore;
        set => SetField(ref _backupCanRestore, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public bool HasPendingJournal
    {
        get => _hasPendingJournal;
        set => SetField(ref _hasPendingJournal, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public bool ScheduledBackupConfigured
    {
        get => _scheduledBackupConfigured;
        set => SetField(ref _scheduledBackupConfigured, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public int ScheduledBackupCount
    {
        get => _scheduledBackupCount;
        set => SetField(ref _scheduledBackupCount, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public DateTime? LastScheduledBackupUtc
    {
        get => _lastScheduledBackupUtc;
        set => SetField(ref _lastScheduledBackupUtc, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public string? ScheduledBackupDirectory
    {
        get => _scheduledBackupDirectory;
        set => SetField(ref _scheduledBackupDirectory, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public long ScheduledBackupSizeBytes
    {
        get => _scheduledBackupSizeBytes;
        set => SetField(ref _scheduledBackupSizeBytes, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public VaultScheduledBackupHealth ScheduledBackupHealth
    {
        get => _scheduledBackupHealth;
        set => SetField(ref _scheduledBackupHealth, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public string? ScheduledBackupError
    {
        get => _scheduledBackupError;
        set => SetField(ref _scheduledBackupError, value, updateModifiedTime: false);
    }

    [JsonIgnore]
    public string StatusLabel => LocalizationService.T(IsClosing
        ? "Protegiendo…"
        : IsMounted
            ? IsReadOnlyMounted ? "Consulta segura" : "Cambios pendientes"
        : BackupNeedsAttention
            ? BackupCanRestore ? "Recuperación disponible" : "Copia dañada"
        : HasPendingJournal
            ? "Cambios recuperables"
            : "Cerrada");

    [JsonIgnore]
    public bool CanOpen => !IsClosing;

    [JsonIgnore]
    public bool CanLock => IsMounted && !IsClosing;

    [JsonIgnore]
    public bool CanManageBackup => HasRecoveryBackup && !IsMounted && !IsClosing;

    [JsonIgnore]
    public bool CanManageScheduledBackups => ScheduledBackupConfigured && ScheduledBackupCount > 0 && !IsMounted && !IsClosing;

    [JsonIgnore]
    public bool CanEdit => !IsMounted && !IsClosing;

    [JsonIgnore]
    public bool CanRemove => !IsMounted && !IsClosing;

    [JsonIgnore]
    public string SizeLabel => SizeBytes <= 0 ? LocalizationService.T("Vacía") : SizeBytes < 1024 * 1024
        ? $"{Math.Max(1, SizeBytes / 1024)} KB"
        : $"{SizeBytes / (1024d * 1024d):0.0} MB";

    [JsonIgnore]
    public string ScheduledBackupLabel => ScheduledBackupHealth switch
    {
        VaultScheduledBackupHealth.NotConfigured => LocalizationService.T("Copias programadas sin configurar"),
        VaultScheduledBackupHealth.Pending => LocalizationService.T("⚠ Copia programada pendiente"),
        VaultScheduledBackupHealth.Overdue => LocalizationService.T("⚠ Copia programada vencida"),
        VaultScheduledBackupHealth.Failed => LocalizationService.T("⚠ Error al crear la copia programada"),
        _ => LocalizationService.IsEnglish
            ? $"Backup successful: {LastScheduledBackupUtc?.ToLocalTime():dd/MM HH:mm} · {ScheduledBackupCount} version{(ScheduledBackupCount == 1 ? string.Empty : "s")} · {FormatSize(ScheduledBackupSizeBytes)}"
            : $"Copia correcta: {LastScheduledBackupUtc?.ToLocalTime():dd/MM HH:mm} · {ScheduledBackupCount} versión{(ScheduledBackupCount == 1 ? string.Empty : "es")} · {FormatSize(ScheduledBackupSizeBytes)}",
    };

    [JsonIgnore]
    public string ScheduledBackupDetail => string.IsNullOrWhiteSpace(ScheduledBackupDirectory)
        ? ScheduledBackupLabel
        : $"{ScheduledBackupLabel}{(string.IsNullOrWhiteSpace(ScheduledBackupError) ? string.Empty : $"{Environment.NewLine}{ScheduledBackupError}")}{Environment.NewLine}{ScheduledBackupDirectory}";

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(ScheduledBackupLabel));
        OnPropertyChanged(nameof(ScheduledBackupDetail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, bool updateModifiedTime = true,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        if (updateModifiedTime && propertyName != nameof(ModifiedUtc))
        {
            _modifiedUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(ModifiedUtc));
        }
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsMounted))
        {
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(CanLock));
            OnPropertyChanged(nameof(CanManageBackup));
            OnPropertyChanged(nameof(CanManageScheduledBackups));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanRemove));
        }
        if (propertyName == nameof(IsReadOnlyMounted)) OnPropertyChanged(nameof(StatusLabel));
        if (propertyName == nameof(IsClosing))
        {
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(CanLock));
            OnPropertyChanged(nameof(CanManageBackup));
            OnPropertyChanged(nameof(CanManageScheduledBackups));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanRemove));
        }
        if (propertyName == nameof(HasRecoveryBackup)) OnPropertyChanged(nameof(CanManageBackup));
        if (propertyName == nameof(BackupNeedsAttention)) OnPropertyChanged(nameof(StatusLabel));
        if (propertyName == nameof(BackupCanRestore)) OnPropertyChanged(nameof(StatusLabel));
        if (propertyName == nameof(HasPendingJournal)) OnPropertyChanged(nameof(StatusLabel));
        if (propertyName == nameof(SizeBytes)) OnPropertyChanged(nameof(SizeLabel));
        if (propertyName is nameof(ScheduledBackupConfigured) or nameof(ScheduledBackupCount)
            or nameof(LastScheduledBackupUtc) or nameof(ScheduledBackupDirectory)
            or nameof(ScheduledBackupHealth) or nameof(ScheduledBackupError) or nameof(ScheduledBackupSizeBytes))
        {
            OnPropertyChanged(nameof(ScheduledBackupLabel));
            OnPropertyChanged(nameof(ScheduledBackupDetail));
            OnPropertyChanged(nameof(CanManageScheduledBackups));
        }
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatSize(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, bytes / 1024)} KB"
        : $"{bytes / (1024d * 1024d):0.0} MB";

    /// <summary>Retorna un resumen del estado de la bóveda</summary>
    public override string ToString() => $"Vault: {Name} (Id={Id:N}, Mounted={IsMounted}, Size={SizeBytes / 1024}KB)";
}
