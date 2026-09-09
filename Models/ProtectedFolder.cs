using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ProtectedApp.Services;

namespace ProtectedApp.Models;

public sealed class ProtectedFolder : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _path = string.Empty;
    private bool _isEnabled = true;
    private string? _passwordHash;
    private string? _passwordSalt;
    private int _unlockMinutes = 5;
    private DateTimeOffset? _unlockedUntilUtc;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Path { get => _path; set => SetField(ref _path, value); }
    public bool IsEnabled { get => _isEnabled; set { if (SetField(ref _isEnabled, value)) { OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(CanLock)); } } }
    public string? PasswordHash { get => _passwordHash; set { if (SetField(ref _passwordHash, value)) OnPropertyChanged(nameof(AccessLabel)); } }
    public string? PasswordSalt { get => _passwordSalt; set => SetField(ref _passwordSalt, value); }
    public int UnlockMinutes { get => _unlockMinutes; set { if (SetField(ref _unlockMinutes, value)) OnPropertyChanged(nameof(AccessLabel)); } }

    // These values let ProtectedApp restore a folder's visual customization
    // exactly as it was before applying the protected-folder icon.
    public bool HasFolderIconBackup { get; set; }
    public bool DesktopIniExistedBeforeProtection { get; set; }
    public string? DesktopIniBackupBase64 { get; set; }
    public int DesktopIniAttributesBeforeProtection { get; set; }
    public int FolderAttributesBeforeProtection { get; set; }

    [JsonIgnore]
    public DateTimeOffset? UnlockedUntilUtc
    {
        get => _unlockedUntilUtc;
        set { if (SetField(ref _unlockedUntilUtc, value)) { OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(CanLock)); } }
    }

    [JsonIgnore]
    public string AccessLabel => $"{LocalizationService.T(string.IsNullOrWhiteSpace(PasswordHash) ? "Contraseña maestra" : "Contraseña propia")} · {FormatMinutes(UnlockMinutes)}";

    [JsonIgnore]
    public string StatusLabel => !IsEnabled
        ? LocalizationService.T("Sin protección")
        : UnlockedUntilUtc is { } until && until > DateTimeOffset.UtcNow
            ? LocalizationService.IsEnglish
                ? $"Open until {until.ToLocalTime():HH:mm}"
                : $"Abierta hasta {until.ToLocalTime():HH:mm}"
            : LocalizationService.T("Bloqueada");

    [JsonIgnore]
    public bool CanLock => IsEnabled && UnlockedUntilUtc is { } until && until > DateTimeOffset.UtcNow;

    private static string FormatMinutes(int minutes) => minutes == 60
        ? "1 h"
        : minutes % 60 == 0 && minutes >= 120 ? $"{minutes / 60} h" : $"{minutes} min";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CanLock));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
