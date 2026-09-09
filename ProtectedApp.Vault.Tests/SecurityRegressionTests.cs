using System.Security.Cryptography;
using ProtectedApp.Models;
using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class SecurityRegressionTests
{
    [Fact]
    public void PasswordRecords_AreVersionedAndLegacyRecordsRemainVerifiable()
    {
        const string password = "A long test password 123!";
        var current = PasswordService.Hash(password);

        Assert.StartsWith("pbkdf2-sha256:600000:", current.Salt);
        Assert.True(PasswordService.Verify(password, current.Hash, current.Salt));
        Assert.False(PasswordService.Verify(password + "x", current.Hash, current.Salt));
        Assert.False(PasswordService.NeedsRehash(current.Salt));

        var legacySalt = RandomNumberGenerator.GetBytes(16);
        var legacyHash = Rfc2898DeriveBytes.Pbkdf2(password, legacySalt, 210_000,
            HashAlgorithmName.SHA256, 32);
        try
        {
            var encodedSalt = Convert.ToBase64String(legacySalt);
            Assert.True(PasswordService.Verify(password, Convert.ToBase64String(legacyHash), encodedSalt));
            Assert.True(PasswordService.NeedsRehash(encodedSalt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacySalt);
            CryptographicOperations.ZeroMemory(legacyHash);
        }
    }

    [Fact]
    public async Task VaultFormat_RejectsRandomAndTamperedContainers()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp.Vault.Fuzz.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string password = "A long vault password 123!";
        try
        {
            using var service = new VaultService();
            for (var index = 0; index < 32; index++)
            {
                var path = Path.Combine(root, $"random-{index}.pavault");
                var bytes = RandomNumberGenerator.GetBytes(index * 431 + 1);
                await File.WriteAllBytesAsync(path, bytes);
                CryptographicOperations.ZeroMemory(bytes);
                Assert.False(await service.VerifyVaultPasswordAsync(path, password));
            }

            var validPath = Path.Combine(root, "valid.pavault");
            var vault = new VaultContainer { Name = "Integrity probe" };
            Assert.True(await service.SaveVaultAsync(vault, validPath, password), service.LastError);
            var container = await File.ReadAllBytesAsync(validPath);
            try
            {
                foreach (var offset in new[] { 0, 17, container.Length / 2, container.Length - 1 })
                {
                    var mutated = container.ToArray();
                    mutated[offset] ^= 0x5A;
                    var tamperedPath = Path.Combine(root, $"tampered-{offset}.pavault");
                    await File.WriteAllBytesAsync(tamperedPath, mutated);
                    CryptographicOperations.ZeroMemory(mutated);
                    Assert.False(await service.VerifyVaultPasswordAsync(tamperedPath, password));
                }
            }
            finally { CryptographicOperations.ZeroMemory(container); }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task EncryptedBackup_RejectsCorruptionAndWrongPassword()
    {
        const string password = "A long backup password 123!";
        var bytes = await BackupService.CreateAsync(new BackupSnapshot { Version = 1 }, password);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(bytes, password + "x"));
            bytes[bytes.Length / 2] ^= 0x5A;
            await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(bytes, password));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
