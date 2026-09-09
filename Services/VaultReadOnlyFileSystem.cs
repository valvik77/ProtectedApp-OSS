using System.IO.Enumeration;
using System.Security.AccessControl;
using DokanNet;
using DokanFileAccess = DokanNet.FileAccess;

namespace ProtectedApp.Services;

/// <summary>
/// Adaptador Dokany de solo lectura para PAVLT003. El catálogo permanece en
/// memoria, pero los datos se descifran por bloques directamente desde el
/// contenedor únicamente cuando Windows solicita una lectura.
/// </summary>
internal sealed class VaultReadOnlyFileSystem : IDokanOperations
{
    private const DokanFileAccess WriteAccess = DokanFileAccess.WriteData
        | DokanFileAccess.AppendData | DokanFileAccess.WriteExtendedAttributes
        | DokanFileAccess.WriteAttributes | DokanFileAccess.Delete
        | DokanFileAccess.DeleteChild | DokanFileAccess.ChangePermissions
        | DokanFileAccess.SetOwnership | DokanFileAccess.GenericWrite
        | DokanFileAccess.GenericAll;

    private readonly VaultFormatV3.OpenedVault _opened;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Node>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _defaultCreatedUtc;
    private readonly DateTime _defaultModifiedUtc;
    private readonly Action? _activityObserved;

    public VaultReadOnlyFileSystem(VaultFormatV3.OpenedVault opened, Action? activityObserved = null)
    {
        _opened = opened;
        _activityObserved = activityObserved;
        _defaultCreatedUtc = NormalizeDate(opened.Vault.CreatedUtc, DateTime.UtcNow);
        _defaultModifiedUtc = NormalizeDate(opened.Vault.ModifiedUtc, _defaultCreatedUtc);
        _nodes[string.Empty] = new Node(string.Empty, string.Empty, true, 0,
            _defaultCreatedUtc, _defaultModifiedUtc);

        foreach (var entry in opened.Index.Entries)
        {
            var path = NormalizePath(entry.Path);
            EnsureParentDirectories(path);
            _nodes[path] = new Node(path, GetName(path), entry.IsDirectory, entry.Length,
                NormalizeDate(entry.CreationUtc, _defaultCreatedUtc),
                NormalizeDate(entry.LastWriteUtc, _defaultModifiedUtc));
        }

        foreach (var node in _nodes.Values.Where(node => node.Path.Length > 0))
        {
            var parent = GetParent(node.Path);
            if (!_children.TryGetValue(parent, out var children))
                _children[parent] = children = [];
            children.Add(node);
        }
        foreach (var children in _children.Values)
            children.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
    }

    internal bool TryGetNode(string path, out Node node) =>
        _nodes.TryGetValue(NormalizePath(path), out node!);

    internal IReadOnlyList<Node> GetChildren(string path) =>
        _children.TryGetValue(NormalizePath(path), out var children) ? children : [];

    internal async Task<byte[]> ReadAsync(string path, long offset, int count)
    {
        var normalized = NormalizePath(path);
        if (!_nodes.TryGetValue(normalized, out var node) || node.IsDirectory)
            throw new FileNotFoundException("El archivo no existe dentro de la bóveda.", path);
        return await VaultFormatV3.ReadFileRangeAsync(_opened, normalized, offset, count).ConfigureAwait(false);
    }

    public NtStatus CreateFile(string fileName, DokanFileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        if (!TryGetNode(fileName, out var node)) return NtStatus.ObjectNameNotFound;
        if ((access & WriteAccess) != 0 || mode != FileMode.Open || options.HasFlag(FileOptions.DeleteOnClose))
            return NtStatus.AccessDenied;
        if (node.IsDirectory && options.HasFlag(FileOptions.RandomAccess)) return NtStatus.InvalidParameter;
        ReportActivity();
        info.IsDirectory = node.IsDirectory;
        info.Context = node;
        return NtStatus.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info) => info.Context = null;

