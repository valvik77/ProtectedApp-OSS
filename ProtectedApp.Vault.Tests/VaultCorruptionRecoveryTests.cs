using System.Security.Cryptography;
using ProtectedApp.Models;
using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

/// <summary>
/// Regression corpus for malformed on-disk vault data. These tests deliberately use stable
/// mutations rather than a time-based fuzzer: every failure remains reproducible in CI.
/// </summary>
public sealed class VaultCorruptionRecoveryTests
{
    private const string VaultPassword = "Vault corruption test password 123!";

    [Fact]
    public async Task CorruptedAndTruncatedContainers_AreRejected_WithoutChangingTheOriginal()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        var original = await File.ReadAllBytesAsync(fixture.VaultPath);
        var originalHash = SHA256.HashData(original);
        try
        {
            using var service = new VaultService();
            // The envelope (magic, KDF, wrapped key, lengths and encrypted index) must reject
            // any change at authentication time. File chunks are verified on first read below.
            var envelopeOffsets = new[] { 0, 8, 9, 13, 29, 41, 57, 89, 97, original.Length - 1 };
            foreach (var offset in envelopeOffsets.Distinct())
            {
                var mutated = original.ToArray();
                mutated[offset] ^= 0xA5;
                var path = fixture.PathFor($"mutated-{offset}.pavault");
                await File.WriteAllBytesAsync(path, mutated);
                CryptographicOperations.ZeroMemory(mutated);
                Assert.False(await service.VerifyVaultPasswordAsync(path, VaultPassword));
            }

            var payloadMutation = original.ToArray();
            var payloadOffset = payloadMutation.Length / 2;
            payloadMutation[payloadOffset] ^= 0xA5;
            var payloadPath = fixture.PathFor("mutated-payload.pavault");
            await File.WriteAllBytesAsync(payloadPath, payloadMutation);
            CryptographicOperations.ZeroMemory(payloadMutation);
            Assert.True(await service.VerifyVaultPasswordAsync(payloadPath, VaultPassword));
            using (var opened = await VaultFormatV3.OpenAsync(payloadPath, VaultPassword))
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    VaultFormatV3.ReadFileRangeAsync(opened, "payload.bin", 0, 64));

            foreach (var length in DistinctLengths(original.Length))
            {
                var path = fixture.PathFor($"truncated-{length}.pavault");
                await File.WriteAllBytesAsync(path, original.AsMemory(0, length).ToArray());
                Assert.False(await service.VerifyVaultPasswordAsync(path, VaultPassword));
            }

