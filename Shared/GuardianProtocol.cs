namespace ProtectedApp.Shared;

public static class GuardianProtocol
{
    public const string PipeName = "ProtectedApp.Guardian.v1";
    // Shared by the UI and the SYSTEM service so a service upgrade cannot
    // accidentally disable pending-request polling in the interactive agent.
    public const string ProtectionVersion = "32";
    public const int MaxMessageCharacters = 256 * 1024;

    public const string Status = "status";
    public const string AgentHeartbeat = "agent-heartbeat";
    public const string Diagnostics = "diagnostics";
    public const string BootstrapPolicy = "bootstrap-policy";
    public const string AuthenticateMaster = "authenticate-master";
    public const string RecoverPolicy = "recover-policy";
    public const string SyncPolicy = "sync-policy";
    public const string LockAll = "lock-all";
    public const string EmergencyLock = "emergency-lock";
    public const string LockRule = "lock-rule";
    public const string EndApplicationSession = "end-application-session";
    public const string UnlockFolder = "unlock-folder";
    public const string LockFolder = "lock-folder";
    public const string RestoreOrphanedFolderLock = "restore-orphaned-folder-lock";
    public const string PrepareUninstall = "prepare-uninstall";
    public const string ClaimPending = "claim-pending";
    public const string DismissPending = "dismiss-pending";
    public const string AuthorizeAndLaunch = "authorize-and-launch";
    public const string ExtendTimedSession = "extend-timed-session";
    public const string ExtendInactiveSession = "extend-inactive-session";
    public const string ReportApplicationActivity = "report-application-activity";
    public const string RegisterBlockedAttempt = "register-blocked-attempt";
    public const string RegisterHostAttempt = "register-host-attempt";
    public const string GetTamperWebhook = "get-tamper-webhook";
    public const string ConfigureTamperWebhook = "configure-tamper-webhook";
    public const string PendingAuthentication = "authentication";
    public const string PendingBlocked = "blocked";
    public const string PendingError = "error";
    public const string PendingNotice = "notice";
    public const string PendingGracefulClose = "graceful-close";
}

public sealed class GuardianRequest
{
    public string Type { get; set; } = string.Empty;
    public string UserSid { get; set; } = string.Empty;
    public int SessionId { get; set; }
    public string? Token { get; set; }
    public string? TimedSessionToken { get; set; }
    public string? Password { get; set; }
    public Guid? RuleId { get; set; }
    public string? TargetPath { get; set; }
    public string? HostPath { get; set; }
    public string? HostArguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public GuardianPolicy? Policy { get; set; }
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    public bool WebhookUseHmac { get; set; }
    public string? WebhookSecret { get; set; }
}

public sealed class GuardianResponse
{
    public bool Success { get; set; }
    public bool PasswordAccepted { get; set; }
    public bool PasswordRejected { get; set; }
    public bool RateLimited { get; set; }
    public bool LockoutStarted { get; set; }
    public bool LockoutEnded { get; set; }
    public int RetryAfterSeconds { get; set; }
    public int FailureCount { get; set; }
    public bool PolicyConfigured { get; set; }
    public bool? GateHealthy { get; set; }
    public bool? HealthTaskHealthy { get; set; }
    public string? HealthTaskDetail { get; set; }
    public bool? IntegrityHealthy { get; set; }
    public string? IntegrityDetail { get; set; }
    public bool? AuditTrailHealthy { get; set; }
    public bool SafeRecoveryActive { get; set; }
    public bool SignatureIdentityConfigured { get; set; }
    public string? InstalledProtectionVersion { get; set; }
    public string? Error { get; set; }
    public string? Token { get; set; }
    public string? TimedSessionToken { get; set; }
    public GuardianPendingRequest? Pending { get; set; }
    public int? ProcessId { get; set; }
    public int AffectedProcessCount { get; set; }
    public int GracefulCloseCount { get; set; }
    public int ForcedTerminationCount { get; set; }
    public int AffectedFolderCount { get; set; }
    public DateTimeOffset? UnlockedUntilUtc { get; set; }
    public GuardianPolicy? Policy { get; set; }
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    public bool WebhookUseHmac { get; set; }
    public string? WebhookInstallationId { get; set; }
}

public sealed class GuardianPolicyDatabase
{
    public int FormatVersion { get; set; } = 1;
    public List<GuardianPolicy> Policies { get; set; } = [];
}

public sealed class GuardianPolicy
{
    public string UserSid { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string? MasterPasswordHash { get; set; }
    public string? MasterPasswordSalt { get; set; }
    public int ScanIntervalMilliseconds { get; set; } = 350;
    public bool CloseWarningNotificationsEnabled { get; set; } = true;
    public List<GuardianRule> Rules { get; set; } = [];
    public List<GuardianFolderRule> FolderRules { get; set; } = [];
}

public sealed class GuardianFolderRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public int UnlockMinutes { get; set; } = 5;
}

public sealed class GuardianRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public bool IsEnabled { get; set; }
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public int UnlockGraceMinutes { get; set; }
    public int ForceCloseAfterMinutes { get; set; }
    public int ForceCloseAfterInactivityMinutes { get; set; }
    public bool ScheduleEnabled { get; set; }
    public int ScheduleDays { get; set; } = 127;
    public int ScheduleStartMinutes { get; set; } = 9 * 60;
    public int ScheduleEndMinutes { get; set; } = 17 * 60;
    public bool BlockOutsideSchedule { get; set; }
}

public sealed class GuardianPendingRequest
{
    public Guid RuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset DetectedUtc { get; set; }
    public string Kind { get; set; } = GuardianProtocol.PendingAuthentication;
    public string? Message { get; set; }
    public List<int> ProcessIds { get; set; } = [];
}
