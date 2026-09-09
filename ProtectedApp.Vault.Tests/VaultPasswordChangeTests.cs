using ProtectedApp.Models;
using ProtectedApp.Services;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ProtectedApp.Vault.Tests;

public sealed class VaultPasswordChangeTests
{
    [Fact]
    public async Task CreateVaultFromFolder_CreatesVerifiedContainerWithoutChangingSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ProtectedApp.Vault.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "source");
        var path = Path.Combine(directory, "private.pavault");
        const string password = "Folder conversion password 123";
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "secret.txt"), "contenido privado");
        // FileSavePicker creates this harmless placeholder before returning the
        // selected path to the application.
        await File.WriteAllBytesAsync(path, []);

        try
        {
            using var service = new VaultService();
            var vault = new VaultContainer
            {
                Name = "Converted folder",
                Description = "Conversion test",
                InactivityAutoLockMinutes = 15
            };

            Assert.True(await service.CreateVaultFromFolderAsync(vault, source, path, password), service.LastError);
            Assert.True(File.Exists(path));
            Assert.True(await service.VerifyVaultPasswordAsync(path, password));
            var loaded = await service.LoadVaultAsync(path, password);
            Assert.NotNull(loaded);
            Assert.Equal(vault.Id, loaded!.Id);
            Assert.Equal(15, loaded.InactivityAutoLockMinutes);
            Assert.True(File.Exists(Path.Combine(source, "secret.txt")));
            Assert.Empty(Directory.EnumerateFiles(directory, ".private.pavault.*.v3tmp"));
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task ChangePassword_KeepsVerifiedRecoveryCopyUntilReplacementCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ProtectedApp.Vault.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "private.pavault");
        const string oldPassword = "Original password 123";
        const string newPassword = "Replacement password 456";

        try
        {
            using var service = new VaultService();
            var vault = new VaultContainer { Name = "Test vault", Description = "Recovery test" };

            var source = Path.Combine(directory, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "sample.txt"), "Data preserved across key rotation");
            Assert.True(await service.CreateVaultFromFolderAsync(vault, source, path, oldPassword));
            Assert.True(await service.ChangeVaultPasswordAsync(path, oldPassword, newPassword));
            Assert.True(await service.VerifyVaultPasswordAsync(path, newPassword));
            Assert.False(await service.VerifyVaultPasswordAsync(path, oldPassword));
            var integrity = await service.VerifyVaultIntegrityAsync(vault, newPassword);
            Assert.True(integrity.PasswordVerified);
            Assert.True(integrity.IsValid);

            var backup = path + ".bak";
            Assert.True(File.Exists(backup));
            Assert.True(await service.VerifyVaultPasswordAsync(backup, oldPassword));
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void PrivateVaultDirectories_AllowOnlyOwnerSystemAndAdministrators()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp.Vault.Acl.Tests", Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("PROTECTEDAPP_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PROTECTEDAPP_DATA_DIR", root);
            using var service = new VaultService();
            var vault = new VaultContainer { Id = Guid.NewGuid() };

            _ = service.GetWorkingDirectory(vault.Id);
            _ = service.HasPendingJournal(vault);

            var expectedSids = new[]
            {
                WindowsIdentity.GetCurrent().User!.Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
            };
            AssertPrivateDirectory(Path.Combine(root, "VaultWork"), expectedSids);
            AssertPrivateDirectory(Path.Combine(root, "VaultJournal"), expectedSids);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROTECTEDAPP_DATA_DIR", previousRoot);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void DeleteVaultPermanently_OptionallyRemovesRecoveryAndScheduledCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp.Vault.Delete.Tests", Guid.NewGuid().ToString("N"));
        var vaultPath = Path.Combine(root, "private.pavault");
        var vault = new VaultContainer { Id = Guid.NewGuid(), VaultFilePath = vaultPath };
        var scheduled = Path.Combine(root, "backups", "ProtectedApp Vaults", vault.Id.ToString("N"), "copy.pavault");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scheduled)!);
            File.WriteAllText(vaultPath, "primary");
            File.WriteAllText(vaultPath + ".bak", "recovery");
            File.WriteAllText(scheduled, "scheduled");

            using var service = new VaultService();
            var result = service.DeleteVaultPermanently(vault, deleteCopies: true,
                scheduledBackupRoot: Path.Combine(root, "backups"));

            Assert.True(result.PrimaryDeleted);
            Assert.Equal(2, result.DeletedCopies);
            Assert.False(File.Exists(vaultPath));
            Assert.False(File.Exists(vaultPath + ".bak"));
            Assert.False(File.Exists(scheduled));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task StateStoreLoad_DoesNotResetStateWhenFileIsTemporarilyLocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp.State.Tests", Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("PROTECTEDAPP_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PROTECTEDAPP_DATA_DIR", root);
            var store = new StateStore();
            await store.SaveAsync(new AppState { MasterPasswordHash = "preserve" });
            var path = Path.Combine(root, "state.dat");
            await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            await Assert.ThrowsAsync<IOException>(() => new StateStore().LoadAsync());
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(root, "*.corrupt-*"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROTECTEDAPP_DATA_DIR", previousRoot);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static void AssertPrivateDirectory(string path, IReadOnlyCollection<string> expectedSids)
    {
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Access);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.All(expectedSids, sid => Assert.Contains(rules, rule =>
            rule.IdentityReference.Value == sid
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)
            && rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)));
        Assert.DoesNotContain(rules, rule => rule.AccessControlType == AccessControlType.Allow
            && !expectedSids.Contains(rule.IdentityReference.Value, StringComparer.OrdinalIgnoreCase));
    }
}
