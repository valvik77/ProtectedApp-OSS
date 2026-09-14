using System.IO.Enumeration;
using System.Security.AccessControl;
using DokanNet;
using DokanFileAccess = DokanNet.FileAccess;

namespace ProtectedApp.Services;

internal sealed class VaultReadWriteFileSystem : IDokanOperations, IDisposable
{
    private const DokanFileAccess WriteAccess = DokanFileAccess.WriteData | DokanFileAccess.AppendData
        | DokanFileAccess.WriteExtendedAttributes | DokanFileAccess.WriteAttributes | DokanFileAccess.Delete
        | DokanFileAccess.DeleteChild | DokanFileAccess.ChangePermissions | DokanFileAccess.SetOwnership
        | DokanFileAccess.GenericWrite | DokanFileAccess.GenericAll;
    private const long Capacity = 1024L * 1024 * 1024;
    private const int BlockSize = 64 * 1024;
    private readonly VaultFormatV3.OpenedVault _opened;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Node>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly Action? _activityObserved;
    private readonly bool _writeProtected;
    private long _changeVersion;
    private int _journalSaveQueued;

    public VaultReadWriteFileSystem(VaultFormatV3.OpenedVault opened, Action? activityObserved = null,
        bool writeProtected = false)
    {
        _opened = opened;
        _activityObserved = activityObserved;
        _writeProtected = writeProtected;
        _nodes[string.Empty] = new Node(string.Empty, true, 0, DateTime.UtcNow, DateTime.UtcNow, true,
            string.Empty, 0);
        foreach (var entry in opened.Index.Entries)
        {
            var path = NormalizePath(entry.Path);
            EnsureParents(path);
            _nodes[path] = new Node(path, entry.IsDirectory, entry.Length, entry.CreationUtc,
                entry.LastWriteUtc, true, path, entry.Length);
        }
        RebuildChildren();
    }

