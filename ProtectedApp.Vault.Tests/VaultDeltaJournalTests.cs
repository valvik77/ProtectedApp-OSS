using DokanNet;
using ProtectedApp.Models;
using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class VaultDeltaJournalTests
{
    [Fact]
    public async Task OpenVaultReads_CopyDirectlyIntoExplorerAndOverlayBuffers()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp-DirectRead-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        var vaultPath = Path.Combine(root, "test.pavault");
        Directory.CreateDirectory(sourceRoot);
        var expected = new byte[256 * 1024];
        for (var index = 0; index < expected.Length; index++) expected[index] = (byte)(index % 251);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "large.bin"), expected);
        var uncachedExpected = "uncached during consolidation"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "uncached.bin"), uncachedExpected);
        var vault = new VaultContainer { Id = Guid.NewGuid(), Name = "DirectRead", AutoLockMinutes = 5 };
        const string password = "ProtectedApp-Direct-Read";
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, vaultPath, password, sourceRoot,
                createRecoveryBackup: false);
            using var opened = await VaultFormatV3.OpenAsync(vaultPath, password);
            var direct = new byte[128 * 1024];
            Assert.Equal(direct.Length, await VaultFormatV3.ReadFileRangeIntoAsync(opened, "large.bin",
                32 * 1024, direct));
            Assert.Equal(expected.AsSpan(32 * 1024, direct.Length).ToArray(), direct);

            // Repeat the request after the first read has populated the
            // authenticated cache.  The direct destination path must return
            // identical content without exposing the cached plaintext array.
            var cachedDirect = new byte[direct.Length];
            Assert.Equal(cachedDirect.Length, await VaultFormatV3.ReadFileRangeIntoAsync(opened, "large.bin",
                32 * 1024, cachedDirect));
            Assert.Equal(direct, cachedDirect);

            var readOnly = new VaultReadOnlyFileSystem(opened);
            Assert.Equal(NtStatus.Success, readOnly.FindFiles("\\", out var firstListing, null!));
            Assert.Equal(NtStatus.Success, readOnly.FindFiles("\\", out var secondListing, null!));
            Assert.Same(firstListing, secondListing);
            Assert.Equal(2, firstListing.Count);
            Assert.Contains(firstListing, item => item.FileName == "large.bin");
            Assert.Contains(firstListing, item => item.FileName == "uncached.bin");

            using var overlay = new VaultReadWriteFileSystem(opened);
            Assert.Equal(NtStatus.Success, overlay.FindFiles("\\", out var firstEditableListing, null!));
            Assert.Equal(NtStatus.Success, overlay.FindFiles("\\", out var secondEditableListing, null!));
            Assert.Same(firstEditableListing, secondEditableListing);
            var source = overlay.CreateSnapshot().Single(item => item.Path == "large.bin");
            await using var stream = await source.OpenReadAsync();
            stream.Position = 32 * 1024;
            var overlayRead = new byte[direct.Length];
            Assert.Equal(overlayRead.Length, await stream.ReadAsync(overlayRead));
            Assert.Equal(direct, overlayRead);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var positionBeforeCancellation = stream.Position;
#pragma warning disable CA2022 // The token is already cancelled; no partial read can occur.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await stream.ReadAsync(new byte[1], cancelled.Token));
#pragma warning restore CA2022
            Assert.Equal(positionBeforeCancellation, stream.Position);

            var replacement = "edited"u8.ToArray();
            Assert.Equal(NtStatus.Success, overlay.WriteFile("large.bin", replacement, out var bytesWritten,
                64 * 1024, null!));
            Assert.Equal(replacement.Length, bytesWritten);
            Assert.Equal(NtStatus.Success, overlay.FindFiles("\\", out var updatedEditableListing, null!));
            Assert.NotSame(firstEditableListing, updatedEditableListing);
            var expectedEdited = direct.ToArray();
            replacement.CopyTo(expectedEdited, 64 * 1024 - 32 * 1024);
            var editedSource = overlay.CreateSnapshot().Single(item => item.Path == "large.bin");
            await using var editedStream = await editedSource.OpenReadAsync();
            editedStream.Position = 32 * 1024;
            var editedRead = new byte[direct.Length];
            Assert.Equal(editedRead.Length, await editedStream.ReadAsync(editedRead));
            Assert.Equal(expectedEdited, editedRead);

            var commitSource = overlay.CreateCommitSnapshot().Single(item => item.Path == "large.bin");
            await using var commitStream = await commitSource.OpenReadAsync();
            commitStream.Position = 32 * 1024;
            var committedRead = new byte[direct.Length];
            Assert.Equal(committedRead.Length, await commitStream.ReadAsync(committedRead));
            Assert.Equal(expectedEdited, committedRead);

            var uncachedSource = overlay.CreateSnapshot().Single(item => item.Path == "uncached.bin");
            await using var uncachedStream = await uncachedSource.OpenReadAsync();
            var uncachedRead = new byte[uncachedExpected.Length];
            Assert.Equal(uncachedRead.Length, await uncachedStream.ReadAsync(uncachedRead));
            Assert.Equal(uncachedExpected, uncachedRead);
            Assert.True(opened.TryGetEntryIndex("uncached.bin", out var uncachedEntryIndex));
            Assert.False(opened.TryGetCachedChunk(uncachedEntryIndex, 0, out _));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

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

            using (var invalid = new VaultJournalSnapshot([
                       new VaultJournalNode("invalid.txt", false, 1, DateTime.UtcNow, DateTime.UtcNow,
                           false, null, 0, new Dictionary<long, byte[]> { [long.MaxValue] = new byte[64 * 1024] })
                   ]))
            using (var validationOverlay = new VaultReadWriteFileSystem(opened))
                Assert.Throws<InvalidDataException>(() => validationOverlay.ApplyJournalSnapshot(invalid));

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
