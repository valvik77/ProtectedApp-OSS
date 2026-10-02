using DokanNet;
using ProtectedApp.Models;
using ProtectedApp.Services;
using Xunit;
using DokanFileAccess = DokanNet.FileAccess;

namespace ProtectedApp.Vault.Tests;

/// <summary>
/// Hiding the recycle area from the root listing is not an access control on
/// its own. Its names are derived from a deletion timestamp and the original
/// path, so an application that guesses or remembers one must still be refused:
/// otherwise retained content stays readable, and on an editable mount
/// writable, by anything running against the mounted drive.
/// </summary>
public sealed class VaultRecycleBinAccessTests
{
    [Fact]
    public async Task TheRetainedCopyCannotBeReadByItsPath()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup(@"\notes.txt", Info(deletePending: true));
            var id = Assert.Single(overlay.ListRecycledEntries()).Id;
            var recycledPath = ToMountPath(id);

            Assert.Equal(NtStatus.ObjectNameNotFound, overlay.CreateFile(recycledPath,
                DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open,
                FileOptions.None, FileAttributes.Normal, Info()));

            var buffer = new byte[64];
            Assert.Equal(NtStatus.ObjectNameNotFound,
                overlay.ReadFile(recycledPath, buffer, out var read, 0, Info()));
            Assert.Equal(0, read);
            Assert.Equal(NtStatus.ObjectNameNotFound,
                overlay.GetFileInformation(recycledPath, out _, Info()));

            // Refusing the drive access must not cost the user the recovery.
            Assert.Equal("notes.txt", overlay.RestoreRecycledEntry(id));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheRetainedCopyCannotBeAlteredByItsPath()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup(@"\notes.txt", Info(deletePending: true));
            var id = Assert.Single(overlay.ListRecycledEntries()).Id;
            var recycledPath = ToMountPath(id);

            Assert.Equal(NtStatus.ObjectNameNotFound, overlay.WriteFile(recycledPath,
                new byte[] { 1, 2, 3 }, out var written, 0, Info()));
            Assert.Equal(0, written);
            Assert.Equal(NtStatus.ObjectNameNotFound, overlay.SetEndOfFile(recycledPath, 0, Info()));
            Assert.Equal(NtStatus.ObjectNameNotFound, overlay.DeleteFile(recycledPath, Info()));
            Assert.Equal(NtStatus.ObjectNameNotFound,
                overlay.SetFileTime(recycledPath, DateTime.UtcNow, null, DateTime.UtcNow, Info()));

            // Renaming must not lift it out of the recycle area, nor push a
            // live file into it behind the UI's back.
            Assert.Equal(NtStatus.ObjectNameNotFound,
                overlay.MoveFile(recycledPath, @"\escaped.txt", false, Info()));
            Assert.Equal(NtStatus.AccessDenied,
                overlay.MoveFile(@"\sub\deep.txt", recycledPath + ".x", false, Info()));

