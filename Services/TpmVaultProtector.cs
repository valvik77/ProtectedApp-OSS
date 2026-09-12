using System.Security.Cryptography;

namespace ProtectedApp.Services;

/// <summary>TPM-resident material used as a second, local factor for a vault.</summary>
internal static class TpmVaultProtector
{
    private static readonly CngProvider PlatformProvider = CngProvider.MicrosoftPlatformCryptoProvider;

    internal static VaultTpmBinding CreateBinding(Guid vaultId)
    {
        var keyName = $"ProtectedApp.Vault.{vaultId:N}.{Guid.NewGuid():N}";
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
            var secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                using var rsa = new RSACng(key);
                return new VaultTpmBinding(keyName, rsa.Encrypt(secret, RSAEncryptionPadding.OaepSHA256), secret);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(secret);
                try { key.Delete(); } catch { }
                throw;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new TpmVaultProtectionUnavailableException(
                "Windows no pudo crear una clave no exportable en el TPM para esta bóveda.", ex);
        }
    }

    internal static byte[] UnwrapSecret(string keyName, byte[] wrappedSecret)
    {
        try
        {
            using var key = CngKey.Open(keyName, PlatformProvider);
            using var rsa = new RSACng(key);
            return rsa.Decrypt(wrappedSecret, RSAEncryptionPadding.OaepSHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            throw new TpmVaultProtectionUnavailableException(
                "No se pudo abrir la clave TPM de esta bóveda. El TPM pudo haberse restablecido, Windows pudo haberse reinstalado o el equipo pudo haber cambiado.", ex);
        }
    }

    internal static void DeleteKey(string keyName)
    {
        try
        {
            using var key = CngKey.Open(keyName, PlatformProvider);
            key.Delete();
        }
        catch (CryptographicException) { }
    }
}

internal sealed class VaultTpmBinding(string keyName, byte[] wrappedSecret, byte[]? secret) : IDisposable
{
    public string KeyName { get; } = keyName;
    public byte[] WrappedSecret { get; } = wrappedSecret;
    public byte[]? Secret { get; private set; } = secret;

    public VaultTpmBinding Clone() => new(KeyName, WrappedSecret.ToArray(), Secret?.ToArray());

    public void Dispose()
    {
        if (Secret is not null) CryptographicOperations.ZeroMemory(Secret);
        Secret = null;
    }
}

/// <summary>TPM factor for a vault is absent; the vault file is deliberately not modified.</summary>
public sealed class TpmVaultProtectionUnavailableException : CryptographicException
{
    public TpmVaultProtectionUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
