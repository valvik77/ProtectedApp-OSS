using ProtectedApp.Service;
using ProtectedApp.Shared;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ProtectedApp.Guardian.Tests;

public sealed class GuardianPolicyStoreTests
{
    [Fact]
    public void SetPolicy_PersistsAnIndependentValidatedCopy()
    {
        using var state = new TemporaryGuardianState();
        var store = new GuardianPolicyStore();
        var ruleId = Guid.NewGuid();
        var policy = new GuardianPolicy
        {
            UserSid = "S-1-5-21-101-202-303-1001",
            Revision = 7,
            MasterPasswordHash = "hash",
            MasterPasswordSalt = "salt",
            ScanIntervalMilliseconds = 100,
            Rules =
            [
                new GuardianRule
                {
                    Id = ruleId,
                    Name = "Example",
                    Path = Path.Combine(state.DirectoryPath, "example.exe"),
                    IsEnabled = true,
                    ForceCloseAfterMinutes = 15,
                    ForceCloseAfterInactivityMinutes = 10
                }
            ]
        };

        store.SetPolicy(policy);
        policy.Rules[0].Name = "Modified after save";

        var reloaded = new GuardianPolicyStore().GetPolicy(policy.UserSid);

        Assert.NotNull(reloaded);
        Assert.Equal(7, reloaded.Revision);
        Assert.Equal(500, reloaded.ScanIntervalMilliseconds);
        Assert.Equal("Example", reloaded.Rules.Single().Name);
        Assert.Equal(15, reloaded.Rules.Single().ForceCloseAfterMinutes);
        Assert.Equal(0, reloaded.Rules.Single().ForceCloseAfterInactivityMinutes);
        Assert.True(File.Exists(GuardianConstants.PolicyPath));
    }

    [Fact]
    public void SetPolicy_RejectsGuardianComponentsAsProtectedTargets()
    {
        using var state = new TemporaryGuardianState();
        var store = new GuardianPolicyStore();
        var policy = new GuardianPolicy
        {
            UserSid = "S-1-5-21-101-202-303-1002",
            MasterPasswordHash = "hash",
            MasterPasswordSalt = "salt",
            Rules =
            [
                new GuardianRule
                {
                    Id = Guid.NewGuid(),
                    Name = "Guardian",
                    Path = Path.Combine(state.DirectoryPath, "ProtectedApp.Guardian.exe"),
                    IsEnabled = true
                }
            ]
        };

        Assert.Throws<InvalidDataException>(() => store.SetPolicy(policy));
    }

    [Fact]
    public void SetPolicy_PrefersTimedCloseWhenBothAutomaticCloseModesAreConfigured()
    {
        using var state = new TemporaryGuardianState();
        var store = new GuardianPolicyStore();
        var policy = new GuardianPolicy
        {
            UserSid = "S-1-5-21-101-202-303-1003",
            MasterPasswordHash = "hash",
            MasterPasswordSalt = "salt",
            Rules =
            [
                new GuardianRule
                {
                    Id = Guid.NewGuid(),
                    Name = "Timed rule",
                    Path = Path.Combine(state.DirectoryPath, "timed.exe"),
                    IsEnabled = true,
                    UnlockGraceMinutes = 30,
                    ForceCloseAfterMinutes = 10,
                    ForceCloseAfterInactivityMinutes = 5
                }
            ]
        };

        store.SetPolicy(policy);
        var rule = store.GetPolicy(policy.UserSid)!.Rules.Single();

        Assert.Equal(10, rule.ForceCloseAfterMinutes);
        Assert.Equal(0, rule.ForceCloseAfterInactivityMinutes);
        Assert.Equal(10, rule.UnlockGraceMinutes);
    }

    [Fact]
    public void SetPolicy_RemovesLegacyFolderRules()
    {
        using var state = new TemporaryGuardianState();
        var store = new GuardianPolicyStore();
        var policy = new GuardianPolicy
        {
            UserSid = "S-1-5-21-101-202-303-1004",
            MasterPasswordHash = "hash",
            MasterPasswordSalt = "salt",
            FolderRules =
            [
                new GuardianFolderRule
                {
                    Id = Guid.NewGuid(), Name = "Legacy folder",
                    Path = Path.Combine(state.DirectoryPath, "folder"), IsEnabled = true
                }
            ]
        };

        store.SetPolicy(policy);

        Assert.Empty(store.GetPolicy(policy.UserSid)!.FolderRules);
    }

