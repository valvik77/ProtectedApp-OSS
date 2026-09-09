using System.IO.Enumeration;
using System.Security.AccessControl;
using DokanNet;
using DokanFileAccess = DokanNet.FileAccess;

namespace ProtectedApp.Services;

internal sealed class VaultReadWriteFileSystem : IDokanOperations, IDisposable
{
    private const long Capacity = 1024L * 1024 * 1024;
    private const int BlockSize = 64 * 1024;
    private readonly VaultFormatV3.OpenedVault _opened;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly Action? _activityObserved;
    private long _changeVersion;

    public VaultReadWriteFileSystem(VaultFormatV3.OpenedVault opened, Action? activityObserved = null)
    {
        _opened = opened;
        _activityObserved = activityObserved;
        _nodes[string.Empty] = new Node(string.Empty, true, 0, DateTime.UtcNow, DateTime.UtcNow, true,
            string.Empty, 0);
        foreach (var entry in opened.Index.Entries)
        {
            var path = NormalizePath(entry.Path);
            EnsureParents(path);
            _nodes[path] = new Node(path, entry.IsDirectory, entry.Length, entry.CreationUtc,
                entry.LastWriteUtc, true, path, entry.Length);
        }
    }

    public bool HasChanges { get; private set; }
    public bool NeedsJournal { get; private set; }
    public Func<IReadOnlyList<VaultFormatV3.VirtualEntrySource>, Task>? JournalWriter { get; set; }
    public async Task SaveJournalAsync()
    {
        if (!NeedsJournal || JournalWriter is null) return;
        await _journalGate.WaitAsync();
        try
        {
            var writer = JournalWriter;
            if (!NeedsJournal || writer is null) return;
            long version;
            lock (_sync) version = _changeVersion;
            await writer(CreateSnapshot());
            lock (_sync)
            {
                if (_changeVersion == version) NeedsJournal = false;
            }
        }
        finally { _journalGate.Release(); }
    }

    // Dokany invokes Cleanup/FlushFileBuffers while its instance is being
    // disposed. Do not synchronously create a journal from those callbacks:
    // the final unmount already created one and will commit the vault itself.
    public void SuspendJournalCallbacks() => JournalWriter = null;

    public IReadOnlyList<VaultFormatV3.VirtualEntrySource> CreateSnapshot()
    {
        lock (_sync)
        {
            return _nodes.Values.Where(node => node.Path.Length > 0)
                .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
                .Select(node => SnapshotNode.From(node))
                .Select(node => new VaultFormatV3.VirtualEntrySource(node.Path, node.IsDirectory,
                    node.IsDirectory ? 0 : node.Length, node.CreationUtc, node.LastWriteUtc,
                    node.IsDirectory
                        ? () => Task.FromResult<Stream>(Stream.Null)
                        : () => Task.FromResult<Stream>(new OverlayReadStream(_opened, node))))
                .ToArray();
        }
    }

    public NtStatus CreateFile(string fileName, DokanFileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        var path = NormalizePath(fileName);
        if (path.StartsWith('\0')) return NtStatus.ObjectNameInvalid;
        ReportActivity();
        lock (_sync)
        {
            var exists = _nodes.TryGetValue(path, out var node);
            if (!exists)
            {
                if (mode is FileMode.Open or FileMode.Truncate) return NtStatus.ObjectNameNotFound;
                var parent = GetParent(path);
                if (!_nodes.TryGetValue(parent, out var parentNode) || !parentNode.IsDirectory)
                    return NtStatus.ObjectPathNotFound;
                node = new Node(path, info.IsDirectory, 0, DateTime.UtcNow, DateTime.UtcNow, false,
                    null, 0);
                _nodes[path] = node;
                MarkChanged();
            }
            else if (mode == FileMode.CreateNew)
            {
                return NtStatus.ObjectNameCollision;
            }

            if (node!.IsDirectory)
            {
                info.IsDirectory = true;
                info.Context = node;
                return NtStatus.Success;
            }
            if (mode is FileMode.Create or FileMode.Truncate)
            {
                node.Length = 0;
                node.OriginalReadableLength = 0;
                ClearBlocks(node);
                node.LastWriteUtc = DateTime.UtcNow;
                node.FromOriginal = false;
                MarkChanged();
            }
            info.Context = node;
            return NtStatus.Success;
        }
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        if (info.DeletePending)
        {
            lock (_sync) RemovePath(NormalizePath(fileName), info.IsDirectory);
        }
        if (NeedsJournal && JournalWriter is not null)
        {
            try { SaveJournalAsync().GetAwaiter().GetResult(); }
            catch { /* El commit final reintentará; Dokany no permite devolver estado desde Cleanup. */ }
        }
        info.Context = null;
    }

