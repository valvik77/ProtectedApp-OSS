using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

/// <summary>
/// PAVLT003: índice cifrado y autenticado, clave de datos envuelta por la
/// contraseña y contenido dividido en bloques AEAD de acceso aleatorio.
/// </summary>
internal static class VaultFormatV3
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PAVLT003");
    private const byte Version = 3;
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
    private const int HeaderSize = 8 + 1 + sizeof(int) + SaltSize + NonceSize + TagSize + KeySize
        + sizeof(long) + sizeof(long);
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
                FileShare.Read | FileShare.Delete, Magic.Length + 1, FileOptions.SequentialScan);
            Span<byte> prefix = stackalloc byte[Magic.Length + 1];
            return stream.Read(prefix) == prefix.Length
                && prefix[..Magic.Length].SequenceEqual(Magic)
                && prefix[^1] == Version;
        }
        catch { return false; }
    }

    public static bool HasStructurallyValidEnvelope(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, HeaderSize, FileOptions.SequentialScan);
            if (stream.Length < HeaderSize + NonceSize + TagSize + 1) return false;
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadByte() != Version
                || reader.ReadInt32() != Iterations) return false;
            if (reader.ReadBytes(SaltSize).Length != SaltSize
                || reader.ReadBytes(NonceSize).Length != NonceSize
                || reader.ReadBytes(TagSize).Length != TagSize
                || reader.ReadBytes(KeySize).Length != KeySize) return false;
            var dataLength = reader.ReadInt64();
            var indexLength = reader.ReadInt64();
            return dataLength >= 0 && dataLength <= MaximumDataBytes
                && indexLength > 0 && indexLength <= MaximumIndexBytes
                && stream.Length == HeaderSize + dataLength + NonceSize + TagSize + indexLength;
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
                createRecoveryBackup);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static async Task WriteFromDirectoryAsync(VaultContainer vault, string? sourceDirectory, string path,
        byte[] passwordKey, byte[] salt, byte[]? existingDataKey, bool createRecoveryBackup = true)
    {
        ValidateKeyMaterial(passwordKey, salt);
        var dataKey = existingDataKey?.ToArray() ?? RandomNumberGenerator.GetBytes(KeySize);
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
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                output.Position = HeaderSize;
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    var root = Path.GetFullPath(sourceDirectory);
                    if (!Directory.Exists(root)) throw new DirectoryNotFoundException("La carpeta de trabajo no existe.");
                    foreach (var sourcePath in EnumerateSafePaths(root))
                    {
                        if (index.Entries.Count >= MaximumEntries)
                            throw new InvalidDataException("La bóveda contiene demasiados elementos.");
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
                        await EncryptFileAsync(vault.Id, entryIndex, sourcePath, entry, output, dataKey,
                            () => ++chunkCount > MaximumChunks);
                    }
                }

                var dataLength = output.Position - HeaderSize;
                if (dataLength < 0 || dataLength > MaximumDataBytes)
                    throw new InvalidDataException("Los bloques cifrados superan el límite admitido.");
                indexPlaintext = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
                if (indexPlaintext.Length <= 0 || indexPlaintext.Length > MaximumIndexBytes)
                    throw new InvalidDataException("El índice de la bóveda es demasiado grande.");

                var indexLength = (long)indexPlaintext.Length;
                var keyNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var keyTag = new byte[TagSize];
                var wrappedDataKey = new byte[KeySize];
                using (var aes = new AesGcm(passwordKey, TagSize))
                    aes.Encrypt(keyNonce, dataKey, wrappedDataKey, keyTag, BuildWrapAad(salt));
                var header = BuildHeader(salt, keyNonce, keyTag, wrappedDataKey, dataLength, indexLength);

                var indexNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var indexTag = new byte[TagSize];
                indexCiphertext = new byte[indexPlaintext.Length];
                using (var aes = new AesGcm(dataKey, TagSize))
                    aes.Encrypt(indexNonce, indexPlaintext, indexCiphertext, indexTag,
                        BuildIndexAad(dataLength, indexLength));
                output.Position = HeaderSize + dataLength;
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
            ReplaceAtomically(temporaryPath, path, createRecoveryBackup);
            DeleteStaleWriteTemporaries(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
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
        byte[] existingDataKey, bool createRecoveryBackup = true)
    {
        ValidateKeyMaterial(passwordKey, salt);
        ArgumentNullException.ThrowIfNull(existingDataKey);
        if (existingDataKey.Length != KeySize) throw new InvalidDataException("La clave de datos no es válida.");
        if (sources.Count > MaximumEntries) throw new InvalidDataException("La bóveda contiene demasiados elementos.");

        var dataKey = existingDataKey.ToArray();
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
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                output.Position = HeaderSize;
                foreach (var source in sources.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = NormalizeRelativePath(source.Path);
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
                    if (source.Length < 0) throw new InvalidDataException("La longitud de un archivo no es válida.");
                    expandedBytes = checked(expandedBytes + source.Length);
                    if (expandedBytes > MaximumExpandedBytes)
                        throw new InvalidDataException("El contenido de la bóveda supera 1 GB.");
                    await using var input = await source.OpenReadAsync();
                    if (!input.CanRead) throw new InvalidDataException("No se puede leer un archivo virtual.");
                    await EncryptStreamAsync(vault.Id, entryIndex, input, entry, output, dataKey,
                        () => ++chunkCount > MaximumChunks);
                }

                var dataLength = output.Position - HeaderSize;
                if (dataLength < 0 || dataLength > MaximumDataBytes)
                    throw new InvalidDataException("Los bloques cifrados superan el límite admitido.");
                indexPlaintext = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
                if (indexPlaintext.Length <= 0 || indexPlaintext.Length > MaximumIndexBytes)
                    throw new InvalidDataException("El índice de la bóveda es demasiado grande.");

                var indexLength = (long)indexPlaintext.Length;
                var keyNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var keyTag = new byte[TagSize];
                var wrappedDataKey = new byte[KeySize];
                using (var aes = new AesGcm(passwordKey, TagSize))
                    aes.Encrypt(keyNonce, dataKey, wrappedDataKey, keyTag, BuildWrapAad(salt));
                var header = BuildHeader(salt, keyNonce, keyTag, wrappedDataKey, dataLength, indexLength);

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
            ReplaceAtomically(temporaryPath, path, createRecoveryBackup);
            DeleteStaleWriteTemporaries(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
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
        var entryIndex = opened.Index.Entries.FindIndex(entry =>
            !entry.IsDirectory && entry.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (entryIndex < 0) throw new FileNotFoundException("El archivo no existe dentro de la bóveda.", relativePath);
        var entry = opened.Index.Entries[entryIndex];
        if (offset > entry.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var resultLength = (int)Math.Min(count, entry.Length - offset);
        var result = new byte[resultLength];
        if (resultLength == 0) return result;
        var requestedEnd = offset + resultLength;
        var written = 0;
        for (var chunkIndex = 0; chunkIndex < entry.Chunks.Count; chunkIndex++)
        {
            var chunk = entry.Chunks[chunkIndex];
            var chunkEnd = chunk.PlainOffset + chunk.PlainLength;
            if (chunkEnd <= offset || chunk.PlainOffset >= requestedEnd) continue;
            var plaintext = await ReadChunkAsync(opened, entryIndex, chunkIndex);
            try
            {
                var from = (int)Math.Max(0, offset - chunk.PlainOffset);
                var to = (int)Math.Min(chunk.PlainLength, requestedEnd - chunk.PlainOffset);
                var length = to - from;
                Buffer.BlockCopy(plaintext, from, result, written, length);
                written += length;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        if (written != result.Length) throw new InvalidDataException("No se pudo reconstruir el intervalo solicitado.");
        return result;
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
                    opened.Salt, opened.DataKey, createRecoveryBackup: false);
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
                newPasswordKey, newSalt, newDataKey, createRecoveryBackup: true);
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
        byte[]? indexCiphertext = null;
        byte[]? indexPlaintext = null;
        try
        {
            dataKey = new byte[KeySize];
            using (var aes = new AesGcm(passwordKey, TagSize))
                aes.Decrypt(header.KeyNonce, header.WrappedDataKey, header.KeyTag, dataKey,
                    BuildWrapAad(header.Salt));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            stream.Position = HeaderSize + header.DataLength;
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
            var result = new OpenedVault(path, header, index, passwordKey.ToArray(), dataKey);
            dataKey = null;
            return result;
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("La contraseña es incorrecta o la bóveda ha sido modificada.");
        }
        finally
        {
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            if (indexCiphertext is not null) CryptographicOperations.ZeroMemory(indexCiphertext);
            if (indexPlaintext is not null) CryptographicOperations.ZeroMemory(indexPlaintext);
        }
    }

    private static async Task EncryptFileAsync(Guid vaultId, int entryIndex, string sourcePath, IndexEntry entry,
        FileStream output, byte[] dataKey, Func<bool> chunkLimitExceeded)
    {
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await EncryptStreamAsync(vaultId, entryIndex, input, entry, output, dataKey, chunkLimitExceeded);
    }

    private static async Task EncryptStreamAsync(Guid vaultId, int entryIndex, Stream input, IndexEntry entry,
        FileStream output, byte[] dataKey, Func<bool> chunkLimitExceeded)
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
                    var offset = output.Position - HeaderSize;
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

    private static async Task<byte[]> ReadChunkAsync(OpenedVault opened, int entryIndex, int chunkIndex)
    {
        var entry = opened.Index.Entries[entryIndex];
        var chunk = entry.Chunks[chunkIndex];
        var ciphertext = new byte[chunk.CipherLength];
        var prepared = new byte[chunk.CipherLength];
        try
        {
            await using var stream = new FileStream(opened.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            stream.Position = HeaderSize + chunk.Offset;
            await ReadExactIntoAsync(stream, ciphertext);
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
                return result;
            }
            return DecompressExact(prepared, chunk.PlainLength);
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
        var wrappedKey = new byte[KeySize];
        using (var aes = new AesGcm(opened.PasswordKey, TagSize))
            aes.Encrypt(keyNonce, opened.DataKey, wrappedKey, keyTag, BuildWrapAad(opened.Header.Salt));
        var headerBytes = BuildHeader(opened.Header.Salt, keyNonce, keyTag, wrappedKey,
            opened.Header.DataLength, indexLength);
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
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await destination.WriteAsync(headerBytes);
                source.Position = HeaderSize;
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
            ReplaceAtomically(temporaryPath, path, createRecoveryBackup);
            DeleteStaleWriteTemporaries(path);
        }
        finally
        {
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
            FileShare.Read | FileShare.Delete, HeaderSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await ReadExactAsync(stream, HeaderSize);
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadByte() != Version)
            throw new InvalidDataException("El contenedor no usa el formato PAVLT003.");
        if (reader.ReadInt32() != Iterations)
            throw new InvalidDataException("La derivación de clave PAVLT003 no es compatible.");
        var salt = reader.ReadBytes(SaltSize);
        var keyNonce = reader.ReadBytes(NonceSize);
        var keyTag = reader.ReadBytes(TagSize);
        var wrappedKey = reader.ReadBytes(KeySize);
        var dataLength = reader.ReadInt64();
        var indexLength = reader.ReadInt64();
        if (salt.Length != SaltSize || keyNonce.Length != NonceSize || keyTag.Length != TagSize
            || wrappedKey.Length != KeySize || dataLength < 0 || dataLength > MaximumDataBytes
            || indexLength <= 0 || indexLength > MaximumIndexBytes
            || stream.Length != HeaderSize + dataLength + NonceSize + TagSize + indexLength)
            throw new InvalidDataException("La cabecera PAVLT003 no es válida.");
        return new Header(salt, keyNonce, keyTag, wrappedKey, dataLength, indexLength);
    }

    private static byte[] BuildHeader(byte[] salt, byte[] keyNonce, byte[] keyTag, byte[] wrappedKey,
        long dataLength, long indexLength)
    {
        using var stream = new MemoryStream(HeaderSize);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(Iterations);
        writer.Write(salt);
        writer.Write(keyNonce);
        writer.Write(keyTag);
        writer.Write(wrappedKey);
        writer.Write(dataLength);
        writer.Write(indexLength);
        var result = stream.ToArray();
        if (result.Length != HeaderSize) throw new InvalidOperationException("La cabecera PAVLT003 tiene un tamaño inesperado.");
        return result;
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
            entry.Path = NormalizeRelativePath(entry.Path);
            if (!paths.Add(entry.Path)) throw new InvalidDataException("El índice contiene rutas duplicadas.");
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

    private static IEnumerable<string> EnumerateSafePaths(string root)
    {
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
        return result.OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase);
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

    private static void ReplaceAtomically(string temporaryPath, string path, bool createRecoveryBackup)
    {
        var backupPath = path + ".bak";
        if (File.Exists(path))
        {
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
        await ReadExactIntoAsync(stream, result);
        return result;
    }

    private static async Task ReadExactIntoAsync(Stream stream, byte[] destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination.AsMemory(offset));
            if (read == 0) throw new EndOfStreamException("El contenedor está truncado.");
            offset += read;
        }
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
        internal OpenedVault(string path, Header header, IndexDocument index, byte[] passwordKey, byte[] dataKey)
        {
            Path = System.IO.Path.GetFullPath(path);
            Header = header;
            Index = index;
            PasswordKey = passwordKey;
            DataKey = dataKey;
            Vault = index.Vault.ToVault();
        }

        public string Path { get; }
        public VaultContainer Vault { get; }
        public byte[] PasswordKey { get; }
        public byte[] DataKey { get; }
        public byte[] Salt => Header.Salt;
        internal Header Header { get; }
        internal IndexDocument Index { get; }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(PasswordKey);
            CryptographicOperations.ZeroMemory(DataKey);
        }
    }

    internal sealed record VirtualEntrySource(string Path, bool IsDirectory, long Length,
        DateTime CreationUtc, DateTime LastWriteUtc, Func<Task<Stream>> OpenReadAsync);

    internal sealed record Header(byte[] Salt, byte[] KeyNonce, byte[] KeyTag, byte[] WrappedDataKey,
        long DataLength, long IndexLength);

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
        public DateTime CreatedUtc { get; set; }
        public DateTime ModifiedUtc { get; set; }

        public static IndexManifest From(VaultContainer vault) => new()
        {
            Id = vault.Id,
            Name = vault.Name,
            Description = vault.Description,
            AutoLockMinutes = Math.Clamp(vault.AutoLockMinutes, 1, 10_080),
            InactivityAutoLockMinutes = Math.Clamp(vault.InactivityAutoLockMinutes, 0, 10_080),
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
