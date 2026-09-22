namespace ProtectedApp.Models;

/// <summary>
/// A structurally plausible but uncommitted PAVLT container left behind by an
/// interrupted atomic replacement. It is never restored automatically.
/// </summary>
public sealed record VaultWriteRecoveryCandidate(string TargetPath, string TemporaryPath,
    long SizeBytes, DateTime LastWriteUtc)
{
    public string FileName => Path.GetFileName(TemporaryPath);
}
