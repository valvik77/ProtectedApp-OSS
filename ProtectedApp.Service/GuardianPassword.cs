using System.Security.Cryptography;

namespace ProtectedApp.Service;

internal static class GuardianPassword
{
    private const int LegacyIterations = 210_000;
    private const int Iterations = 600_000;
    private const string SaltPrefix = "pbkdf2-sha256:600000:";

    public static bool Verify(string? password, string? expectedHash, string? salt)
    {
        if (password is null || string.IsNullOrWhiteSpace(expectedHash) || string.IsNullOrWhiteSpace(salt))
            return false;
        try
        {
            var expected = Convert.FromBase64String(expectedHash);
            var (iterations, decodedSalt) = DecodeSalt(salt);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, decodedSalt,
                iterations, HashAlgorithmName.SHA256, expected.Length);
            try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(decodedSalt);
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        catch (FormatException) { return false; }
    }

    private static (int Iterations, byte[] Salt) DecodeSalt(string salt) =>
        salt.StartsWith(SaltPrefix, StringComparison.Ordinal)
            ? (Iterations, Convert.FromBase64String(salt[SaltPrefix.Length..]))
            : (LegacyIterations, Convert.FromBase64String(salt));
}
