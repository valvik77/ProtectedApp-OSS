using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

/// <summary>
/// PAVLT003/PAVLT004: índice cifrado y autenticado y contenido dividido en
/// bloques AEAD de acceso aleatorio. PAVLT004 puede exigir además el TPM local.
/// </summary>
internal static class VaultFormatV3
{
    private static readonly byte[] MagicV3 = Encoding.ASCII.GetBytes("PAVLT003");
    private static readonly byte[] MagicV4 = Encoding.ASCII.GetBytes("PAVLT004");
    private const byte VersionV3 = 3;
    private const byte VersionV4 = 4;
    // Inner AEAD records intentionally retain the PAVLT003 domain separator;
    // PAVLT004 only changes how the random data key is unlocked.
    private static readonly byte[] Magic = MagicV3;
    private const byte Version = VersionV3;
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int ChunkSize = 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const int MaximumChunks = 100_000;
    private const int MaximumIndexBytes = 16 * 1024 * 1024;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const long MaximumDataBytes = MaximumExpandedBytes + 64L * 1024 * 1024;
    private const int HeaderSizeV3 = 8 + 1 + sizeof(int) + SaltSize + NonceSize + TagSize + KeySize
        + sizeof(long) + sizeof(long);
    private const int TpmKeyNameCapacity = 128;
    private const int TpmWrappedSecretCapacity = 512;
    private const int HeaderSizeV4 = HeaderSizeV3 + sizeof(byte) + sizeof(byte) + TpmKeyNameCapacity
        + sizeof(ushort) + TpmWrappedSecretCapacity;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 64
    };

    public static bool IsFormat(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, MagicV3.Length + 1, FileOptions.SequentialScan);
            Span<byte> prefix = stackalloc byte[MagicV3.Length + 1];
            return stream.Read(prefix) == prefix.Length
                && ((prefix[..MagicV3.Length].SequenceEqual(MagicV3) && prefix[^1] == VersionV3)
                    || (prefix[..MagicV4.Length].SequenceEqual(MagicV4) && prefix[^1] == VersionV4));
        }
        catch { return false; }
    }

    public static bool HasStructurallyValidEnvelope(string path)
    {
        try
        {
            var header = ReadHeaderAsync(path).GetAwaiter().GetResult();
            return header.DataLength >= 0 && header.DataLength <= MaximumDataBytes
                && header.IndexLength > 0 && header.IndexLength <= MaximumIndexBytes;
        }
        catch { return false; }
    }

    public static async Task<OpenedVault> OpenAsync(string path, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new InvalidDataException("No se indicó la contraseña de la bóveda.");
        var passwordKey = Array.Empty<byte>();
        try
        {
            var header = await ReadHeaderAsync(path);
            passwordKey = DerivePasswordKey(password, header.Salt);
            return await OpenWithPasswordKeyAsync(path, header, passwordKey);
        }
        catch
        {
            if (passwordKey.Length > 0) CryptographicOperations.ZeroMemory(passwordKey);
            throw;
        }
    }

    public static async Task WriteNewAsync(VaultContainer vault, string path, string password,
        string? sourceDirectory = null, bool createRecoveryBackup = true)
    {
        if (string.IsNullOrEmpty(password) || password.Length < PasswordService.MinimumPasswordLength)
            throw new InvalidDataException("La contraseña debe tener al menos 12 caracteres.");
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var passwordKey = DerivePasswordKey(password, salt);
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            await WriteFromDirectoryAsync(vault, sourceDirectory, path, passwordKey, salt, dataKey,
                createRecoveryBackup: createRecoveryBackup);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static async Task WriteFromDirectoryAsync(VaultContainer vault, string? sourceDirectory, string path,
        byte[] passwordKey, byte[] salt, byte[]? existingDataKey, VaultTpmBinding? tpmBinding = null,
        bool createRecoveryBackup = true)
    {
        ValidateKeyMaterial(passwordKey, salt);
        ValidateTpmBinding(tpmBinding);
        var dataKey = existingDataKey?.ToArray() ?? RandomNumberGenerator.GetBytes(KeySize);
        var wrapKey = GetWrapKey(passwordKey, tpmBinding);
        var headerSize = GetHeaderSize(tpmBinding);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("La ruta de la bóveda no es válida.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.v3tmp");
        var index = new IndexDocument
        {
            Vault = IndexManifest.From(vault)
        };
        byte[]? indexPlaintext = null;
        byte[]? indexCiphertext = null;
        try
        {
            long expandedBytes = 0;
            var chunkCount = 0;
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                output.Position = headerSize;
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    var root = Path.GetFullPath(sourceDirectory);
                    if (!Directory.Exists(root)) throw new DirectoryNotFoundException("La carpeta de trabajo no existe.");
                    var sourcePaths = EnumerateSafePaths(root);
                    if (sourcePaths.Count > MaximumEntries)
                        throw new InvalidDataException("La bóveda contiene demasiados elementos.");
                    foreach (var sourcePath in sourcePaths)
                    {
                        var relative = NormalizeRelativePath(Path.GetRelativePath(root, sourcePath));
                        var isDirectory = Directory.Exists(sourcePath);
                        var entry = new IndexEntry
                        {
                            Path = relative,
                            IsDirectory = isDirectory,
                            Length = isDirectory ? 0 : new FileInfo(sourcePath).Length,
                            CreationUtc = isDirectory
                                ? new DirectoryInfo(sourcePath).CreationTimeUtc
                                : new FileInfo(sourcePath).CreationTimeUtc,
                            LastWriteUtc = isDirectory
                                ? new DirectoryInfo(sourcePath).LastWriteTimeUtc
                                : new FileInfo(sourcePath).LastWriteTimeUtc
                        };
                        var entryIndex = index.Entries.Count;
                        index.Entries.Add(entry);
                        if (isDirectory) continue;
                        expandedBytes = checked(expandedBytes + entry.Length);
                        if (expandedBytes > MaximumExpandedBytes)
                            throw new InvalidDataException("El contenido de la bóveda supera 1 GB.");
                        await EncryptFileAsync(vault.Id, entryIndex, sourcePath, entry, output, dataKey, headerSize,
                            () => ++chunkCount > MaximumChunks);
                    }
                }

                var dataLength = output.Position - headerSize;
                if (dataLength < 0 || dataLength > MaximumDataBytes)
                    throw new InvalidDataException("Los bloques cifrados superan el límite admitido.");
                indexPlaintext = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
                if (indexPlaintext.Length <= 0 || indexPlaintext.Length > MaximumIndexBytes)
                    throw new InvalidDataException("El índice de la bóveda es demasiado grande.");

                var indexLength = (long)indexPlaintext.Length;
                var keyNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var keyTag = new byte[TagSize];
                var wrappedDataKey = new byte[KeySize];
                using (var aes = new AesGcm(wrapKey, TagSize))
                    aes.Encrypt(keyNonce, dataKey, wrappedDataKey, keyTag, BuildWrapAad(salt));
                var header = BuildHeader(salt, keyNonce, keyTag, wrappedDataKey, dataLength, indexLength, tpmBinding);

                var indexNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var indexTag = new byte[TagSize];
                indexCiphertext = new byte[indexPlaintext.Length];
                using (var aes = new AesGcm(dataKey, TagSize))
                    aes.Encrypt(indexNonce, indexPlaintext, indexCiphertext, indexTag,
                        BuildIndexAad(dataLength, indexLength));
                output.Position = headerSize + dataLength;
                await output.WriteAsync(indexNonce);
                await output.WriteAsync(indexTag);
                await output.WriteAsync(indexCiphertext);
                output.Position = 0;
                await output.WriteAsync(header);
                await output.FlushAsync();
                output.Flush(flushToDisk: true);
            }

            using (var verification = await OpenWithPasswordKeyAsync(temporaryPath,
                       await ReadHeaderAsync(temporaryPath), passwordKey))
            {
                if (verification.Vault.Id != vault.Id)
                    throw new InvalidDataException("La verificación del contenedor nuevo devolvió otra bóveda.");
            }
            ReplaceAtomically(temporaryPath, path, createRecoveryBackup, preservePortableTpmCopy: tpmBinding is not null);
            DeleteStaleWriteTemporaries(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(wrapKey);
            if (indexPlaintext is not null) CryptographicOperations.ZeroMemory(indexPlaintext);
            if (indexCiphertext is not null) CryptographicOperations.ZeroMemory(indexCiphertext);
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public static async Task WriteFromVirtualEntriesAsync(VaultContainer vault,
        IReadOnlyList<VirtualEntrySource> sources, string path, byte[] passwordKey, byte[] salt,
        byte[] existingDataKey, VaultTpmBinding? tpmBinding = null, bool createRecoveryBackup = true)
    {
        ValidateKeyMaterial(passwordKey, salt);
        ValidateTpmBinding(tpmBinding);
        ArgumentNullException.ThrowIfNull(existingDataKey);
        if (existingDataKey.Length != KeySize) throw new InvalidDataException("La clave de datos no es válida.");
        var validatedSources = ValidateVirtualSources(sources);

        var dataKey = existingDataKey.ToArray();
        var wrapKey = GetWrapKey(passwordKey, tpmBinding);
        var headerSize = GetHeaderSize(tpmBinding);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("La ruta de la bóveda no es válida.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.v3tmp");
        var index = new IndexDocument { Vault = IndexManifest.From(vault) };
        byte[]? indexPlaintext = null;
        byte[]? indexCiphertext = null;
        try
        {
            long expandedBytes = 0;
            var chunkCount = 0;
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                output.Position = headerSize;
                foreach (var (source, relative) in validatedSources)
                {
                    var entry = new IndexEntry
                    {
                        Path = relative,
                        IsDirectory = source.IsDirectory,
                        Length = source.IsDirectory ? 0 : source.Length,
                        CreationUtc = source.CreationUtc,
                        LastWriteUtc = source.LastWriteUtc
                    };
                    var entryIndex = index.Entries.Count;
                    index.Entries.Add(entry);
                    if (source.IsDirectory) continue;
                    expandedBytes = checked(expandedBytes + source.Length);
                    if (expandedBytes > MaximumExpandedBytes)
                        throw new InvalidDataException("El contenido de la bóveda supera 1 GB.");
                    await using var input = await source.OpenReadAsync();
                    if (!input.CanRead) throw new InvalidDataException("No se puede leer un archivo virtual.");
                    await EncryptStreamAsync(vault.Id, entryIndex, input, entry, output, dataKey, headerSize,
                        () => ++chunkCount > MaximumChunks);
                }

                var dataLength = output.Position - headerSize;
                if (dataLength < 0 || dataLength > MaximumDataBytes)
                    throw new InvalidDataException("Los bloques cifrados superan el límite admitido.");
                indexPlaintext = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
                if (indexPlaintext.Length <= 0 || indexPlaintext.Length > MaximumIndexBytes)
                    throw new InvalidDataException("El índice de la bóveda es demasiado grande.");

                var indexLength = (long)indexPlaintext.Length;
                var keyNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var keyTag = new byte[TagSize];
                var wrappedDataKey = new byte[KeySize];
                using (var aes = new AesGcm(wrapKey, TagSize))
                    aes.Encrypt(keyNonce, dataKey, wrappedDataKey, keyTag, BuildWrapAad(salt));
                var header = BuildHeader(salt, keyNonce, keyTag, wrappedDataKey, dataLength, indexLength, tpmBinding);

                var indexNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var indexTag = new byte[TagSize];
                indexCiphertext = new byte[indexPlaintext.Length];
                using (var aes = new AesGcm(dataKey, TagSize))
                    aes.Encrypt(indexNonce, indexPlaintext, indexCiphertext, indexTag,
                        BuildIndexAad(dataLength, indexLength));
                await output.WriteAsync(indexNonce);
                await output.WriteAsync(indexTag);
                await output.WriteAsync(indexCiphertext);
                output.Position = 0;
                await output.WriteAsync(header);
                await output.FlushAsync();
                output.Flush(flushToDisk: true);
            }

            using (var verification = await OpenWithPasswordKeyAsync(temporaryPath,
                       await ReadHeaderAsync(temporaryPath), passwordKey))
            {
                if (verification.Vault.Id != vault.Id)
                    throw new InvalidDataException("La verificación del contenedor nuevo devolvió otra bóveda.");
            }
            ReplaceAtomically(temporaryPath, path, createRecoveryBackup, preservePortableTpmCopy: tpmBinding is not null);
            DeleteStaleWriteTemporaries(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(wrapKey);
            if (indexPlaintext is not null) CryptographicOperations.ZeroMemory(indexPlaintext);
            if (indexCiphertext is not null) CryptographicOperations.ZeroMemory(indexCiphertext);
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public static async Task ExtractAsync(OpenedVault opened, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        for (var entryIndex = 0; entryIndex < opened.Index.Entries.Count; entryIndex++)
        {
            var entry = opened.Index.Entries[entryIndex];
            var target = ResolveExtractionPath(root, entry.Path);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            for (var chunkIndex = 0; chunkIndex < entry.Chunks.Count; chunkIndex++)
            {
                var plaintext = await ReadChunkAsync(opened, entryIndex, chunkIndex);
                try { await output.WriteAsync(plaintext); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            await output.FlushAsync();
        }
    }

    public static async Task<byte[]> ReadFileRangeAsync(OpenedVault opened, string relativePath, long offset,
        int count)
    {
        if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException();
        var normalized = NormalizeRelativePath(relativePath);
        if (!opened.TryGetEntryIndex(normalized, out var entryIndex)
            || opened.Index.Entries[entryIndex].IsDirectory)
            throw new FileNotFoundException("El archivo no existe dentro de la bóveda.", relativePath);
        var entry = opened.Index.Entries[entryIndex];
        if (offset > entry.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var result = new byte[(int)Math.Min(count, entry.Length - offset)];
        _ = await ReadFileRangeIntoAsync(opened, entryIndex, offset, result).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Reconstructs an encrypted file range directly into a caller-owned
    /// buffer. Dokany already provides that buffer for normal Explorer reads;
    /// avoiding an intermediate array is particularly important for repeated
    /// 64 KiB reads while a vault is open for editing.
    /// </summary>
    internal static async Task<int> ReadFileRangeIntoAsync(OpenedVault opened, string relativePath, long offset,
        Memory<byte> destination, bool cacheChunks = true)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var normalized = NormalizeRelativePath(relativePath);
        if (!opened.TryGetEntryIndex(normalized, out var entryIndex)
            || opened.Index.Entries[entryIndex].IsDirectory)
            throw new FileNotFoundException("El archivo no existe dentro de la bóveda.", relativePath);
        var entry = opened.Index.Entries[entryIndex];
        if (offset > entry.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return await ReadFileRangeIntoAsync(opened, entryIndex, offset,
            destination[..(int)Math.Min(destination.Length, entry.Length - offset)], cacheChunks).ConfigureAwait(false);
    }

    private static async Task<int> ReadFileRangeIntoAsync(OpenedVault opened, int entryIndex, long offset,
        Memory<byte> destination, bool cacheChunks = true)
    {
        var entry = opened.Index.Entries[entryIndex];
        if (destination.Length == 0) return 0;
        var requestedEnd = offset + destination.Length;
        var written = 0;
        for (var chunkIndex = FindFirstIntersectingChunk(entry.Chunks, offset);
             chunkIndex < entry.Chunks.Count; chunkIndex++)
        {
            var chunk = entry.Chunks[chunkIndex];
            if (chunk.PlainOffset >= requestedEnd) break;
            var from = (int)Math.Max(0, offset - chunk.PlainOffset);
            var to = (int)Math.Min(chunk.PlainLength, requestedEnd - chunk.PlainOffset);
            var length = to - from;
            if (opened.TryCopyCachedChunkRange(entryIndex, chunkIndex, from,
                    destination.Slice(written, length)))
            {
                written += length;
                continue;
            }

            var plaintext = await ReadChunkAsync(opened, entryIndex, chunkIndex, cacheChunks).ConfigureAwait(false);
            try
            {
                plaintext.AsMemory(from, length).CopyTo(destination.Slice(written, length));
                written += length;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        if (written != destination.Length) throw new InvalidDataException("No se pudo reconstruir el intervalo solicitado.");
        return written;
    }

    internal static async Task<(int FileCount, int ChunkCount, long VerifiedBytes)> VerifyIntegrityAsync(
        OpenedVault opened)
    {
        var fileCount = 0;
        var chunkCount = 0;
        long verifiedBytes = 0;
        for (var entryIndex = 0; entryIndex < opened.Index.Entries.Count; entryIndex++)
        {
            var entry = opened.Index.Entries[entryIndex];
            if (entry.IsDirectory) continue;
            fileCount++;
            for (var chunkIndex = 0; chunkIndex < entry.Chunks.Count; chunkIndex++)
            {
                var plaintext = await ReadChunkAsync(opened, entryIndex, chunkIndex);
                try
                {
                    verifiedBytes = checked(verifiedBytes + plaintext.Length);
                    chunkCount++;
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
        }
        return (fileCount, chunkCount, verifiedBytes);
    }

    internal static async Task RunVirtualWriteProbeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "probe.pavault");
        var vault = new VaultContainer { Name = "Probe", AutoLockMinutes = 5 };
        const string password = "ProtectedApp-Probe-Only";
        try
        {
            await WriteNewAsync(vault, path, password, createRecoveryBackup: false);
            using (var opened = await OpenAsync(path, password))
            {
                var expected = Encoding.UTF8.GetBytes("virtual-write-ok");
                var sources = new VirtualEntrySource[]
                {
                    new("Documentos", true, 0, DateTime.UtcNow, DateTime.UtcNow,
                        () => Task.FromResult<Stream>(Stream.Null)),
                    new("Documentos/prueba.txt", false, expected.Length, DateTime.UtcNow, DateTime.UtcNow,
                        () => Task.FromResult<Stream>(new MemoryStream(expected, writable: false)))
                };
                await WriteFromVirtualEntriesAsync(vault, sources, path, opened.PasswordKey,
                    opened.Salt, opened.DataKey, opened.TpmBinding, createRecoveryBackup: false);
            }
            using var verification = await OpenAsync(path, password);
            var actual = await ReadFileRangeAsync(verification, "Documentos/prueba.txt", 0, 128);
            try
            {
                if (!Encoding.UTF8.GetString(actual).Equals("virtual-write-ok", StringComparison.Ordinal))
                    throw new InvalidDataException("La prueba de escritura virtual devolvió datos distintos.");
                var integrity = await VerifyIntegrityAsync(verification);
                if (integrity.FileCount != 1 || integrity.ChunkCount != 1
                    || integrity.VerifiedBytes != Encoding.UTF8.GetByteCount("virtual-write-ok"))
                    throw new InvalidDataException("La comprobación de integridad no validó el contenido esperado.");
            }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: false); } catch { }
        }
    }

    public static async Task RewriteMetadataAsync(OpenedVault opened, VaultContainer vault, string path,
        bool createRecoveryBackup = true)
    {
        opened.Index.Vault = IndexManifest.From(vault);
        await RewriteIndexAsync(opened, path, createRecoveryBackup);
    }

    public static async Task ChangePasswordAsync(string path, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < PasswordService.MinimumPasswordLength)
            throw new InvalidDataException("La contraseña debe tener al menos 12 caracteres.");
        using var opened = await OpenAsync(path, oldPassword);
        var newSalt = RandomNumberGenerator.GetBytes(SaltSize);
        var newPasswordKey = DerivePasswordKey(newPassword, newSalt);
        var newDataKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            using var view = new VaultReadWriteFileSystem(opened);
            await WriteFromVirtualEntriesAsync(opened.Vault, view.CreateSnapshot(), path,
                newPasswordKey, newSalt, newDataKey, opened.TpmBinding, createRecoveryBackup: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newDataKey);
            CryptographicOperations.ZeroMemory(newSalt);
            CryptographicOperations.ZeroMemory(newPasswordKey);
        }
    }

    private static async Task<OpenedVault> OpenWithPasswordKeyAsync(string path, Header header, byte[] passwordKey)
    {
        byte[]? dataKey = null;
        byte[]? wrapKey = null;
        VaultTpmBinding? tpmBinding = null;
        byte[]? indexCiphertext = null;
        byte[]? indexPlaintext = null;
        try
        {
            tpmBinding = header.TpmBinding?.Clone();
            if (tpmBinding is not null)
            {
                var secret = TpmVaultProtector.UnwrapSecret(tpmBinding.KeyName, tpmBinding.WrappedSecret);
                if (secret.Length != KeySize) throw new InvalidDataException("La clave TPM de la bóveda no es válida.");
                tpmBinding.Dispose();
                tpmBinding = new VaultTpmBinding(header.TpmBinding!.KeyName, header.TpmBinding.WrappedSecret.ToArray(), secret);
            }
            wrapKey = GetWrapKey(passwordKey, tpmBinding);
            dataKey = new byte[KeySize];
            using (var aes = new AesGcm(wrapKey, TagSize))
                aes.Decrypt(header.KeyNonce, header.WrappedDataKey, header.KeyTag, dataKey,
                    BuildWrapAad(header.Salt));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            stream.Position = header.HeaderSize + header.DataLength;
            var indexNonce = await ReadExactAsync(stream, NonceSize);
            var indexTag = await ReadExactAsync(stream, TagSize);
            indexCiphertext = await ReadExactAsync(stream, checked((int)header.IndexLength));
            indexPlaintext = new byte[indexCiphertext.Length];
            using (var aes = new AesGcm(dataKey, TagSize))
                aes.Decrypt(indexNonce, indexCiphertext, indexTag, indexPlaintext,
                    BuildIndexAad(header.DataLength, header.IndexLength));
            var index = JsonSerializer.Deserialize<IndexDocument>(indexPlaintext, JsonOptions)
                ?? throw new InvalidDataException("No se pudo leer el índice PAVLT003.");
            ValidateIndex(index, header.DataLength);
            var result = new OpenedVault(path, header, index, passwordKey.ToArray(), dataKey, tpmBinding);
            dataKey = null;
            tpmBinding = null;
            return result;
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("La contraseña es incorrecta o la bóveda ha sido modificada.");
        }
        finally
        {
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            if (wrapKey is not null) CryptographicOperations.ZeroMemory(wrapKey);
            tpmBinding?.Dispose();
            if (indexCiphertext is not null) CryptographicOperations.ZeroMemory(indexCiphertext);
            if (indexPlaintext is not null) CryptographicOperations.ZeroMemory(indexPlaintext);
        }
    }

    private static async Task EncryptFileAsync(Guid vaultId, int entryIndex, string sourcePath, IndexEntry entry,
        FileStream output, byte[] dataKey, int headerSize, Func<bool> chunkLimitExceeded)
    {
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await EncryptStreamAsync(vaultId, entryIndex, input, entry, output, dataKey, headerSize, chunkLimitExceeded);
    }

    private static async Task EncryptStreamAsync(Guid vaultId, int entryIndex, Stream input, IndexEntry entry,
        FileStream output, byte[] dataKey, int headerSize, Func<bool> chunkLimitExceeded)
    {
        var buffer = new byte[ChunkSize];
        try
        {
            long plainOffset = 0;
            var chunkIndex = 0;
            while (true)
            {
                var read = await ReadUpToAsync(input, buffer);
                if (read == 0) break;
                if (chunkLimitExceeded()) throw new InvalidDataException("La bóveda contiene demasiados bloques.");
                var compressed = CompressIfUseful(buffer.AsSpan(0, read), out var prepared);
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var tag = new byte[TagSize];
                var ciphertext = new byte[prepared.Length];
                try
                {
                    using (var aes = new AesGcm(dataKey, TagSize))
                        aes.Encrypt(nonce, prepared, ciphertext, tag,
                            BuildChunkAad(vaultId, entryIndex, chunkIndex, plainOffset, read, compressed));
                    var offset = output.Position - headerSize;
                    await output.WriteAsync(ciphertext);
                    entry.Chunks.Add(new IndexChunk
                    {
                        Offset = offset,
                        CipherLength = ciphertext.Length,
                        PlainOffset = plainOffset,
                        PlainLength = read,
                        Compressed = compressed,
                        Nonce = nonce,
                        Tag = tag
                    });
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(prepared);
                    CryptographicOperations.ZeroMemory(ciphertext);
                }
                plainOffset += read;
                chunkIndex++;
            }
            if (plainOffset != entry.Length)
                throw new IOException("El archivo cambió mientras se estaba cifrando.");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task<byte[]> ReadChunkAsync(OpenedVault opened, int entryIndex, int chunkIndex,
        bool cachePlaintext = true)
    {
        if (opened.TryGetCachedChunk(entryIndex, chunkIndex, out var cached)) return cached;
        var entry = opened.Index.Entries[entryIndex];
        var chunk = entry.Chunks[chunkIndex];
        var ciphertext = new byte[chunk.CipherLength];
        var prepared = new byte[chunk.CipherLength];
        try
        {
            await ReadExactAtAsync(opened.ReadHandle, ciphertext,
                opened.Header.HeaderSize + chunk.Offset).ConfigureAwait(false);
            using (var aes = new AesGcm(opened.DataKey, TagSize))
                aes.Decrypt(chunk.Nonce, ciphertext, chunk.Tag, prepared,
                    BuildChunkAad(opened.Vault.Id, entryIndex, chunkIndex, chunk.PlainOffset,
                        chunk.PlainLength, chunk.Compressed));
            if (!chunk.Compressed)
            {
                if (prepared.Length != chunk.PlainLength)
                    throw new InvalidDataException("La longitud del bloque no es válida.");
                var result = prepared;
                prepared = Array.Empty<byte>();
                if (cachePlaintext) opened.CacheChunk(entryIndex, chunkIndex, result);
                return result;
            }
            var decompressed = DecompressExact(prepared, chunk.PlainLength);
            if (cachePlaintext) opened.CacheChunk(entryIndex, chunkIndex, decompressed);
            return decompressed;
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("Un bloque de la bóveda ha sido modificado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (prepared.Length > 0) CryptographicOperations.ZeroMemory(prepared);
        }
    }

    private static async Task RewriteIndexAsync(OpenedVault opened, string path, bool createRecoveryBackup)
    {
        var indexPlaintext = JsonSerializer.SerializeToUtf8Bytes(opened.Index, JsonOptions);
        if (indexPlaintext.Length <= 0 || indexPlaintext.Length > MaximumIndexBytes)
            throw new InvalidDataException("El índice de la bóveda es demasiado grande.");
        var indexLength = (long)indexPlaintext.Length;
        var keyNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var keyTag = new byte[TagSize];
        var wrapKey = GetWrapKey(opened.PasswordKey, opened.TpmBinding);
        var wrappedKey = new byte[KeySize];
        using (var aes = new AesGcm(wrapKey, TagSize))
            aes.Encrypt(keyNonce, opened.DataKey, wrappedKey, keyTag, BuildWrapAad(opened.Header.Salt));
        var headerBytes = BuildHeader(opened.Header.Salt, keyNonce, keyTag, wrappedKey,
            opened.Header.DataLength, indexLength, opened.TpmBinding);
        var indexNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var indexTag = new byte[TagSize];
        var indexCiphertext = new byte[indexPlaintext.Length];
        using (var aes = new AesGcm(opened.DataKey, TagSize))
            aes.Encrypt(indexNonce, indexPlaintext, indexCiphertext, indexTag,
                BuildIndexAad(opened.Header.DataLength, indexLength));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.index.tmp");
        try
        {
            await using (var source = new FileStream(opened.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await destination.WriteAsync(headerBytes);
                source.Position = opened.Header.HeaderSize;
                await CopyExactlyAsync(source, destination, opened.Header.DataLength);
                await destination.WriteAsync(indexNonce);
                await destination.WriteAsync(indexTag);
                await destination.WriteAsync(indexCiphertext);
                await destination.FlushAsync();
                destination.Flush(flushToDisk: true);
            }
            using (var verification = await OpenWithPasswordKeyAsync(temporaryPath,
                       await ReadHeaderAsync(temporaryPath), opened.PasswordKey))
            {
                if (verification.Vault.Id != opened.Vault.Id)
                    throw new InvalidDataException("La actualización del índice no superó la verificación.");
            }
            ReplaceAtomically(temporaryPath, path, createRecoveryBackup, preservePortableTpmCopy: opened.TpmBinding is not null);
            DeleteStaleWriteTemporaries(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
            CryptographicOperations.ZeroMemory(indexPlaintext);
            CryptographicOperations.ZeroMemory(indexCiphertext);
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static async Task<Header> ReadHeaderAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, HeaderSizeV4, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // This reader is also used by the synchronous envelope inspection
        // below. Do not capture a WinUI synchronization context here: doing
        // so would deadlock that inspection while a UI-thread probe waits for
        // the file read to complete.
        var prefix = await ReadExactAsync(stream, MagicV3.Length + 1).ConfigureAwait(false);
        var isV3 = prefix.AsSpan(0, MagicV3.Length).SequenceEqual(MagicV3) && prefix[^1] == VersionV3;
        var isV4 = prefix.AsSpan(0, MagicV4.Length).SequenceEqual(MagicV4) && prefix[^1] == VersionV4;
        if (!isV3 && !isV4) throw new InvalidDataException("El contenedor no usa un formato PAVLT compatible.");
        var headerSize = isV4 ? HeaderSizeV4 : HeaderSizeV3;
        var bytes = new byte[headerSize];
        prefix.CopyTo(bytes, 0);
        var remaining = await ReadExactAsync(stream, headerSize - prefix.Length).ConfigureAwait(false);
        remaining.CopyTo(bytes, prefix.Length);
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
        _ = reader.ReadBytes(MagicV3.Length);
        _ = reader.ReadByte();
        if (reader.ReadInt32() != Iterations)
            throw new InvalidDataException("La derivación de clave PAVLT003 no es compatible.");
        var salt = reader.ReadBytes(SaltSize);
        var keyNonce = reader.ReadBytes(NonceSize);
        var keyTag = reader.ReadBytes(TagSize);
        var wrappedKey = reader.ReadBytes(KeySize);
        var dataLength = reader.ReadInt64();
        var indexLength = reader.ReadInt64();
        VaultTpmBinding? tpmBinding = null;
        if (isV4)
        {
            var tpmEnabled = reader.ReadByte();
            var keyNameLength = reader.ReadByte();
            var keyNameBytes = reader.ReadBytes(TpmKeyNameCapacity);
            var wrappedSecretLength = reader.ReadUInt16();
            var wrappedSecretBytes = reader.ReadBytes(TpmWrappedSecretCapacity);
            if (tpmEnabled != 1 || keyNameLength == 0 || keyNameLength > TpmKeyNameCapacity
                || wrappedSecretLength == 0 || wrappedSecretLength > TpmWrappedSecretCapacity)
                throw new InvalidDataException("La cabecera TPM de la bóveda no es válida.");
            var keyName = Encoding.UTF8.GetString(keyNameBytes, 0, keyNameLength);
            tpmBinding = new VaultTpmBinding(keyName, wrappedSecretBytes[..wrappedSecretLength], null);
        }
        if (salt.Length != SaltSize || keyNonce.Length != NonceSize || keyTag.Length != TagSize
            || wrappedKey.Length != KeySize || dataLength < 0 || dataLength > MaximumDataBytes
            || indexLength <= 0 || indexLength > MaximumIndexBytes
            || stream.Length != headerSize + dataLength + NonceSize + TagSize + indexLength)
        {
            tpmBinding?.Dispose();
            throw new InvalidDataException("La cabecera de la bóveda no es válida.");
        }
        return new Header(salt, keyNonce, keyTag, wrappedKey, dataLength, indexLength, headerSize, tpmBinding);
    }

    private static byte[] BuildHeader(byte[] salt, byte[] keyNonce, byte[] keyTag, byte[] wrappedKey,
        long dataLength, long indexLength, VaultTpmBinding? tpmBinding = null)
    {
        var isTpmBound = tpmBinding is not null;
        ValidateTpmBinding(tpmBinding);
        var headerSize = GetHeaderSize(tpmBinding);
        using var stream = new MemoryStream(headerSize);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(isTpmBound ? MagicV4 : MagicV3);
        writer.Write(isTpmBound ? VersionV4 : VersionV3);
        writer.Write(Iterations);
        writer.Write(salt);
        writer.Write(keyNonce);
        writer.Write(keyTag);
        writer.Write(wrappedKey);
        writer.Write(dataLength);
        writer.Write(indexLength);
        if (isTpmBound)
        {
            var keyNameBytes = Encoding.UTF8.GetBytes(tpmBinding!.KeyName);
            if (keyNameBytes.Length is 0 or > TpmKeyNameCapacity
                || tpmBinding.WrappedSecret.Length is 0 or > TpmWrappedSecretCapacity)
                throw new InvalidDataException("La clave TPM de la bóveda no tiene un tamaño válido.");
            writer.Write((byte)1);
            writer.Write((byte)keyNameBytes.Length);
            writer.Write(keyNameBytes);
            writer.Write(new byte[TpmKeyNameCapacity - keyNameBytes.Length]);
            writer.Write((ushort)tpmBinding.WrappedSecret.Length);
            writer.Write(tpmBinding.WrappedSecret);
            writer.Write(new byte[TpmWrappedSecretCapacity - tpmBinding.WrappedSecret.Length]);
        }
        var result = stream.ToArray();
        if (result.Length != headerSize) throw new InvalidOperationException("La cabecera de la bóveda tiene un tamaño inesperado.");
        return result;
    }

    private static int GetHeaderSize(VaultTpmBinding? tpmBinding) => tpmBinding is null ? HeaderSizeV3 : HeaderSizeV4;

    private static void ValidateTpmBinding(VaultTpmBinding? binding)
    {
        if (binding is not null && (binding.Secret is null || binding.Secret.Length != KeySize))
            throw new InvalidDataException("La clave TPM de la bóveda no está disponible.");
    }

    private static byte[] GetWrapKey(byte[] passwordKey, VaultTpmBinding? binding)
    {
        ValidateTpmBinding(binding);
        return binding is null ? passwordKey.ToArray() : HMACSHA256.HashData(passwordKey, binding.Secret!);
    }

    private static byte[] BuildWrapAad(byte[] salt)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(Iterations);
        writer.Write(salt);
        return stream.ToArray();
    }

    private static byte[] BuildIndexAad(long dataLength, long indexLength)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(dataLength);
        writer.Write(indexLength);
        return stream.ToArray();
    }

    private static byte[] BuildChunkAad(Guid vaultId, int entryIndex, int chunkIndex, long plainOffset,
        int plainLength, bool compressed)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(vaultId.ToByteArray());
        writer.Write(entryIndex);
        writer.Write(chunkIndex);
        writer.Write(plainOffset);
        writer.Write(plainLength);
        writer.Write(compressed);
        return stream.ToArray();
    }

    private static void ValidateIndex(IndexDocument index, long dataLength)
    {
        if (index.Vault.Id == Guid.Empty || string.IsNullOrWhiteSpace(index.Vault.Name))
            throw new InvalidDataException("El índice no contiene metadatos de bóveda válidos.");
        if (index.Entries.Count > MaximumEntries) throw new InvalidDataException("El índice contiene demasiados elementos.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spans = new List<(long Start, long End)>();
        long expanded = 0;
        var chunks = 0;
        foreach (var entry in index.Entries)
        {
            entry.Path = NormalizeAndRegisterUniquePath(paths, entry.Path, "El índice contiene rutas duplicadas.");
            if (entry.Length < 0 || entry.IsDirectory && (entry.Length != 0 || entry.Chunks.Count != 0))
                throw new InvalidDataException("Una entrada del índice no es válida.");
            if (entry.IsDirectory) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException("El contenido expandido supera 1 GB.");
            long expectedPlainOffset = 0;
            foreach (var chunk in entry.Chunks)
            {
                if (++chunks > MaximumChunks || chunk.Nonce.Length != NonceSize || chunk.Tag.Length != TagSize
                    || chunk.Offset < 0 || chunk.CipherLength <= 0 || chunk.PlainLength <= 0
                    || chunk.PlainLength > ChunkSize || chunk.PlainOffset != expectedPlainOffset
                    || chunk.Offset + chunk.CipherLength > dataLength)
                    throw new InvalidDataException("Un bloque del índice no es válido.");
                if (!chunk.Compressed && chunk.CipherLength != chunk.PlainLength)
                    throw new InvalidDataException("La longitud de un bloque sin compresión no es válida.");
                expectedPlainOffset += chunk.PlainLength;
                spans.Add((chunk.Offset, chunk.Offset + chunk.CipherLength));
            }
            if (expectedPlainOffset != entry.Length)
                throw new InvalidDataException("Los bloques no cubren el archivo completo.");
        }
        spans.Sort((left, right) => left.Start.CompareTo(right.Start));
        long previousEnd = 0;
        foreach (var span in spans)
        {
            if (span.Start != previousEnd)
                throw new InvalidDataException("La zona de datos cifrados contiene huecos o bloques solapados.");
            previousEnd = span.End;
        }
        if (previousEnd != dataLength)
            throw new InvalidDataException("La zona de datos cifrados no coincide con el índice.");
    }

    private static IReadOnlyList<string> EnumerateSafePaths(string root)
    {
        // Already a full, eager walk of the tree (attributes are checked and
        // every entry collected before this returns), so callers can read
        // Count to enforce MaximumEntries up front instead of mid-loop.
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Las bóvedas no admiten enlaces ni puntos de montaje.");
                result.Add(path);
                if ((attributes & FileAttributes.Directory) != 0) stack.Push(path);
            }
        }
        return result.OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("El índice contiene una ruta no válida.");
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("El índice contiene una ruta no segura.");
        normalized = string.Join('/', parts);
        if (normalized.Length > 2048) throw new InvalidDataException("Una ruta de la bóveda es demasiado larga.");
        return normalized;
    }

    /// <summary>
    /// Normalizes <paramref name="path"/> and registers it in <paramref name="seenPaths"/>,
    /// throwing <paramref name="duplicatePathMessage"/> if it was already present. Shared by
    /// every validator that must reject duplicate vault entry paths, so the dedup rule can't
    /// drift between the write-time (caller input) and load-time (on-disk index) checks.
    /// </summary>
    private static string NormalizeAndRegisterUniquePath(HashSet<string> seenPaths, string path,
        string duplicatePathMessage)
    {
        var normalized = NormalizeRelativePath(path);
        if (!seenPaths.Add(normalized)) throw new InvalidDataException(duplicatePathMessage);
        return normalized;
    }

    private static IReadOnlyList<(VirtualEntrySource Source, string RelativePath)> ValidateVirtualSources(
        IReadOnlyList<VirtualEntrySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count > MaximumEntries) throw new InvalidDataException("La bóveda contiene demasiados elementos.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(VirtualEntrySource Source, string RelativePath)>(sources.Count);
        foreach (var source in sources)
        {
            if (source is null) throw new InvalidDataException("La bóveda contiene una entrada virtual no válida.");
            // OpenReadAsync is only ever invoked for files below; directory placeholders
            // legitimately have none, so don't demand one here.
            if (!source.IsDirectory && source.OpenReadAsync is null)
                throw new InvalidDataException("La bóveda contiene una entrada virtual no válida.");
            var relativePath = NormalizeAndRegisterUniquePath(paths, source.Path, "La bóveda contiene rutas duplicadas.");
            if (!source.IsDirectory && source.Length < 0)
                throw new InvalidDataException("La longitud de un archivo no es válida.");
            result.Add((source, relativePath));
        }
        result.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string ResolveExtractionPath(string destinationRoot, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var target = Path.GetFullPath(Path.Combine(destinationRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El índice intenta salir de la carpeta de trabajo.");
        return target;
    }

    private static bool CompressIfUseful(ReadOnlySpan<byte> plaintext, out byte[] prepared)
    {
        // Most media and installers are already compressed. Avoid spending CPU
        // on Deflate only to discard its result for high-entropy blocks.
        if (!ShouldAttemptCompression(plaintext))
        {
            prepared = plaintext.ToArray();
            return false;
        }
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(plaintext);
        var compressed = output.ToArray();
        if (compressed.Length + 32 < plaintext.Length)
        {
            prepared = compressed;
            return true;
        }
        CryptographicOperations.ZeroMemory(compressed);
        prepared = plaintext.ToArray();
        return false;
    }

    private static byte[] DecompressExact(byte[] compressed, int expectedLength)
    {
        var result = new byte[expectedLength];
        using var input = new MemoryStream(compressed, writable: false);
        using var decompressor = new DeflateStream(input, CompressionMode.Decompress);
        var offset = 0;
        while (offset < result.Length)
        {
            var read = decompressor.Read(result, offset, result.Length - offset);
            if (read == 0) break;
            offset += read;
        }
        if (offset != expectedLength || decompressor.ReadByte() != -1)
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidDataException("Un bloque comprimido no tiene la longitud esperada.");
        }
        return result;
    }

    private static void ReplaceAtomically(string temporaryPath, string path, bool createRecoveryBackup,
        bool preservePortableTpmCopy = false)
    {
        var backupPath = path + ".bak";
        var portableTpmRecoveryPath = VaultService.GetTpmRecoveryPath(path);
        if (File.Exists(path))
        {
            // On the first TPM conversion the current container is still
            // password-only. Preserve it permanently before .bak begins to
            // track later TPM-bound versions.
            if (preservePortableTpmCopy && !File.Exists(portableTpmRecoveryPath))
                File.Copy(path, portableTpmRecoveryPath, overwrite: false);
            if (createRecoveryBackup)
            {
                try { File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true); }
                catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
                {
                    // Some synchronized folders reject File.Replace although
                    // they accept normal replacement. Preserve the recovery
                    // copy first, then finish with the supported operation.
                    File.Copy(path, backupPath, true);
                    File.Move(temporaryPath, path, true);
                }
            }
            else File.Move(temporaryPath, path, true);
        }
        else File.Move(temporaryPath, path);
    }

    internal static void DeleteStaleWriteTemporaries(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(directory)) return;
        var fileName = Path.GetFileName(path);
        var patterns = new[]
        {
            $".{fileName}.*.v3tmp",
            $".{fileName}.*.index.tmp",
            $".{fileName}.*.password.tmp"
        };
        foreach (var temporary in patterns.SelectMany(pattern => Directory.EnumerateFiles(directory, pattern)))
        {
            try { File.Delete(temporary); }
            catch { /* A concurrent file handle is retained for recovery. */ }
        }
    }

    private static byte[] DerivePasswordKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private static bool ShouldAttemptCompression(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length < 4096) return true;
        var sampleLength = Math.Min(plaintext.Length, 64 * 1024);
        Span<int> frequencies = stackalloc int[256];
        for (var index = 0; index < sampleLength; index++) frequencies[plaintext[index]]++;
        double entropy = 0;
        foreach (var frequency in frequencies)
        {
            if (frequency == 0) continue;
            var probability = (double)frequency / sampleLength;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy < 7.65;
    }

    private static void ValidateKeyMaterial(byte[] passwordKey, byte[] salt)
    {
        if (passwordKey.Length != KeySize || salt.Length != SaltSize)
            throw new InvalidDataException("El material de clave PAVLT003 no es válido.");
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0) break;
            offset += read;
        }
        return offset;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var result = new byte[length];
        await ReadExactIntoAsync(stream, result).ConfigureAwait(false);
        return result;
    }

    private static async Task ReadExactIntoAsync(Stream stream, byte[] destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination.AsMemory(offset)).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("El contenedor está truncado.");
            offset += read;
        }
    }

    private static async Task ReadExactAtAsync(SafeFileHandle handle, byte[] destination, long fileOffset)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, destination.AsMemory(offset), fileOffset + offset)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("El contenedor está truncado.");
            offset += read;
        }
    }

    private static int FindFirstIntersectingChunk(IReadOnlyList<IndexChunk> chunks, long offset)
    {
        var low = 0;
        var high = chunks.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var chunkEnd = chunks[middle].PlainOffset + chunks[middle].PlainLength;
            if (chunkEnd <= offset) low = middle + 1;
            else high = middle - 1;
        }
        return low;
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long bytes)
    {
        var buffer = new byte[128 * 1024];
        try
        {
            var remaining = bytes;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, requested));
                if (read == 0) throw new EndOfStreamException("El contenedor está truncado.");
                await destination.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal sealed class OpenedVault : IDisposable
    {
        private const int MaximumCachedPlaintextBytes = 64 * 1024 * 1024;
        private readonly Dictionary<string, int> _entryIndices;
        private readonly Dictionary<(int EntryIndex, int ChunkIndex), LinkedListNode<CachedChunk>> _cachedChunks = [];
        private readonly LinkedList<CachedChunk> _cacheLru = [];
        private readonly object _cacheSync = new();
        private int _cachedPlaintextBytes;

        internal OpenedVault(string path, Header header, IndexDocument index, byte[] passwordKey, byte[] dataKey,
            VaultTpmBinding? tpmBinding)
        {
            Path = System.IO.Path.GetFullPath(path);
            Header = header;
            Index = index;
            PasswordKey = passwordKey;
            DataKey = dataKey;
            TpmBinding = tpmBinding;
            Vault = index.Vault.ToVault();
            Vault.IsTpmBound = tpmBinding is not null;
            _entryIndices = index.Entries
                .Select((entry, entryIndex) => (entry.Path, entryIndex))
                .ToDictionary(item => item.Path, item => item.entryIndex, StringComparer.OrdinalIgnoreCase);
            ReadHandle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }

        public string Path { get; }
        public VaultContainer Vault { get; }
        public byte[] PasswordKey { get; }
        public byte[] DataKey { get; }
        internal VaultTpmBinding? TpmBinding { get; }
        public byte[] Salt => Header.Salt;
        internal Header Header { get; }
        internal IndexDocument Index { get; }
        internal SafeFileHandle ReadHandle { get; }

        internal bool TryGetEntryIndex(string normalizedPath, out int entryIndex) =>
            _entryIndices.TryGetValue(normalizedPath, out entryIndex);

        internal bool TryGetCachedChunk(int entryIndex, int chunkIndex, out byte[] plaintext)
        {
            lock (_cacheSync)
            {
                if (!_cachedChunks.TryGetValue((entryIndex, chunkIndex), out var node))
                {
                    plaintext = Array.Empty<byte>();
                    return false;
                }
                _cacheLru.Remove(node);
                _cacheLru.AddFirst(node);
                plaintext = node.Value.Plaintext.ToArray();
                return true;
            }
        }

        /// <summary>
        /// Copies a portion of a cached plaintext chunk while it remains owned
        /// by the cache.  This avoids making a complete temporary copy for a
        /// normal Explorer read, and never exposes the cache buffer itself.
        /// </summary>
        internal bool TryCopyCachedChunkRange(int entryIndex, int chunkIndex, int sourceOffset,
            Memory<byte> destination)
        {
            lock (_cacheSync)
            {
                if (!_cachedChunks.TryGetValue((entryIndex, chunkIndex), out var node)) return false;
                _cacheLru.Remove(node);
                _cacheLru.AddFirst(node);
                node.Value.Plaintext.AsMemory(sourceOffset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        internal void CacheChunk(int entryIndex, int chunkIndex, byte[] plaintext)
        {
            if (plaintext.Length <= 0 || plaintext.Length > MaximumCachedPlaintextBytes) return;
            lock (_cacheSync)
            {
                var key = (entryIndex, chunkIndex);
                if (_cachedChunks.TryGetValue(key, out var existing))
                {
                    _cacheLru.Remove(existing);
                    _cachedPlaintextBytes -= existing.Value.Plaintext.Length;
                    CryptographicOperations.ZeroMemory(existing.Value.Plaintext);
                    _cachedChunks.Remove(key);
                }
                while (_cachedPlaintextBytes + plaintext.Length > MaximumCachedPlaintextBytes
                       && _cacheLru.Last is { } evicted)
                {
                    _cacheLru.RemoveLast();
                    _cachedChunks.Remove((evicted.Value.EntryIndex, evicted.Value.ChunkIndex));
                    _cachedPlaintextBytes -= evicted.Value.Plaintext.Length;
                    CryptographicOperations.ZeroMemory(evicted.Value.Plaintext);
                }
                var copy = plaintext.ToArray();
                var node = _cacheLru.AddFirst(new CachedChunk(entryIndex, chunkIndex, copy));
                _cachedChunks[key] = node;
                _cachedPlaintextBytes += copy.Length;
            }
        }

        public void Dispose()
        {
            lock (_cacheSync)
            {
                foreach (var chunk in _cacheLru) CryptographicOperations.ZeroMemory(chunk.Plaintext);
                _cachedChunks.Clear();
                _cacheLru.Clear();
                _cachedPlaintextBytes = 0;
            }
            ReadHandle.Dispose();
            CryptographicOperations.ZeroMemory(PasswordKey);
            CryptographicOperations.ZeroMemory(DataKey);
            TpmBinding?.Dispose();
        }

        private sealed record CachedChunk(int EntryIndex, int ChunkIndex, byte[] Plaintext);
    }

    internal sealed record VirtualEntrySource(string Path, bool IsDirectory, long Length,
        DateTime CreationUtc, DateTime LastWriteUtc, Func<Task<Stream>> OpenReadAsync);

    internal sealed record Header(byte[] Salt, byte[] KeyNonce, byte[] KeyTag, byte[] WrappedDataKey,
        long DataLength, long IndexLength, int HeaderSize, VaultTpmBinding? TpmBinding);

    internal sealed class IndexDocument
    {
        public IndexManifest Vault { get; set; } = new();
        public List<IndexEntry> Entries { get; set; } = [];
    }

    internal sealed class IndexManifest
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int AutoLockMinutes { get; set; }
        public int InactivityAutoLockMinutes { get; set; }
        public bool IsTpmBound { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ModifiedUtc { get; set; }

        public static IndexManifest From(VaultContainer vault) => new()
        {
            Id = vault.Id,
            Name = vault.Name,
            Description = vault.Description,
            AutoLockMinutes = Math.Clamp(vault.AutoLockMinutes, 1, 10_080),
            InactivityAutoLockMinutes = Math.Clamp(vault.InactivityAutoLockMinutes, 0, 10_080),
            IsTpmBound = vault.IsTpmBound,
            CreatedUtc = vault.CreatedUtc,
            ModifiedUtc = DateTime.UtcNow
        };

        public VaultContainer ToVault() => new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            AutoLockMinutes = Math.Clamp(AutoLockMinutes, 1, 10_080),
            InactivityAutoLockMinutes = Math.Clamp(InactivityAutoLockMinutes, 0, 10_080),
            IsTpmBound = IsTpmBound,
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc
        };
    }

    internal sealed class IndexEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Length { get; set; }
        public DateTime CreationUtc { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public List<IndexChunk> Chunks { get; set; } = [];
    }

    internal sealed class IndexChunk
    {
        public long Offset { get; set; }
        public int CipherLength { get; set; }
        public long PlainOffset { get; set; }
        public int PlainLength { get; set; }
        public bool Compressed { get; set; }
        public byte[] Nonce { get; set; } = [];
        public byte[] Tag { get; set; } = [];
    }
}