    public void CloseFile(string fileName, IDokanFileInfo info) => info.Context = null;

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        try
        {
            ReportActivity();
            lock (_sync)
            {
                if (!_nodes.TryGetValue(NormalizePath(fileName), out var node)) return NtStatus.ObjectNameNotFound;
                if (node.IsDirectory || offset < 0) return NtStatus.InvalidParameter;
                if (offset >= node.Length) return NtStatus.Success;
                bytesRead = ReadNodeRange(node, buffer, 0, offset,
                    (int)Math.Min(buffer.Length, node.Length - offset));
                return NtStatus.Success;
            }
        }
        catch (InvalidDataException) { return NtStatus.DataError; }
        catch (IOException) { return NtStatus.Unsuccessful; }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        if (offset < 0) return NtStatus.InvalidParameter;
        ReportActivity();
        lock (_sync)
        {
            if (!_nodes.TryGetValue(NormalizePath(fileName), out var node)) return NtStatus.ObjectNameNotFound;
            if (node.IsDirectory) return NtStatus.InvalidParameter;
            if (offset + buffer.Length > Capacity) return NtStatus.DiskFull;
            WriteNodeRange(node, buffer, 0, offset, buffer.Length);
            node.Length = Math.Max(node.Length, offset + buffer.Length);
            node.LastWriteUtc = DateTime.UtcNow;
            bytesWritten = buffer.Length;
            MarkChanged();
            return NtStatus.Success;
        }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        if (!NeedsJournal || JournalWriter is null) return NtStatus.Success;
        try
        {
            SaveJournalAsync().GetAwaiter().GetResult();
            return NtStatus.Success;
        }
        catch (IOException) { return NtStatus.Unsuccessful; }
        catch (InvalidDataException) { return NtStatus.DataError; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        lock (_sync)
        {
            if (!_nodes.TryGetValue(NormalizePath(fileName), out var node))
            {
                fileInfo = new FileInformation();
                return NtStatus.ObjectNameNotFound;
            }
            fileInfo = ToInfo(node);
            return NtStatus.Success;
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info) =>
        FindFilesCore(fileName, null, out files);

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern,
        out IList<FileInformation> files, IDokanFileInfo info) => FindFilesCore(fileName, searchPattern, out files);

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) =>
        _nodes.ContainsKey(NormalizePath(fileName)) ? NtStatus.Success : NtStatus.ObjectNameNotFound;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info)
    {
        lock (_sync)
        {
            if (!_nodes.TryGetValue(NormalizePath(fileName), out var node)) return NtStatus.ObjectNameNotFound;
            if (creationTime.HasValue) node.CreationUtc = creationTime.Value.ToUniversalTime();
            if (lastWriteTime.HasValue) node.LastWriteUtc = lastWriteTime.Value.ToUniversalTime();
            MarkChanged();
            return NtStatus.Success;
        }
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        lock (_sync)
        {
            var path = NormalizePath(fileName);
            return _nodes.TryGetValue(path, out var node) && !node.IsDirectory
                ? NtStatus.Success : NtStatus.ObjectNameNotFound;
        }
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        lock (_sync)
        {
            var path = NormalizePath(fileName);
            if (!_nodes.TryGetValue(path, out var node) || !node.IsDirectory) return NtStatus.ObjectPathNotFound;
            return _nodes.Keys.Any(candidate => GetParent(candidate).Equals(path, StringComparison.OrdinalIgnoreCase))
                ? NtStatus.DirectoryNotEmpty : NtStatus.Success;
        }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        lock (_sync)
        {
            var oldPath = NormalizePath(oldName);
            var newPath = NormalizePath(newName);
            if (!_nodes.TryGetValue(oldPath, out var node)) return NtStatus.ObjectNameNotFound;
            if (_nodes.ContainsKey(newPath) && !replace) return NtStatus.ObjectNameCollision;
            if (_nodes.ContainsKey(newPath)) _nodes.Remove(newPath);
            var affected = _nodes.Values.Where(item => item.Path.Equals(oldPath, StringComparison.OrdinalIgnoreCase)
                || item.Path.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var item in affected) _nodes.Remove(item.Path);
            foreach (var item in affected)
            {
                item.Path = newPath + item.Path[oldPath.Length..];
                _nodes[item.Path] = item;
            }
            MarkChanged();
            return NtStatus.Success;
        }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (length < 0 || length > Capacity) return NtStatus.DiskFull;
        lock (_sync)
        {
            if (!_nodes.TryGetValue(NormalizePath(fileName), out var node) || node.IsDirectory)
                return NtStatus.ObjectNameNotFound;
            if (length < node.Length)
                TruncateNode(node, length);
            node.Length = length;
            node.LastWriteUtc = DateTime.UtcNow;
            MarkChanged();
            return NtStatus.Success;
        }
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        // Windows can reserve a larger allocation before or after changing
        // EOF.  Allocation must not alter the logical file length; treating
        // it as SetEndOfFile re-expanded files that had just been truncated.
        if (length < 0 || length > Capacity) return NtStatus.DiskFull;
        lock (_sync)
        {
            return _nodes.TryGetValue(NormalizePath(fileName), out var node) && !node.IsDirectory
                ? NtStatus.Success
                : NtStatus.ObjectNameNotFound;
        }
    }
    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;
    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        lock (_sync)
        {
            var used = _nodes.Values.Where(node => !node.IsDirectory).Sum(node => node.Length);
            totalNumberOfBytes = Capacity;
            freeBytesAvailable = totalNumberOfFreeBytes = Math.Max(0, Capacity - used);
            return NtStatus.Success;
        }
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = SanitizeVolumeLabel(_opened.Vault.Name);
        fileSystemName = "PAVLT003";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
        return NtStatus.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info) { security = null!; return NtStatus.NotImplemented; }
    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info) => NtStatus.NotImplemented;
    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => NtStatus.Success;
    public NtStatus Unmounted(IDokanFileInfo info) => NtStatus.Success;
    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    { streams = Array.Empty<FileInformation>(); return NtStatus.NotImplemented; }

    private NtStatus FindFilesCore(string fileName, string? pattern, out IList<FileInformation> files)
    {
        lock (_sync)
        {
            var path = NormalizePath(fileName);
            if (!_nodes.TryGetValue(path, out var node) || !node.IsDirectory)
            { files = Array.Empty<FileInformation>(); return NtStatus.ObjectPathNotFound; }
            files = _nodes.Values.Where(child => GetParent(child.Path).Equals(path, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(pattern) || FileSystemName.MatchesWin32Expression(pattern,
                        GetName(child.Path), true)))
                .Select(ToInfo).ToArray();
            return NtStatus.Success;
        }
    }

    private void RemovePath(string path, bool directory)
    {
        if (!_nodes.TryGetValue(path, out var node) || node.IsDirectory != directory) return;
        if (directory && _nodes.Keys.Any(candidate => GetParent(candidate).Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        ClearBlocks(node);
        _nodes.Remove(path);
        MarkChanged();
    }

    private void MarkChanged()
    {
        HasChanges = true;
        NeedsJournal = true;
        _changeVersion++;
    }

    private void ReportActivity()
    {
        try { _activityObserved?.Invoke(); }
        catch { /* Un aviso de actividad nunca debe interrumpir Dokany. */ }
    }

    private static string SanitizeVolumeLabel(string name)
    {
        var cleaned = new string(name.Where(character => !"\\/:*?\"<>|".Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "ProtectedApp" : cleaned[..Math.Min(cleaned.Length, 32)];
    }

    private void EnsureParents(string path)
    {
        var parent = GetParent(path);
        while (parent.Length > 0)
        {
            if (!_nodes.ContainsKey(parent)) _nodes[parent] = new Node(parent, true, 0, DateTime.UtcNow,
                DateTime.UtcNow, true, parent, 0);
            parent = GetParent(parent);
        }
    }

    private int ReadNodeRange(Node node, byte[] buffer, int bufferOffset, long offset, int count) =>
        ReadNodeRange(_opened, node.SourcePath, node.FromOriginal, node.OriginalReadableLength, node.Blocks,
            buffer, bufferOffset, offset, count);

    private static int ReadNodeRange(VaultFormatV3.OpenedVault opened, string? sourcePath, bool fromOriginal,
        long originalReadableLength, IReadOnlyDictionary<long, byte[]>? blocks, byte[] buffer, int bufferOffset,
        long offset, int count)
    {
        var remaining = count;
        var position = offset;
        while (remaining > 0)
        {
            var blockIndex = position / BlockSize;
            var blockOffset = (int)(position % BlockSize);
            var take = Math.Min(remaining, BlockSize - blockOffset);
            if (blocks is not null && blocks.TryGetValue(blockIndex, out var block))
            {
                Buffer.BlockCopy(block, blockOffset, buffer, bufferOffset, take);
            }
            else if (fromOriginal && sourcePath is not null && position < originalReadableLength)
            {
                var originalTake = (int)Math.Min(take, originalReadableLength - position);
                var data = VaultFormatV3.ReadFileRangeAsync(opened, sourcePath, position, originalTake)
                    .GetAwaiter().GetResult();
                try { Buffer.BlockCopy(data, 0, buffer, bufferOffset, data.Length); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
                if (data.Length < take) Array.Clear(buffer, bufferOffset + data.Length, take - data.Length);
            }
            else
            {
                Array.Clear(buffer, bufferOffset, take);
            }
            position += take;
            bufferOffset += take;
            remaining -= take;
        }
        return count;
    }

    private void WriteNodeRange(Node node, byte[] buffer, int bufferOffset, long offset, int count)
    {
        var remaining = count;
        var position = offset;
        while (remaining > 0)
        {
            var blockIndex = position / BlockSize;
            var blockOffset = (int)(position % BlockSize);
            var take = Math.Min(remaining, BlockSize - blockOffset);
            var block = GetWritableBlock(node, blockIndex);
            Buffer.BlockCopy(buffer, bufferOffset, block, blockOffset, take);
            position += take;
            bufferOffset += take;
            remaining -= take;
        }
    }

    private byte[] GetWritableBlock(Node node, long blockIndex)
    {
        node.Blocks ??= new Dictionary<long, byte[]>();
        if (node.Blocks.TryGetValue(blockIndex, out var existing)) return existing;
        var block = new byte[BlockSize];
        var offset = blockIndex * (long)BlockSize;
        if (node.FromOriginal && node.SourcePath is not null && offset < node.OriginalReadableLength)
        {
            var bytes = (int)Math.Min(BlockSize, node.OriginalReadableLength - offset);
            var data = VaultFormatV3.ReadFileRangeAsync(_opened, node.SourcePath, offset, bytes)
                .GetAwaiter().GetResult();
            try { Buffer.BlockCopy(data, 0, block, 0, data.Length); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
        }
        node.Blocks[blockIndex] = block;
        return block;
    }

    private void TruncateNode(Node node, long length)
    {
        node.OriginalReadableLength = Math.Min(node.OriginalReadableLength, length);
        if (node.Blocks is null) return;
        var lastBlock = length / BlockSize;
        foreach (var blockIndex in node.Blocks.Keys.Where(index => index > lastBlock).ToArray())
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(node.Blocks[blockIndex]);
            node.Blocks.Remove(blockIndex);
        }
        var tail = (int)(length % BlockSize);
        if (tail > 0)
        {
            var block = GetWritableBlock(node, lastBlock);
            Array.Clear(block, tail, BlockSize - tail);
        }
    }

    private static FileInformation ToInfo(Node node) => new()
    {
        FileName = GetName(node.Path), Attributes = node.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
        CreationTime = node.CreationUtc, LastAccessTime = node.LastWriteUtc, LastWriteTime = node.LastWriteUtc,
        Length = node.IsDirectory ? 0 : node.Length
    };
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "\\" or "/") return string.Empty;
        var parts = path.Trim().Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.Contains(':'))) return "\0invalid";
        return string.Join('/', parts);
    }
    private static string GetParent(string path) { var i = path.LastIndexOf('/'); return i < 0 ? string.Empty : path[..i]; }
    private static string GetName(string path) { var i = path.LastIndexOf('/'); return i < 0 ? path : path[(i + 1)..]; }

    private static void ClearBlocks(Node node)
    {
        if (node.Blocks is null) return;
        foreach (var block in node.Blocks.Values)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(block);
        node.Blocks.Clear();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var node in _nodes.Values) ClearBlocks(node);
            _nodes.Clear();
        }
    }

    private sealed class Node(string path, bool isDirectory, long length, DateTime creationUtc,
        DateTime lastWriteUtc, bool fromOriginal, string? sourcePath, long originalReadableLength)
    {
        public string Path { get; set; } = path;
        public bool IsDirectory { get; } = isDirectory;
        public long Length { get; set; } = length;
        public DateTime CreationUtc { get; set; } = creationUtc;
        public DateTime LastWriteUtc { get; set; } = lastWriteUtc;
        public bool FromOriginal { get; set; } = fromOriginal;
        public string? SourcePath { get; } = sourcePath;
        public long OriginalReadableLength { get; set; } = originalReadableLength;
        public Dictionary<long, byte[]>? Blocks { get; set; }
    }

    private sealed class SnapshotNode(string path, bool isDirectory, long length, DateTime creationUtc,
        DateTime lastWriteUtc, bool fromOriginal, string? sourcePath, long originalReadableLength,
        IReadOnlyDictionary<long, byte[]>? blocks)
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; } = isDirectory;
        public long Length { get; } = length;
        public DateTime CreationUtc { get; } = creationUtc;
        public DateTime LastWriteUtc { get; } = lastWriteUtc;
        public bool FromOriginal { get; } = fromOriginal;
        public string? SourcePath { get; } = sourcePath;
        public long OriginalReadableLength { get; } = originalReadableLength;
        public IReadOnlyDictionary<long, byte[]>? Blocks { get; } = blocks;

        public static SnapshotNode From(Node node)
        {
            Dictionary<long, byte[]>? blocks = null;
            if (node.Blocks is { Count: > 0 })
                blocks = node.Blocks.ToDictionary(item => item.Key, item => item.Value.ToArray());
            return new SnapshotNode(node.Path, node.IsDirectory, node.Length, node.CreationUtc,
                node.LastWriteUtc, node.FromOriginal, node.SourcePath, node.OriginalReadableLength, blocks);
        }
    }

    private sealed class OverlayReadStream(VaultFormatV3.OpenedVault opened, SnapshotNode node) : Stream
    {
        private long _position;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => node.Length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, node.Length); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, node.Length - _position);
            if (read <= 0) return 0;
            ReadNodeRange(opened, node.SourcePath, node.FromOriginal, node.OriginalReadableLength, node.Blocks,
                buffer, offset, _position, read);
            _position += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var temporary = new byte[buffer.Length];
            var read = Read(temporary, 0, temporary.Length);
            temporary.AsMemory(0, read).CopyTo(buffer);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(temporary);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => node.Length + offset }; return _position; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
