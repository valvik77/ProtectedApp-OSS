namespace ProtectedApp.Services;

/// <summary>
/// A file retained inside a vault's recycle area, recoverable until the user
/// restores it, discards it, or empties the bin.
/// </summary>
/// <param name="Id">
/// Its path inside the recycle area. Opaque to callers; pass it back to
/// restore or discard the entry.
/// </param>
/// <param name="OriginalPath">The vault path it was deleted from.</param>
/// <param name="Length">Its size in bytes.</param>
/// <param name="LastWriteUtc">When its contents were last modified.</param>
/// <param name="DeletedUtc">When it was deleted, or <see cref="DateTime.MinValue"/> if unknown.</param>
public sealed record VaultRecycledEntry(string Id, string OriginalPath, long Length,
    DateTime LastWriteUtc, DateTime DeletedUtc)
{
    /// <summary>The file name alone, for a compact listing.</summary>
    public string Name
    {
        get
        {
            var separator = OriginalPath.LastIndexOf('/');
            return separator < 0 ? OriginalPath : OriginalPath[(separator + 1)..];
        }
    }

    /// <summary>The folder it was deleted from, or an empty string at the root.</summary>
    public string Folder
    {
        get
        {
            var separator = OriginalPath.LastIndexOf('/');
            return separator < 0 ? string.Empty : OriginalPath[..separator];
        }
    }

    /// <summary>When it was deleted and how large it is, for the listing.</summary>
    public string DeletedLabel
    {
        get
        {
            var size = FormatSize(Length);
            if (DeletedUtc == DateTime.MinValue) return size;
            return $"{DeletedUtc.ToLocalTime():g} · {size}";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB"
    };
}
