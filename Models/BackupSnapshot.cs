namespace ProtectedApp.Models;

public sealed class BackupSnapshot
{
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool StartWithWindows { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool LockPanelWhenHidden { get; set; } = true;
    public string? ThemePreference { get; set; }
    public string? LanguagePreference { get; set; }
    public int ManagementAutoLockMinutes { get; set; }
    public int PollIntervalMilliseconds { get; set; } = 500;
    public bool OpenVaultsReadOnlyByDefault { get; set; }
    public string? VaultBackupDirectory { get; set; }
    public int VaultBackupIntervalHours { get; set; } = 24;
    public int VaultBackupRetentionCount { get; set; } = 5;
    public bool VaultBackupNotificationsEnabled { get; set; } = true;
    public bool CloseWarningNotificationsEnabled { get; set; } = true;
    public bool SecurityAlertsEnabled { get; set; } = true;
    public string? ImmediateLockHotkey { get; set; }
    public List<ProtectedApplication> Applications { get; set; } = [];
    public List<ProtectedFolder> Folders { get; set; } = [];
    public List<VaultContainer> Vaults { get; set; } = [];
    public List<ActivityEntry> ActivityHistory { get; set; } = [];
}
