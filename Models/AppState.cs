namespace ProtectedApp.Models;

public sealed class AppState
{
    public string? MasterPasswordHash { get; set; }
    public string? MasterPasswordSalt { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool LockPanelWhenHidden { get; set; } = true;
    public bool UseWindowsHello { get; set; }
    /// <summary>Preferencia visual explícita: "dark", "light" o null para seguir Windows.</summary>
    public string? ThemePreference { get; set; }
    /// <summary>Idioma explícito: "es", "en" o null para seguir Windows.</summary>
    public string? LanguagePreference { get; set; }
    public int ManagementAutoLockMinutes { get; set; }
    public int PollIntervalMilliseconds { get; set; } = 350;
    public bool OpenVaultsReadOnlyByDefault { get; set; }
    /// <summary>Letra preferida para el siguiente montaje virtual.</summary>
    public string? PreferredVaultDriveLetter { get; set; }
    /// <summary>Accesos recientes de bóvedas, en orden de uso.</summary>
    public List<Guid> RecentVaultIds { get; set; } = [];
    public string? VaultBackupDirectory { get; set; }
    public int VaultBackupIntervalHours { get; set; } = 24;
    public int VaultBackupRetentionCount { get; set; } = 5;
    public DateTime? VaultBackupLastRunUtc { get; set; }
    public bool VaultBackupNotificationsEnabled { get; set; } = true;
    public bool CloseWarningNotificationsEnabled { get; set; } = true;
    public bool SecurityAlertsEnabled { get; set; } = true;
    /// <summary>Atajo global configurable, por ejemplo Ctrl+Alt+L.</summary>
    public string? ImmediateLockHotkey { get; set; }
    public bool VaultRecoveryWarningPending { get; set; }
    public string? VaultRecoveryWarningMessage { get; set; }
    public List<ProtectedApplication> Applications { get; set; } = [];
    public List<ProtectedFolder> Folders { get; set; } = [];
    public List<VaultContainer> Vaults { get; set; } = [];
    public List<ActivityEntry> ActivityHistory { get; set; } = [];
}
