using System.Security.Cryptography;
using System.Text.Json;

namespace ProtectedApp.Service;

/// <summary>
/// The interactive agent is outside Guardian's protected ProgramData tree.
/// Record its signed, authorized installation hash when Guardian is installed
/// and require that identity for every privileged IPC request.
/// </summary>
internal static class GuardianAgentIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public static bool Matches(string path)
    {
        try
        {
            var expected = JsonSerializer.Deserialize<AgentIdentity>(File.ReadAllText(GuardianConstants.AgentIdentityPath),
                JsonOptions);
            var fullPath = Path.GetFullPath(path);
            if (expected is null || expected.Version != 1 || string.IsNullOrWhiteSpace(expected.Path)
                || string.IsNullOrWhiteSpace(expected.Sha256) || expected.Sha256.Length != 64
                || !string.Equals(fullPath, Path.GetFullPath(expected.Path), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath)) return false;

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record AgentIdentity(int Version, string Path, string Sha256, string SignerThumbprint);
}
