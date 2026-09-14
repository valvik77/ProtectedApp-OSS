using ProtectedApp.Models;
using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class VaultDeltaJournalTests
{
    [Fact]
    public async Task Journal_RestoresChangedBlocks_AndRejectsTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp-DeltaJournal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "test.pavault");
        var journalPath = Path.Combine(root, "test.pajournal");
        var vault = new VaultContainer { Id = Guid.NewGuid(), Name = "DeltaTest", AutoLockMinutes = 5 };
        const string password = "ProtectedApp-Delta-Test";
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, vaultPath, password, createRecoveryBackup: false);
            using var opened = await VaultFormatV3.OpenAsync(vaultPath, password);
            var block = new byte[64 * 1024];
            "hello"u8.CopyTo(block);
            using var snapshot = new VaultJournalSnapshot([
                new VaultJournalNode("Docs", true, 0, DateTime.UtcNow, DateTime.UtcNow,
                    false, null, 0, null),
                new VaultJournalNode("Docs/note.txt", false, 5, DateTime.UtcNow, DateTime.UtcNow,
                    false, null, 0, new Dictionary<long, byte[]> { [0] = block })
            ]);

            await VaultDeltaJournal.WriteAsync(journalPath, opened, snapshot);
            using var restored = await VaultDeltaJournal.ReadAsync(journalPath, opened);
            using (var overlay = new VaultReadWriteFileSystem(opened))
            {
                overlay.ApplyJournalSnapshot(restored);
                var source = overlay.CreateSnapshot().Single(item => item.Path == "Docs/note.txt");
                await using var stream = await source.OpenReadAsync();
                var bytes = new byte[5];
                Assert.Equal(5, await stream.ReadAsync(bytes));
                Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(bytes));
            }

            var tampered = await File.ReadAllBytesAsync(journalPath);
            tampered[^1] ^= 0x01;
            await File.WriteAllBytesAsync(journalPath, tampered);
            await Assert.ThrowsAsync<InvalidDataException>(() => VaultDeltaJournal.ReadAsync(journalPath, opened));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Journal_RemainsIdempotent_AfterPrimaryWasAlreadyConsolidated()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp-DeltaCommit-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "old.txt"), "original");
        var vaultPath = Path.Combine(root, "test.pavault");
        var vault = new VaultContainer { Id = Guid.NewGuid(), Name = "CommitTest", AutoLockMinutes = 5 };
        const string password = "ProtectedApp-Commit-Test";
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, vaultPath, password, sourceRoot,
                createRecoveryBackup: false);
            using var snapshot = new VaultJournalSnapshot([
                new VaultJournalNode("new.txt", false, 8, DateTime.UtcNow, DateTime.UtcNow,
                    true, "old.txt", 8, null)
            ]);
            using (var original = await VaultFormatV3.OpenAsync(vaultPath, password))
            using (var overlay = new VaultReadWriteFileSystem(original))
            {
                overlay.ApplyJournalSnapshot(snapshot);
                await VaultFormatV3.WriteFromVirtualEntriesAsync(vault, overlay.CreateSnapshot(), vaultPath,
                    original.PasswordKey, original.Salt, original.DataKey, original.TpmBinding,
                    createRecoveryBackup: true);
            }

            using var consolidated = await VaultFormatV3.OpenAsync(vaultPath, password);
            using var replay = new VaultReadWriteFileSystem(consolidated);
            replay.ApplyJournalSnapshot(snapshot);
            var source = replay.CreateSnapshot().Single(item => item.Path == "new.txt");
            await using var stream = await source.OpenReadAsync();
            using var reader = new StreamReader(stream);
            Assert.Equal("original", await reader.ReadToEndAsync());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