    public void CloseFile(string fileName, IDokanFileInfo info) => info.Context = null;

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        if (offset < 0 || !TryGetNode(fileName, out var node)) return NtStatus.ObjectNameNotFound;
        if (node.IsDirectory) return NtStatus.InvalidParameter;
        if (offset >= node.Length || buffer.Length == 0) return NtStatus.Success;
        try
        {
            ReportActivity();
            var requested = (int)Math.Min(buffer.Length, node.Length - offset);
            var plaintext = ReadAsync(node.Path, offset, requested).GetAwaiter().GetResult();
            try
            {
                Buffer.BlockCopy(plaintext, 0, buffer, 0, plaintext.Length);
                bytesRead = plaintext.Length;
                return NtStatus.Success;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (FileNotFoundException) { return NtStatus.ObjectNameNotFound; }
        catch (InvalidDataException) { return NtStatus.DataError; }
        catch (IOException) { return NtStatus.Unsuccessful; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset,
        IDokanFileInfo info)
    {
        bytesWritten = 0;
        return NtStatus.AccessDenied;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        if (!TryGetNode(fileName, out var node))
        {
            fileInfo = new FileInformation();
            return NtStatus.ObjectNameNotFound;
        }
        fileInfo = ToFileInformation(node);
        return NtStatus.Success;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info) =>
        FindFilesCore(fileName, null, out files);

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern,
        out IList<FileInformation> files, IDokanFileInfo info) =>
        FindFilesCore(fileName, searchPattern, out files);

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info) =>
        NtStatus.AccessDenied;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info) => NtStatus.AccessDenied;

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info) => NtStatus.AccessDenied;
    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info) => NtStatus.AccessDenied;
    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info) => NtStatus.AccessDenied;
    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info) => NtStatus.AccessDenied;
    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) => NtStatus.AccessDenied;
    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;
    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        totalNumberOfBytes = Math.Max(1024 * 1024, _nodes.Values.Where(node => !node.IsDirectory).Sum(node => node.Length));
        freeBytesAvailable = 0;
        totalNumberOfFreeBytes = 0;
        return NtStatus.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = SanitizeVolumeLabel(_opened.Vault.Name);
        fileSystemName = "PAVLT003";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk
            | FileSystemFeatures.ReadOnlyVolume;
        return NtStatus.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        security = null!;
        return NtStatus.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info) => NtStatus.AccessDenied;

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => NtStatus.Success;
    public NtStatus Unmounted(IDokanFileInfo info) => NtStatus.Success;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return NtStatus.NotImplemented;
    }

    private NtStatus FindFilesCore(string fileName, string? searchPattern, out IList<FileInformation> files)
    {
        files = Array.Empty<FileInformation>();
        if (!TryGetNode(fileName, out var directory)) return NtStatus.ObjectPathNotFound;
        if (!directory.IsDirectory) return NtStatus.NotADirectory;
        var children = GetChildren(directory.Path);
        files = children
            .Where(node => string.IsNullOrWhiteSpace(searchPattern)
                || FileSystemName.MatchesWin32Expression(searchPattern, node.Name, ignoreCase: true))
            .Select(ToFileInformation)
            .ToArray();
        return NtStatus.Success;
    }

    private void ReportActivity()
    {
        try { _activityObserved?.Invoke(); }
        catch { /* Un aviso de actividad nunca debe interrumpir Dokany. */ }
    }

    private FileInformation ToFileInformation(Node node) => new()
    {
        FileName = node.Name,
        Attributes = node.IsDirectory
            ? FileAttributes.Directory | FileAttributes.ReadOnly
            : FileAttributes.ReadOnly,
        CreationTime = node.CreationUtc,
        LastAccessTime = node.LastWriteUtc,
        LastWriteTime = node.LastWriteUtc,
        Length = node.IsDirectory ? 0 : node.Length
    };

    private void EnsureParentDirectories(string path)
    {
        var parent = GetParent(path);
        while (parent.Length > 0)
        {
            if (!_nodes.ContainsKey(parent))
                _nodes[parent] = new Node(parent, GetName(parent), true, 0,
                    _defaultCreatedUtc, _defaultModifiedUtc);
            parent = GetParent(parent);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "\\" or "/") return string.Empty;
        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.IndexOf(':') >= 0))
            return "\0invalid";
        return string.Join('/', parts);
    }

    private static string GetParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string GetName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static DateTime NormalizeDate(DateTime value, DateTime fallback) =>
        value == default ? fallback : value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static string SanitizeVolumeLabel(string name)
    {
        var cleaned = new string(name.Where(character => !"\\/:*?\"<>|".Contains(character)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "ProtectedApp" : cleaned[..Math.Min(cleaned.Length, 32)];
    }

    internal sealed record Node(string Path, string Name, bool IsDirectory, long Length,
        DateTime CreationUtc, DateTime LastWriteUtc);
}