            Assert.False(await service.VerifyVaultPasswordAsync(fixture.VaultPath, VaultPassword + "wrong"));
            Assert.True(await service.VerifyVaultPasswordAsync(fixture.VaultPath, VaultPassword));
            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.VaultPath)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(originalHash);
        }
    }

    [Fact]
    public async Task CorruptedJournals_AreRejected_AndNeverAlterThePrimaryVault()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(fixture.VaultPath));
        var journalPath = fixture.PathFor("recovery.pajournal");
        try
        {
            using var service = new VaultService();
            using var opened = await VaultFormatV3.OpenAsync(fixture.VaultPath, VaultPassword);
            using var snapshot = new VaultJournalSnapshot([
                new VaultJournalNode("note.txt", false, 5, DateTime.UtcNow, DateTime.UtcNow,
                    false, null, 0, new Dictionary<long, byte[]> { [0] = "hello"u8.ToArray() })
            ]);
            await VaultDeltaJournal.WriteAsync(journalPath, opened, snapshot);
            var journal = await File.ReadAllBytesAsync(journalPath);
            try
            {
                foreach (var length in DistinctLengths(journal.Length))
                    await AssertRejectsJournalAsync(service, fixture, opened, journalPath, journal.AsMemory(0, length).ToArray());

                var altered = journal.ToArray();
                altered[altered.Length / 2] ^= 0x5A;
                await AssertRejectsJournalAsync(service, fixture, opened, journalPath, altered);

                var extended = new byte[journal.Length + 1];
                journal.CopyTo(extended, 0);
                extended[^1] = 0xFF;
                await AssertRejectsJournalAsync(service, fixture, opened, journalPath, extended);
            }
            finally { CryptographicOperations.ZeroMemory(journal); }

            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.VaultPath)));
            Assert.True(await service.VerifyVaultPasswordAsync(fixture.VaultPath, VaultPassword));
        }
        finally { CryptographicOperations.ZeroMemory(originalHash); }
    }

    [Fact]
    public async Task EncryptedBackups_RejectTruncationAndDistributedMutations()
    {
        const string password = "Backup corruption test password 123!";
        var backup = await BackupService.CreateAsync(new BackupSnapshot { Version = 1 }, password);
        try
        {
            foreach (var length in DistinctLengths(backup.Length))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(backup[..length], password));

            foreach (var offset in DistributedOffsets(backup.Length, 13))
            {
                var altered = backup.ToArray();
                altered[offset] ^= 0x3C;
                try
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadAsync(altered, password));
                }
                finally { CryptographicOperations.ZeroMemory(altered); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(backup); }
    }

    [Fact]
    public async Task InterruptedVirtualWrite_PreservesThePreviousVault_AndRemovesItsTemporaryFile()
    {
        await using var fixture = await VaultFixture.CreateAsync();
        var original = await File.ReadAllBytesAsync(fixture.VaultPath);
        var originalHash = SHA256.HashData(original);
        try
        {
            using var opened = await VaultFormatV3.OpenAsync(fixture.VaultPath, VaultPassword);
            var source = new VaultFormatV3.VirtualEntrySource("interrupted.bin", false, 128,
                DateTime.UtcNow, DateTime.UtcNow,
                () => Task.FromResult<Stream>(new MemoryStream(new byte[64], writable: false)));

            await Assert.ThrowsAsync<IOException>(() => VaultFormatV3.WriteFromVirtualEntriesAsync(
                opened.Vault, [source], fixture.VaultPath, opened.PasswordKey, opened.Salt, opened.DataKey,
                opened.TpmBinding, createRecoveryBackup: false));

            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.VaultPath)));
            Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, ".vault.pavault.*.v3tmp"));

            using var service = new VaultService();
            Assert.True(await service.VerifyVaultPasswordAsync(fixture.VaultPath, VaultPassword));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(originalHash);
        }
    }

    private static async Task AssertRejectsJournalAsync(VaultService service, VaultFixture fixture, VaultFormatV3.OpenedVault opened,
        string journalPath, byte[] bytes)
    {
        try
        {
            await File.WriteAllBytesAsync(journalPath, bytes);
            var exception = await Record.ExceptionAsync(() => VaultDeltaJournal.ReadAsync(journalPath, opened));
            Assert.True(exception is InvalidDataException or EndOfStreamException,
                $"El diario corrupto no fue rechazado como datos no válidos: {exception?.GetType().Name ?? "sin excepción"}.");
            Assert.True(await service.VerifyVaultPasswordAsync(fixture.VaultPath, VaultPassword));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static IEnumerable<int> DistributedOffsets(int length, int count)
    {
        yield return 0;
        for (var index = 1; index < count - 1; index++) yield return (int)((long)index * (length - 1) / (count - 1));
        if (length > 1) yield return length - 1;
    }

    private static IEnumerable<int> DistinctLengths(int length) =>
        new[] { 0, 1, 8, 32, length / 3, length / 2, length - 1 }
            .Where(value => value >= 0 && value < length)
            .Distinct();

    private sealed class VaultFixture : IAsyncDisposable
    {
        private readonly string _root;
        public string VaultPath { get; }
        public string DirectoryPath => _root;

        private VaultFixture(string root, string vaultPath) => (_root, VaultPath) = (root, vaultPath);

        public static async Task<VaultFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ProtectedApp-Vault-Corruption-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "source");
            var path = Path.Combine(root, "vault.pavault");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "recovery corpus");
            var payload = RandomNumberGenerator.GetBytes(192 * 1024);
            try { await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload); }
            finally { CryptographicOperations.ZeroMemory(payload); }
            await VaultFormatV3.WriteNewAsync(new VaultContainer { Id = Guid.NewGuid(), Name = "Corruption corpus" },
                path, VaultPassword, source, createRecoveryBackup: false);
            return new VaultFixture(root, path);
        }

        public string PathFor(string name) => Path.Combine(_root, name);

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
