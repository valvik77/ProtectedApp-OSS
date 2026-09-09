using System.Security.AccessControl;
using System.Security.Principal;
using ProtectedApp.Shared;

namespace ProtectedApp.Service;

internal static class GuardianConstants
{
    public const string ServiceName = "ProtectedAppGuardian";
    public const string DisplayName = "ProtectedApp Guardian";
    public const string EventSource = "ProtectedAppGuardian";
    public const string HealthTaskName = "ProtectedApp Guardian Health Check";
    public const string ProtectionVersion = GuardianProtocol.ProtectionVersion;

    public static string StateFolder
    {
        get
        {
            var overrideFolder = Environment.GetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProtectedApp")
                : Path.GetFullPath(overrideFolder);
        }
    }
    public static string LeasePath => Path.Combine(StateFolder, "guardian-running.json");
    public static string TamperPath => Path.Combine(StateFolder, "guardian-tamper.json");
    public static string MaintenancePath => Path.Combine(StateFolder, "guardian-maintenance.flag");
    public static string IncidentPath => Path.Combine(StateFolder, "guardian-recovery.incident");
    public static string AgentLeasePath => Path.Combine(StateFolder, "guardian-agent.json");
    public static string ThrottlePath => Path.Combine(PolicyFolder, "authentication-throttle.json");
    public static string PolicyFolder => Path.Combine(StateFolder, "Policy");
    public static string PolicyPath => Path.Combine(PolicyFolder, "guardian-policy.dat");
    public static string FolderAclPath => Path.Combine(PolicyFolder, "guardian-folder-acl.dat");
    public static string FolderAclBackupPath => Path.Combine(PolicyFolder, "guardian-folder-acl.bak");
    public static string BootstrapSecretPath => Path.Combine(PolicyFolder, "bootstrap.secret");
    public static string VersionPath => Path.Combine(StateFolder, "guardian-protection.version");
    public static string WebhookConfigPath => Path.Combine(PolicyFolder, "tamper-webhook.dat");
    public static string WebhookInstallationIdPath => Path.Combine(PolicyFolder, "tamper-webhook-installation.dat");
    public static string WebhookQueuePath => Path.Combine(StateFolder, "tamper-webhook-queue.json");
    public static string TamperAuditPath => Path.Combine(StateFolder, "guardian-tamper-audit.json");
    public static string TamperAuditKeyPath => Path.Combine(PolicyFolder, "guardian-tamper-audit.key");
    public static string SafeRecoveryPath => Path.Combine(StateFolder, "guardian-safe-recovery.json");
    public static string SignerIdentityPath => Path.Combine(PolicyFolder, "guardian-signer.thumbprint");
    public static string AgentIdentityPath => Path.Combine(PolicyFolder, "guardian-agent-identity.json");
    public static string IntegrityPath => Path.Combine(StateFolder, "guardian-integrity.json");
    public static string RecoveryFolder => Path.Combine(StateFolder, "Recovery");
    public static string GatePath => Path.Combine(StateFolder, "ProtectedApp.Gate.exe");

    /// <summary>
    /// Guardian runs as SYSTEM and its state includes encrypted policies, authentication
    /// throttling and recovery descriptors. Do not rely on the inherited ProgramData ACL:
    /// it commonly grants read access to authenticated users.
    /// </summary>
    public static void EnsurePrivateStateDirectories()
    {
        // The override is intentionally used by diagnostic probes and tests. Their caller
        // needs to inspect and clean its own temporary state after the process exits.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR")))
        {
            Directory.CreateDirectory(StateFolder);
            Directory.CreateDirectory(PolicyFolder);
            Directory.CreateDirectory(RecoveryFolder);
            return;
        }

        EnsurePrivateDirectory(StateFolder);
        EnsurePrivateDirectory(PolicyFolder);
        EnsurePrivateDirectory(RecoveryFolder);
    }

    internal static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), CreateDirectorySecurity());
        HardenExistingEntries(path);
    }

    private static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateGateFileSecurity()
    {
        var security = CreateFileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateHardenedFileSecurity(string path) =>
        PathsEqual(path, Path.Combine(StateFolder, "ProtectedApp.Gate.exe"))
            ? CreateGateFileSecurity()
            : CreateFileSecurity();

    private static void HardenExistingEntries(string root)
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
                        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(entry), CreateDirectorySecurity());
                        pending.Push(entry);
                    }
                    else
                    {
                        FileSystemAclExtensions.SetAccessControl(
                            new FileInfo(entry), CreateHardenedFileSecurity(entry));
                    }
                }
                catch { }
            }
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

internal sealed record GuardianOptions(string AppPath, bool DiagnosticMode);
