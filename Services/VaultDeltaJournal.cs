using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProtectedApp.Services;

internal static class VaultDeltaJournal
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PAVJ001");
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaximumCiphertextBytes = 256 * 1024 * 1024;
    internal const int MaximumEstimatedPlaintextBytes = 160 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };

    internal static async Task WriteAsync(string path, VaultFormatV3.OpenedVault opened,
        VaultJournalSnapshot snapshot)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (plaintext.Length is <= 0 or > MaximumCiphertextBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new VaultDeltaJournalTooLargeException();
        }
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        var temporary = path + ".next";
        try
        {
            using (var aes = new AesGcm(opened.DataKey, TagSize))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAad(opened.Vault.Id));
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Magic);
                await stream.WriteAsync(new byte[] { Version });
                await stream.WriteAsync(opened.Vault.Id.ToByteArray());
                await stream.WriteAsync(nonce);
                await stream.WriteAsync(tag);
                await stream.WriteAsync(BitConverter.GetBytes(ciphertext.Length));
                await stream.WriteAsync(ciphertext);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal static async Task<VaultJournalSnapshot> ReadAsync(string path, VaultFormatV3.OpenedVault opened)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[Magic.Length + 1 + 16 + NonceSize + TagSize + sizeof(int)];
        await ReadExactAsync(stream, header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || header[Magic.Length] != Version)
            throw new InvalidDataException("El diario de recuperación no tiene un formato compatible.");
        var idOffset = Magic.Length + 1;
        var vaultId = new Guid(header.AsSpan(idOffset, 16));
        if (vaultId != opened.Vault.Id) throw new InvalidDataException("El diario pertenece a otra bóveda.");
        var nonceOffset = idOffset + 16;
        var nonce = header.AsSpan(nonceOffset, NonceSize).ToArray();
        var tag = header.AsSpan(nonceOffset + NonceSize, TagSize).ToArray();
        var length = BitConverter.ToInt32(header, nonceOffset + NonceSize + TagSize);
        if (length is <= 0 or > MaximumCiphertextBytes) throw new InvalidDataException("El diario tiene un tamaño no válido.");
        if (stream.Length != header.Length + (long)length) throw new InvalidDataException("El diario está truncado o contiene datos adicionales.");
        var ciphertext = new byte[length];
        var plaintext = new byte[length];
        try
        {
            await ReadExactAsync(stream, ciphertext);
            using (var aes = new AesGcm(opened.DataKey, TagSize))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildAad(vaultId));
            return JsonSerializer.Deserialize<VaultJournalSnapshot>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("El diario no contiene datos válidos.");
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("El diario de recuperación ha sido modificado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] BuildAad(Guid vaultId) => [.. Magic, Version, .. vaultId.ToByteArray()];

    private static async Task ReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) throw new EndOfStreamException("El diario está truncado.");
            offset += read;
        }
    }
}

internal sealed class VaultDeltaJournalTooLargeException()
    : IOException("Los cambios superan el tamaño admitido por el diario diferencial.");