            // What the user gets back is still the original content.
            Assert.Equal("notes.txt", overlay.RestoreRecycledEntry(id));
            var buffer = new byte[64];
            Assert.Equal(NtStatus.Success,
                overlay.ReadFile(@"\notes.txt", buffer, out var read, 0, Info()));
            Assert.Equal("recoverable content", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheRecycleFolderItselfCannotBeBrowsedOrRemoved()
    {
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup(@"\notes.txt", Info(deletePending: true));
            var folder = @"\" + VaultReadWriteFileSystem.RecycleFolderName;

            Assert.Equal(NtStatus.ObjectNameNotFound, overlay.CreateFile(folder,
                DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open,
                FileOptions.None, FileAttributes.Directory, Info(directory: true)));
            Assert.Equal(NtStatus.ObjectPathNotFound, overlay.FindFiles(folder, out var inside, null!));
            Assert.Empty(inside);
            Assert.Equal(NtStatus.ObjectPathNotFound, overlay.DeleteDirectory(folder, Info()));

            // Nothing above may have discarded what it refused to expose.
            Assert.Equal(1, overlay.CountRecycledEntries());
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AReadOnlyMountNeverExposesTheRecycleArea()
    {
        // A read-only mount can neither restore nor empty the recycle area, so
        // it has no reason to carry it at all. This is the wider gap of the two:
        // without it the folder is listed at the root and read straight through.
        await WithVaultAsync(async overlay =>
        {
            overlay.Cleanup(@"\notes.txt", Info(deletePending: true));
            var id = Assert.Single(overlay.ListRecycledEntries()).Id;
            await Task.CompletedTask;
            return id;
        }, async (readOnly, id) =>
        {
            Assert.Equal(NtStatus.Success, readOnly.FindFiles(@"\", out var listing, null!));
            Assert.DoesNotContain(listing,
                item => item.FileName == VaultReadWriteFileSystem.RecycleFolderName);

            var recycledPath = ToMountPath(id);
            Assert.Equal(NtStatus.ObjectNameNotFound, readOnly.CreateFile(recycledPath,
                DokanFileAccess.GenericRead, FileShare.Read, FileMode.Open,
                FileOptions.None, FileAttributes.Normal, Info()));
            Assert.Equal(NtStatus.ObjectNameNotFound,
                readOnly.GetFileInformation(recycledPath, out _, Info()));
            Assert.Equal(NtStatus.ObjectNameNotFound,
                readOnly.ReadFile(recycledPath, new byte[64], out var read, 0, Info()));
            Assert.Equal(0, read);

            var folder = @"\" + VaultReadWriteFileSystem.RecycleFolderName;
            Assert.Equal(NtStatus.ObjectPathNotFound, readOnly.FindFiles(folder, out var inside, null!));
            Assert.Empty(inside);

            // The user's own content is of course still readable.
            Assert.Equal(NtStatus.Success, readOnly.FindFiles(@"\sub", out var sub, null!));
            Assert.Contains(sub, item => item.FileName == "deep.txt");
            await Task.CompletedTask;
        });
    }

    private static string ToMountPath(string recycledId) => @"\" + recycledId.Replace('/', '\\');

    private static IDokanFileInfo Info(bool deletePending = false, bool directory = false) =>
        new TestFileInfo { DeletePending = deletePending, IsDirectory = directory };

    private static Task WithVaultAsync(Func<VaultReadWriteFileSystem, Task> body) =>
        WithVaultAsync(async overlay => { await body(overlay); return string.Empty; }, null);

    /// <summary>
    /// Builds a vault, runs <paramref name="body"/> against an editable mount,
    /// then commits and runs <paramref name="readOnlyBody"/> against a
    /// read-only mount of the committed result, so the recycle area under test
    /// is one that really survived a save and reopen.
    /// </summary>
    private static async Task WithVaultAsync(Func<VaultReadWriteFileSystem, Task<string>> body,
        Func<VaultReadOnlyFileSystem, string, Task>? readOnlyBody)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp-RecycleAccess-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        var vaultPath = Path.Combine(root, "test.pavault");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "notes.txt"), "recoverable content");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "sub", "deep.txt"), "nested content");
        var vault = new VaultContainer { Id = Guid.NewGuid(), Name = "RecycleAccess", AutoLockMinutes = 5 };
        const string password = "ProtectedApp-Recycle-Access";
        try
        {
            await VaultFormatV3.WriteNewAsync(vault, vaultPath, password, sourceRoot,
                createRecoveryBackup: false);
            string carried;
            // The committed copy goes to its own path: the open handle above
            // still holds the original, and what matters here is reopening a
            // container that really went through a save.
            var committedPath = Path.Combine(root, "committed.pavault");
            using (var opened = await VaultFormatV3.OpenAsync(vaultPath, password))
            using (var overlay = new VaultReadWriteFileSystem(opened))
            {
                carried = await body(overlay);
                if (readOnlyBody is not null)
                    await VaultFormatV3.WriteFromVirtualEntriesAsync(vault, overlay.CreateSnapshot(),
                        committedPath, opened.PasswordKey, opened.Salt, opened.DataKey, opened.TpmBinding,
                        createRecoveryBackup: false);
            }
            if (readOnlyBody is null) return;
            using var reopened = await VaultFormatV3.OpenAsync(committedPath, password);
            var readOnly = new VaultReadOnlyFileSystem(reopened);
            await readOnlyBody(readOnly, carried);
        }
        finally
        {
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
