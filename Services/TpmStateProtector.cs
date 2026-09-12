using System.Security.Cryptography;

namespace ProtectedApp.Services;

/// <summary>
/// Keeps the data-encryption key for the local application state in the Windows
/// platform cryptographic provider. The private key is marked non-exportable,
/// so copying state.dat alone is not sufficient to read the state elsewhere.
/// </summary>
internal static class TpmStateProtector
{
    internal const string EnvelopeFormat = "ProtectedApp.TpmState";
    private static readonly CngProvider PlatformProvider = CngProvider.MicrosoftPlatformCryptoProvider;

    internal static string CreateKey()
    {
        var keyName = "ProtectedApp.State." + Guid.NewGuid().ToString("N");
        var parameters = new CngKeyCreationParameters
        {
            Provider = PlatformProvider,
            KeyUsage = CngKeyUsages.Decryption,
            ExportPolicy = CngExportPolicies.None
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));

        try
        {
            using var key = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
            return keyName;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new TpmStateProtectionUnavailableException(
                "Windows no pudo crear una clave no exportable en el TPM de este equipo.", ex);
        }
    }

    internal static byte[] WrapDataKey(string keyName, byte[] dataKey)
    {
        using var key = OpenKey(keyName);
        using var rsa = new RSACng(key);
        return rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
    }

    internal static byte[] UnwrapDataKey(string keyName, byte[] wrappedDataKey)
    {
        using var key = OpenKey(keyName);
        using var rsa = new RSACng(key);
        return rsa.Decrypt(wrappedDataKey, RSAEncryptionPadding.OaepSHA256);
    }

    internal static void DeleteKey(string keyName)
    {
        try
        {
            using var key = CngKey.Open(keyName, PlatformProvider);
            key.Delete();
        }
        catch (CryptographicException)
        {
            // The local state has already been converted to DPAPI. A missing
            // TPM key cannot weaken it, and retaining a stale name is harmless.
        }
    }

    private static CngKey OpenKey(string keyName)
    {
        try
        {
            return CngKey.Open(keyName, PlatformProvider);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new TpmStateProtectionUnavailableException(
                "No se pudo abrir la clave TPM que protege la configuración local. " +
                "El TPM pudo haberse restablecido, Windows pudo haberse reinstalado o el equipo pudo haber cambiado.", ex);
        }
    }
}

/// <summary>Thrown when a TPM-bound state cannot be opened. Callers must fail closed and preserve the file.</summary>
public sealed class TpmStateProtectionUnavailableException : CryptographicException
{
    public TpmStateProtectionUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
