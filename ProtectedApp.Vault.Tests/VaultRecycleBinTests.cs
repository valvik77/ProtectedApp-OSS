using DokanNet;
using ProtectedApp.Models;
using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

/// <summary>
/// Deleting a file inside an editable vault must stay recoverable until the
/// user empties the recycle area, and the area itself must never be visible
/// to the applications working on the mounted drive.
/// </summary>
public sealed class VaultRecycleBinTests
{
    [Fact]
    public async Task ADeletedFileIsRecoverableAndHiddenFromTheMountedDrive()
    {
        await WithVaultAsync(async overlay =>
        {
            Assert.Equal(NtStatus.Success, overlay.DeleteFile("\\notes.txt", Info()));
            overlay.Cleanup("\\notes.txt", Info(deletePending: true));

            // Gone from the drive the user sees...
            Assert.Equal(NtStatus.Success, overlay.FindFiles("\\", out var listing, null!));
            Assert.DoesNotContain(listing, item => item.FileName == "notes.txt");
            // ...but still inside the vault, and the recycle folder itself is
            // not browsable, so an application cannot tamper with it.
            Assert.Equal(1, overlay.CountRecycledEntries());
            Assert.DoesNotContain(listing,
                item => item.FileName == VaultReadWriteFileSystem.RecycleFolderName);

            // The retained copy must survive a commit, not just live in memory.
            var snapshot = overlay.CreateSnapshot();
            Assert.Contains(snapshot, source =>
                source.Path.StartsWith(VaultReadWriteFileSystem.RecycleFolderName + "/",
                    StringComparison.Ordinal));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EmptyingTheRecycleAreaDiscardsTheRetainedCopies()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup("\\notes.txt", Info(deletePending: true));
            Assert.Equal(1, overlay.CountRecycledEntries());

            Assert.Equal(1, overlay.EmptyRecycleBin());

            Assert.Equal(0, overlay.CountRecycledEntries());
            Assert.DoesNotContain(overlay.CreateSnapshot(), source =>
                source.Path.StartsWith(VaultReadWriteFileSystem.RecycleFolderName,
                    StringComparison.Ordinal));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DeletingTheSameNameTwiceKeepsBothCopies()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup("\\notes.txt", Info(deletePending: true));

            // Recreate the file and delete it again; neither copy may be lost.
            Assert.Equal(NtStatus.Success, overlay.CreateFile("\\notes.txt", DokanNet.FileAccess.WriteData,
                FileShare.None, FileMode.Create, FileOptions.None, FileAttributes.Normal, Info()));
            overlay.Cleanup("\\notes.txt", Info(deletePending: true));

            // Both copies must survive even when the two deletions share a
            // timestamp, which is what happens in a fast loop like this one.
            Assert.Equal(2, overlay.CountRecycledEntries());
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RecyclingCanBeTurnedOffForAMountThatMustDeleteOutright()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.RecycleEnabled = false;

            overlay.Cleanup("\\notes.txt", Info(deletePending: true));

            Assert.Equal(0, overlay.CountRecycledEntries());
            Assert.DoesNotContain(overlay.CreateSnapshot(), source => source.Path == "notes.txt");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AWriteProtectedMountNeverRecyclesAnything()
    {
        await WithVaultAsync(async overlay =>
        {
            Assert.Equal(NtStatus.AccessDenied, overlay.DeleteFile("\\notes.txt", Info()));
            Assert.Equal(0, overlay.CountRecycledEntries());
            await Task.CompletedTask;
        }, writeProtected: true);
    }

    [Fact]
    public async Task AListedEntryRemembersWhereItWasDeletedFrom()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup("\\sub\\deep.txt", Info(deletePending: true));

            var entry = Assert.Single(overlay.ListRecycledEntries());
            // The original path must survive the commit and the next unlock, so
            // it is encoded in the recycled name rather than held in memory.
            Assert.Equal("sub/deep.txt", entry.OriginalPath);
            Assert.Equal("deep.txt", entry.Name);
            Assert.Equal("sub", entry.Folder);
            Assert.True(entry.DeletedUtc > DateTime.MinValue);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RestoringPutsTheFileBackWhereItCameFrom()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup("\\sub\\deep.txt", Info(deletePending: true));
            var entry = Assert.Single(overlay.ListRecycledEntries());

            Assert.Equal("sub/deep.txt", overlay.RestoreRecycledEntry(entry.Id));

            Assert.Empty(overlay.ListRecycledEntries());
            Assert.Contains(overlay.CreateSnapshot(), source => source.Path == "sub/deep.txt");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RestoringRefusesToOverwriteAFileRecreatedSince()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup("\\notes.txt", Info(deletePending: true));
            var entry = Assert.Single(overlay.ListRecycledEntries());
            Assert.Equal(NtStatus.Success, overlay.CreateFile("\\notes.txt", DokanNet.FileAccess.WriteData,
                FileShare.None, FileMode.Create, FileOptions.None, FileAttributes.Normal, Info()));

            // The recreated file is the one the user is working on now.
            Assert.Null(overlay.RestoreRecycledEntry(entry.Id));
            Assert.Single(overlay.ListRecycledEntries());
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DiscardingOneEntryLeavesTheOthers()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup("\\notes.txt", Info(deletePending: true));
            overlay.Cleanup("\\sub\\deep.txt", Info(deletePending: true));
            var first = overlay.ListRecycledEntries()[0];

            Assert.True(overlay.DiscardRecycledEntry(first.Id));

            var remaining = Assert.Single(overlay.ListRecycledEntries());
            Assert.NotEqual(first.Id, remaining.Id);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AWriteProtectedMountCannotRestoreOrDiscard()
    {
        await WithVaultAsync(async overlay =>
        {
            Assert.Null(overlay.RestoreRecycledEntry("anything"));
            Assert.False(overlay.DiscardRecycledEntry("anything"));
            await Task.CompletedTask;
        }, writeProtected: true);
    }

    [Fact]
    public async Task ADeletedFileSurvivesSavingClosingAndReopeningTheVault()
    {
        // The full cycle the user actually performs: delete, save, close the
        // vault, unlock it again and restore. Every other test works inside a
        // single mount, so only this one proves the retained copy and its
        // original path survive a real commit and a fresh unlock.
        await WithReopenableVaultAsync(async (open, commit) =>
        {
            using (var overlay = await open())
            {
                overlay.Cleanup("\\sub\\deep.txt", Info(deletePending: true));
                Assert.Equal(1, overlay.CountRecycledEntries());
                await commit(overlay);
            }

            using (var reopened = await open())
            {
                // The entry must come back with its provenance intact, because
                // restoring relies on the path encoded in the recycled name.
                var entry = Assert.Single(reopened.ListRecycledEntries());
                Assert.Equal("sub/deep.txt", entry.OriginalPath);
                Assert.Equal("deep.txt", entry.Name);
                Assert.True(entry.DeletedUtc > DateTime.MinValue);

                Assert.Equal("sub/deep.txt", reopened.RestoreRecycledEntry(entry.Id));
                Assert.Empty(reopened.ListRecycledEntries());
                await commit(reopened);
            }

            using (var verified = await open())
            {
                // Restored for good: back on the drive, and no leftover copy.
                Assert.Equal(0, verified.CountRecycledEntries());
                var source = Assert.Single(verified.CreateSnapshot(),
                    item => item.Path == "sub/deep.txt");
                await using var stream = await source.OpenReadAsync();
                using var reader = new StreamReader(stream);
                Assert.Equal("nested content", await reader.ReadToEndAsync());
            }
        });
    }

    [Fact]
    public async Task EmptyingTheBinAfterReopeningReclaimsTheRetainedCopies()
    {
        await WithReopenableVaultAsync(async (open, commit) =>
        {
            using (var overlay = await open())
            {
                overlay.Cleanup("\\notes.txt", Info(deletePending: true));
                await commit(overlay);
            }

            using (var reopened = await open())
            {
                Assert.Equal(1, reopened.EmptyRecycleBin());
                await commit(reopened);
            }

            using (var verified = await open())
            {
                // Emptying must reach the container, not just the live mount,
                // or the space would come back on the next unlock.
                Assert.Equal(0, verified.CountRecycledEntries());
                Assert.DoesNotContain(verified.CreateSnapshot(), source =>
                    source.Path.StartsWith(VaultReadWriteFileSystem.RecycleFolderName,
                        StringComparison.Ordinal));
            }
        });
    }

    [Fact]
    public void TheServiceReportsNoRecycledEntriesForAVaultThatIsNotMounted()
    {
        using var service = new VaultService();
        var vault = new VaultContainer { Id = Guid.NewGuid(), Name = "Unmounted" };

        // The recycle area lives inside the encrypted container, so it cannot
        // be counted without the data key. The UI relies on this to disable the
        // command instead of offering one that cannot work.
        Assert.Equal(0, service.CountRecycledEntries(vault));
        Assert.Equal(-1, service.EmptyRecycleBin(vault));
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
    }

    private static IDokanFileInfo Info(bool deletePending = false) =>
        new TestFileInfo { DeletePending = deletePending };

    private static async Task WithVaultAsync(Func<VaultReadWriteFileSystem, Task> body,
        bool writeProtected = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp-Recycle-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        var vaultPath = Path.Combine(root, "test.pavault");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "notes.txt"), "recoverable content");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "sub", "deep.txt"), "nested content");
        var vault = new VaultContainer { Id = Guid.NewGuid(), Name = "Recycle", AutoLockMinutes = 5 };
        const string password = "ProtectedApp-Recycle-Bin";
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, vaultPath, password, sourceRoot,
                createRecoveryBackup: false);
            using var opened = await VaultFormatV3.OpenAsync(vaultPath, password);
            using var overlay = new VaultReadWriteFileSystem(opened, writeProtected: writeProtected);
            await body(overlay);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a vault on disk that can be closed
    /// and unlocked again. <c>open</c> unlocks the container and hands back a
    /// fresh mount; <c>commit</c> writes that mount back to the same file, which
    /// is what the UI does when the user saves.
    /// </summary>
    private static async Task WithReopenableVaultAsync(
        Func<Func<Task<VaultReadWriteFileSystem>>, Func<VaultReadWriteFileSystem, Task>, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp-RecycleCycle-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        var vaultPath = Path.Combine(root, "test.pavault");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "notes.txt"), "recoverable content");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "sub", "deep.txt"), "nested content");
        var vault = new VaultContainer { Id = Guid.NewGuid(), Name = "RecycleCycle", AutoLockMinutes = 5 };
        const string password = "ProtectedApp-Recycle-Cycle";
        var opened = new List<VaultFormatV3.OpenedVault>();
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, vaultPath, password, sourceRoot,
                createRecoveryBackup: false);

            async Task<VaultReadWriteFileSystem> OpenAsync()
            {
                var container = await VaultFormatV3.OpenAsync(vaultPath, password);
                opened.Add(container);
                return new VaultReadWriteFileSystem(container);
            }

            async Task CommitAsync(VaultReadWriteFileSystem overlay)
            {
                // The snapshot reads lazily from the open container, so it must
                // still be open while the new file is written. That is what the
                // app does when it saves a mounted vault, recovery copy and all.
                var container = opened[^1];
                await VaultFormatV3.WriteFromVirtualEntriesAsync(vault, overlay.CreateSnapshot(),
                    vaultPath, container.PasswordKey, container.Salt, container.DataKey,
                    container.TpmBinding, createRecoveryBackup: true);
                // Saved: release the container so the next unlock, and the next
                // save's recovery copy, are not blocked by this handle.
                container.Dispose();
                opened.Remove(container);
            }

            await body(OpenAsync, CommitAsync);
        }
        finally
        {
            foreach (var container in opened)
            {
                try { container.Dispose(); } catch (IOException) { }
            }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class TestFileInfo : IDokanFileInfo
    {
        public object? Context { get; set; }
        public bool DeleteOnClose { get; set; }
        public bool DeletePending { get; set; }
        public bool IsDirectory { get; set; }
        public bool NoCache { get; set; }
        public bool PagingIo { get; set; }
        public int ProcessId => 0;
        public bool SynchronousIo { get; set; }
        public bool WriteToEndOfFile { get; set; }
        public System.Security.Principal.WindowsIdentity GetRequestor() =>
            System.Security.Principal.WindowsIdentity.GetCurrent();
        public bool TryResetTimeout(int milliseconds) => true;
    }
}