    [Fact]
    public void ProtectionSchedule_HandlesOvernightWindowsAndOutsidePolicy()
    {
        const int from22To06 = 22 * 60;
        const int until06 = 6 * 60;
        var tuesdayAt01 = new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);
        var tuesdayAt12 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ProtectionSchedule.IsWithinSchedule((int)ScheduleDays.Monday, from22To06, until06, tuesdayAt01));
        Assert.False(ProtectionSchedule.IsWithinSchedule((int)ScheduleDays.Monday, from22To06, until06, tuesdayAt12));
        Assert.Equal(ScheduleDisposition.Block,
            ProtectionSchedule.GetDisposition(true, (int)ScheduleDays.Monday, from22To06, until06, true, tuesdayAt12));
        Assert.Equal(ScheduleDisposition.Allow,
            ProtectionSchedule.GetDisposition(true, (int)ScheduleDays.Monday, from22To06, until06, false, tuesdayAt12));
    }

    [Fact]
    public void AgentHeartbeat_RequiresARecentPulseForTheCurrentSession()
    {
        using var state = new TemporaryGuardianState();
        var currentSession = unchecked((uint)Process.GetCurrentProcess().SessionId);

        Assert.False(TamperState.IsAgentHeartbeatFresh(currentSession));
        TamperState.MarkAgentHeartbeat(currentSession);

        Assert.True(TamperState.IsAgentHeartbeatFresh(currentSession));
        Assert.False(TamperState.IsAgentHeartbeatFresh(currentSession + 1));

        TamperState.ClearAgentObservation();
        Assert.False(TamperState.IsAgentHeartbeatFresh(currentSession));
    }

    [Fact]
    public void FolderLock_PreservesOnlyMetadataAccessNeededForFolderIcons()
    {
        using var state = new TemporaryGuardianState();
        // Keep the protected folder outside Guardian's test state folder: the
        // service correctly refuses to protect its own recovery data.
        var folderPath = Path.Combine(Path.GetTempPath(), "ProtectedApp.FolderAcl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folderPath);
        File.WriteAllText(Path.Combine(folderPath, "desktop.ini"), "[.ShellClassInfo]\r\nIconResource=test.ico,0\r\n");
        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var service = new FolderProtectionService(
            new GuardianPolicyStore(),
            new GuardianOptions(Environment.ProcessPath ?? "ProtectedApp.exe", DiagnosticMode: true),
            NullLogger<FolderProtectionService>.Instance);
        var policy = new GuardianPolicy
        {
            UserSid = userSid,
            FolderRules =
            [
                new GuardianFolderRule { Id = Guid.NewGuid(), Name = "Folder", Path = folderPath, IsEnabled = true }
            ]
        };

        try
        {
            service.Synchronize([policy]);

            var directoryRules = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(folderPath), AccessControlSections.Access)
                .GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>();
            var lockRule = Assert.Single(directoryRules, rule =>
                rule.IdentityReference.Value == userSid && rule.AccessControlType == AccessControlType.Deny);
            Assert.True(lockRule.FileSystemRights.HasFlag(FileSystemRights.ReadData));
            Assert.False(lockRule.FileSystemRights.HasFlag(FileSystemRights.ReadAttributes));

            var desktopIniRules = FileSystemAclExtensions.GetAccessControl(new FileInfo(Path.Combine(folderPath, "desktop.ini")), AccessControlSections.Access)
                .GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>();
            Assert.Contains(desktopIniRules, rule => !rule.IsInherited
                && rule.IdentityReference.Value == userSid
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.ReadData));

            Assert.Contains("IconResource", File.ReadAllText(Path.Combine(folderPath, "desktop.ini")));
            Assert.Throws<UnauthorizedAccessException>(() => Directory.EnumerateFileSystemEntries(folderPath).ToArray());
        }
        finally
        {
            service.RestoreAll();
            try { Directory.Delete(folderPath, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void PrivateStateDirectory_GrantsFullControlOnlyToSystemAndAdministrators()
    {
        var path = Path.Combine(Path.GetTempPath(), "ProtectedApp.Guardian.Acl.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            GuardianConstants.EnsurePrivateDirectory(path);

            var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Access);
            var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .ToArray();
            var expectedSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
            };

            Assert.True(security.AreAccessRulesProtected);
            Assert.All(expectedSids, sid => Assert.Contains(rules, rule =>
                rule.IdentityReference.Value == sid
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)
                && rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)));
            Assert.DoesNotContain(rules, rule => rule.AccessControlType == AccessControlType.Allow
                && !expectedSids.Contains(rule.IdentityReference.Value, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void GateExecutableAcl_AllowsAuthenticatedUsersToExecuteButNotModify()
    {
        var security = GuardianConstants.CreateHardenedFileSecurity(
            Path.Combine(GuardianConstants.StateFolder, "ProtectedApp.Gate.exe"));
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value;

        var gateRule = Assert.Single(rules, rule =>
            rule.IdentityReference.Value == authenticatedUsers
            && rule.AccessControlType == AccessControlType.Allow);

        Assert.True(security.AreAccessRulesProtected);
        Assert.True(gateRule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));
        Assert.False(gateRule.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.False(gateRule.FileSystemRights.HasFlag(FileSystemRights.Modify));
        Assert.False(gateRule.FileSystemRights.HasFlag(FileSystemRights.FullControl));

        var privateFileRules = GuardianConstants.CreateHardenedFileSecurity(
                Path.Combine(GuardianConstants.PolicyFolder, "guardian-policy.dat"))
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>();
        Assert.DoesNotContain(privateFileRules, rule =>
            rule.IdentityReference.Value == authenticatedUsers);
    }

    [Fact]
    public void DirectoryOpusCompatibility_RemovesOnlyPageHeapSettings()
    {
        var path = $@"Software\ProtectedApp.Tests\{Guid.NewGuid():N}";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)!;
            key.SetValue("GlobalFlag", 0x02000004, RegistryValueKind.DWord);
            key.SetValue("PageHeapFlags", 3, RegistryValueKind.DWord);
            key.SetValue("UnrelatedValue", "preserve");

            Assert.True(DirectoryOpusCompatibility.RemovePageHeapValues(true, [key]));
            Assert.Equal(4, key.GetValue("GlobalFlag"));
            Assert.Null(key.GetValue("PageHeapFlags"));
            Assert.Equal("preserve", key.GetValue("UnrelatedValue"));
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch { }
        }
    }

    [Fact]
    public void Load_DoesNotQuarantinePolicyWhenFileIsTemporarilyLocked()
    {
        using var state = new TemporaryGuardianState();
        var store = new GuardianPolicyStore();
        store.SetPolicy(new GuardianPolicy
        {
            UserSid = "S-1-5-21-101-202-303-2001", MasterPasswordHash = "hash", MasterPasswordSalt = "salt"
        });
        using var locked = new FileStream(GuardianConstants.PolicyPath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<IOException>(() => _ = new GuardianPolicyStore());
        Assert.True(File.Exists(GuardianConstants.PolicyPath));
        Assert.Empty(Directory.EnumerateFiles(GuardianConstants.PolicyFolder, "*.corrupt-*"));
    }

    [Fact]
    public void IpcTokenValidation_RejectsExpiredOrForeignTokens()
    {
        var now = DateTimeOffset.UtcNow;
        var valid = new GuardianIpcServer.AuthToken("S-1-5-21-1", 42, now.AddSeconds(1));
        var expired = new GuardianIpcServer.AuthToken("S-1-5-21-1", 42, now.AddSeconds(-1));

        Assert.True(GuardianIpcServer.IsTokenValid(valid, "S-1-5-21-1", 42, now));
        Assert.False(GuardianIpcServer.IsTokenValid(expired, "S-1-5-21-1", 42, now));
        Assert.False(GuardianIpcServer.IsTokenValid(valid, "S-1-5-21-2", 42, now));
        Assert.False(GuardianIpcServer.IsTokenValid(valid, "S-1-5-21-1", 43, now));
        Assert.Equal(TimeSpan.FromSeconds(10), GuardianIpcServer.ClientRequestTimeout);
    }

    [Fact]
    public void ThrottleLoad_DoesNotResetLockoutWhenFileIsTemporarilyLocked()
    {
        using var state = new TemporaryGuardianState();
        var clock = DateTimeOffset.UtcNow;
        var throttle = new AuthenticationThrottle(() => clock);
        throttle.RegisterFailure("S-1-5-21-locked");
        throttle.RegisterFailure("S-1-5-21-locked");
        throttle.RegisterFailure("S-1-5-21-locked");
        using var locked = new FileStream(GuardianConstants.ThrottlePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<IOException>(() => _ = new AuthenticationThrottle(() => clock));
        Assert.True(File.Exists(GuardianConstants.ThrottlePath));
    }

    [Fact]
    public void MaintenanceCheck_DoesNotSuppressTamperWhenFlagCannotBeCleared()
    {
        using var state = new TemporaryGuardianState();
        Directory.CreateDirectory(GuardianConstants.StateFolder);
        File.WriteAllText(GuardianConstants.MaintenancePath, "stale");
        File.SetLastWriteTimeUtc(GuardianConstants.MaintenancePath, DateTime.UtcNow.AddMinutes(-11));
        using var locked = new FileStream(GuardianConstants.MaintenancePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(TamperState.IsMaintenanceActive());
    }

    [Fact]
    public void TamperWebhook_ConfigIsEncryptedAndQueueIsBounded()
    {
        using var state = new TemporaryGuardianState();
        TamperWebhookNotifier.Configure(true, "https://alerts.example.test/protectedapp", true, "test-secret");

        var status = TamperWebhookNotifier.GetStatus();
        Assert.True(status.Enabled);
        Assert.True(status.UseHmac);
        Assert.Equal("https://alerts.example.test/protectedapp", status.Url);
        Assert.True(Guid.TryParse(status.InstallationId, out _));
        Assert.DoesNotContain("test-secret", File.ReadAllText(GuardianConstants.WebhookConfigPath));
        Assert.DoesNotContain(status.InstallationId, File.ReadAllText(GuardianConstants.WebhookInstallationIdPath));
        for (var index = 0; index < 25; index++) TamperWebhookNotifier.Enqueue(TamperEventCode.TamperDetected);
        using var queue = System.Text.Json.JsonDocument.Parse(File.ReadAllText(GuardianConstants.WebhookQueuePath));
        Assert.Equal(20, queue.RootElement.GetArrayLength());
        var payload = TamperWebhookNotifier.CreatePayload("event-1", status.InstallationId,
            DateTimeOffset.Parse("2026-09-02T12:34:56Z"), TamperEventCode.TamperDetected.ToString());
        using var payloadJson = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal(status.InstallationId, payloadJson.RootElement.GetProperty("installationId").GetString());
        Assert.Equal("TamperDetected", payloadJson.RootElement.GetProperty("event").GetString());
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("test-secret"));
        var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("payload"))).ToLowerInvariant();
        Assert.Equal(expected, TamperWebhookNotifier.ComputeSignature("payload", "test-secret"));
        Assert.Throws<InvalidDataException>(() => TamperWebhookNotifier.Configure(true, "http://alerts.example.test", false, null));
        Assert.Throws<InvalidDataException>(() => TamperWebhookNotifier.Configure(true, "https://127.0.0.1/", false, null));
        Assert.Throws<InvalidDataException>(() => TamperWebhookNotifier.Configure(true, "https://[::1]/", false, null));
        Assert.Throws<InvalidDataException>(() => TamperWebhookNotifier.Configure(true, "https://192.168.1.1/", false, null));

        TamperWebhookNotifier.Configure(false, null, false, null);
        Assert.False(TamperWebhookNotifier.GetStatus().Enabled);
        Assert.Equal(status.InstallationId, TamperWebhookNotifier.GetStatus().InstallationId);
        Assert.False(File.Exists(GuardianConstants.WebhookQueuePath));
        Assert.Throws<InvalidDataException>(() => TamperWebhookNotifier.Configure(true, "https://alerts.example.test", true, null));
    }

    private sealed class TemporaryGuardianState : IDisposable
    {
        private readonly string? _previousDirectory = Environment.GetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR");

        public TemporaryGuardianState()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "ProtectedApp.Guardian.Tests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR", DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR", _previousDirectory);
            try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
            catch { }
        }
    }
}
