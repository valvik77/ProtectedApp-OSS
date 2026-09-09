using System.Security.Cryptography;

namespace ProtectedApp.Services;

public static class PasswordService
{
    public const int MinimumPasswordLength = 12;
    private const int LegacyIterations = 210_000;
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string SaltPrefix = "pbkdf2-sha256:600000:";

    public static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (Convert.ToBase64String(hash), SaltPrefix + Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? expectedHash, string? salt)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) || string.IsNullOrWhiteSpace(salt)) return false;
        try
        {
            var expected = Convert.FromBase64String(expectedHash);
            var (iterations, decodedSalt) = DecodeSalt(salt);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, decodedSalt, iterations, HashAlgorithmName.SHA256, expected.Length);
            try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(decodedSalt);
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool NeedsRehash(string? salt) => !string.IsNullOrWhiteSpace(salt)
        && !salt.StartsWith(SaltPrefix, StringComparison.Ordinal);

    private static (int Iterations, byte[] Salt) DecodeSalt(string salt)
    {
        if (salt.StartsWith(SaltPrefix, StringComparison.Ordinal))
            return (Iterations, Convert.FromBase64String(salt[SaltPrefix.Length..]));
        return (LegacyIterations, Convert.FromBase64String(salt));
    }
}