    public bool HasChanges { get; private set; }
    public bool NeedsJournal { get; private set; }
    public Func<Task>? JournalWriter { get; set; }
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
            await writer();
            lock (_sync)
            {
                if (_changeVersion == version) NeedsJournal = false;
            }
        }
        finally { _journalGate.Release(); }
    }

    private void QueueJournalSave()
    {
        if (!NeedsJournal || JournalWriter is null || Interlocked.Exchange(ref _journalSaveQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Explorer closes several metadata handles for one visible
                // save. Coalescing them prevents each close from blocking the
                // editor while the encrypted recovery journal is rebuilt.
                await Task.Delay(250).ConfigureAwait(false);
                await SaveJournalAsync().ConfigureAwait(false);
            }
            catch { /* The durable flush/final lock will retry the journal. */ }
            finally { Interlocked.Exchange(ref _journalSaveQueued, 0); }
        });
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
        if (_writeProtected && ((access & WriteAccess) != 0 || mode != FileMode.Open
                                || options.HasFlag(FileOptions.DeleteOnClose))) return NtStatus.AccessDenied;
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
                AddChild(node);
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
        // Cleanup is issued for ordinary handle lifetime events. Do not block
        // the calling application on a full vault journal here; a real
        // FlushFileBuffers remains synchronous below and therefore durable.
        QueueJournalSave();
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
        if (_writeProtected) return NtStatus.AccessDenied;
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
        _writeProtected ? NtStatus.AccessDenied
            : _nodes.ContainsKey(NormalizePath(fileName)) ? NtStatus.Success : NtStatus.ObjectNameNotFound;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info)
    {
        if (_writeProtected) return NtStatus.AccessDenied;
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
        if (_writeProtected) return NtStatus.AccessDenied;
        lock (_sync)
        {
            var path = NormalizePath(fileName);
            return _nodes.TryGetValue(path, out var node) && !node.IsDirectory
                ? NtStatus.Success : NtStatus.ObjectNameNotFound;
        }
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        if (_writeProtected) return NtStatus.AccessDenied;
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
        if (_writeProtected) return NtStatus.AccessDenied;
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
            RebuildChildren();
            MarkChanged();
            return NtStatus.Success;
        }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (_writeProtected) return NtStatus.AccessDenied;
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
        if (_writeProtected) return NtStatus.AccessDenied;
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
            files = (_children.TryGetValue(path, out var children) ? children : [])
                .Where(child => string.IsNullOrWhiteSpace(pattern) || FileSystemName.MatchesWin32Expression(pattern,
                    GetName(child.Path), true))
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
        RebuildChildren();
        MarkChanged();
    }

    private void MarkChanged()
    {
        HasChanges = true;
        NeedsJournal = true;
        _changeVersion++;
    }

    internal VaultJournalSnapshot CreateJournalSnapshot()
    {
        lock (_sync)
        {
            return new VaultJournalSnapshot(_nodes.Values.Where(node => node.Path.Length > 0)
                .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
                .Select(node => new VaultJournalNode(node.Path, node.IsDirectory, node.Length, node.CreationUtc,
                    node.LastWriteUtc, node.FromOriginal, node.SourcePath, node.OriginalReadableLength,
                    node.Blocks?.ToDictionary(item => item.Key, item => item.Value.ToArray())))
                .ToArray());
        }
    }

    internal long EstimateJournalPlaintextBytes()
    {
        lock (_sync)
        {
            return _nodes.Values.Where(node => node.Path.Length > 0).Sum(node =>
                256L + (node.Path.Length + (node.SourcePath?.Length ?? 0)) * 4L
                + (node.Blocks?.Count ?? 0) * (BlockSize + 48L));
        }
    }

    internal void ApplyJournalSnapshot(VaultJournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Nodes.Count > 20_000) throw new InvalidDataException("El diario contiene demasiados elementos.");
        lock (_sync)
        {
            var restored = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = new Node(string.Empty, true, 0, DateTime.UtcNow, DateTime.UtcNow, true,
                    string.Empty, 0)
            };
            long totalLength = 0;
            foreach (var item in snapshot.Nodes)
            {
                var path = NormalizePath(item.Path);
                if (path.Length == 0 || path.StartsWith('\0') || item.Length < 0 || item.Length > Capacity
                    || item.OriginalReadableLength < 0 || item.OriginalReadableLength > Capacity)
                    throw new InvalidDataException("El diario contiene una entrada no válida.");
                if (item.IsDirectory && (item.Length != 0 || item.Blocks is { Count: > 0 }))
                    throw new InvalidDataException("El diario contiene un directorio no válido.");
                var sourcePath = item.SourcePath;
                if (item.FromOriginal && !item.IsDirectory)
                {
                    if (string.IsNullOrWhiteSpace(sourcePath)
                        || !_opened.TryGetEntryIndex(sourcePath, out var sourceIndex)
                        || _opened.Index.Entries[sourceIndex].IsDirectory)
                    {
                        // A crash can occur after the new primary container is
                        // atomically installed but before its journal is
                        // deleted. Renamed entries then already use their new
                        // path in the primary; applying the journal again must
                        // remain idempotent.
                        sourcePath = path;
                        if (!_opened.TryGetEntryIndex(sourcePath, out sourceIndex)
                            || _opened.Index.Entries[sourceIndex].IsDirectory)
                            throw new InvalidDataException("El diario hace referencia a contenido base no válido.");
                    }
                    if (item.OriginalReadableLength > _opened.Index.Entries[sourceIndex].Length)
                        throw new InvalidDataException("El diario supera la longitud del contenido base.");
                }
                totalLength = checked(totalLength + item.Length);
                if (totalLength > Capacity) throw new InvalidDataException("El diario supera la capacidad de la bóveda.");
                Dictionary<long, byte[]>? blocks = null;
                if (item.Blocks is { Count: > 0 })
                {
                    blocks = new Dictionary<long, byte[]>();
                    foreach (var block in item.Blocks)
                    {
                        if (block.Key < 0 || block.Value.Length != BlockSize
                            || block.Key * (long)BlockSize >= Capacity)
                            throw new InvalidDataException("El diario contiene un bloque no válido.");
                        blocks.Add(block.Key, block.Value.ToArray());
                    }
                }
                var node = new Node(path, item.IsDirectory, item.Length, item.CreationUtc, item.LastWriteUtc,
                    item.FromOriginal, sourcePath, item.OriginalReadableLength) { Blocks = blocks };
                if (!restored.TryAdd(path, node)) throw new InvalidDataException("El diario contiene rutas duplicadas.");
            }
            foreach (var node in restored.Values.Where(node => node.Path.Length > 0))
            {
                var parent = GetParent(node.Path);
                if (!restored.TryGetValue(parent, out var parentNode) || !parentNode.IsDirectory)
                    throw new InvalidDataException("El diario contiene una jerarquía no válida.");
            }
            foreach (var oldNode in _nodes.Values) ClearBlocks(oldNode);
            _nodes.Clear();
            foreach (var item in restored) _nodes.Add(item.Key, item.Value);
            RebuildChildren();
            HasChanges = true;
            NeedsJournal = false;
            _changeVersion++;
        }
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

    private void RebuildChildren()
    {
        _children.Clear();
        foreach (var node in _nodes.Values.Where(node => node.Path.Length > 0)) AddChild(node);
        foreach (var children in _children.Values)
            children.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(GetName(left.Path), GetName(right.Path)));
    }

    private void AddChild(Node node)
    {
        var parent = GetParent(node.Path);
        if (!_children.TryGetValue(parent, out var children)) _children[parent] = children = [];
        children.Add(node);
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
            _children.Clear();
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

internal sealed record VaultJournalSnapshot(IReadOnlyList<VaultJournalNode> Nodes) : IDisposable
{
    public void Dispose()
    {
        foreach (var node in Nodes)
        {
            if (node.Blocks is null) continue;
            foreach (var block in node.Blocks.Values)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(block);
        }
    }
}
internal sealed record VaultJournalNode(string Path, bool IsDirectory, long Length, DateTime CreationUtc,
    DateTime LastWriteUtc, bool FromOriginal, string? SourcePath, long OriginalReadableLength,
    Dictionary<long, byte[]>? Blocks);
