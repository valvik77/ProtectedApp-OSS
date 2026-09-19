using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProtectedApp.UserState.v1");
    private static readonly byte[] TpmEnvelopePrefix = "PATPM1\n"u8.ToArray();
    private readonly string _statePath;
    private readonly string _legacyStatePath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private string? _tpmKeyName;

    public bool IsTpmProtectionEnabled => !string.IsNullOrWhiteSpace(_tpmKeyName);

    public StateStore()
    {
        var overrideFolder = Environment.GetEnvironmentVariable("PROTECTEDAPP_DATA_DIR");
        var folder = string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp")
            : Path.GetFullPath(overrideFolder);
        Directory.CreateDirectory(folder);
        _statePath = Path.Combine(folder, "state.dat");
        _legacyStatePath = Path.Combine(folder, "state.json");
    }

    public async Task<AppState> LoadAsync()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var encrypted = await File.ReadAllBytesAsync(_statePath);
                var json = IsTpmEnvelope(encrypted)
                    // TPM/CNG calls can wait on firmware or the provider. Keep
                    // them off the WinUI dispatcher so loading protected rules
                    // never makes the panel appear hung.
                    ? await Task.Run(() => UnprotectWithTpm(encrypted)).ConfigureAwait(false)
                    : ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
            }
            if (!File.Exists(_legacyStatePath)) return new AppState();
            AppState migrated;
            await using (var stream = File.OpenRead(_legacyStatePath))
                migrated = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions) ?? new AppState();
            await SaveAsync(migrated);
            File.Delete(_legacyStatePath);
            return migrated;
        }
        catch (TpmStateProtectionUnavailableException)
        {
            // A TPM reset/reinstall must not make us overwrite the only copy
            // of the configuration with a fresh empty state.
            throw;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            var source = File.Exists(_statePath) ? _statePath : _legacyStatePath;
            if (File.Exists(source))
            {
                var backup = source + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(source, backup, true);
            }
            return new AppState();
        }
    }

    public async Task SaveAsync(AppState state)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(state).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SaveCoreAsync(AppState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var encrypted = IsTpmProtectionEnabled
            // Saving a rule also saves state.dat. Do the non-exportable-key
            // operation on a worker so application management stays usable
            // while a TPM provider is slow to answer.
            ? await Task.Run(() => ProtectWithTpm(json, _tpmKeyName!)).ConfigureAwait(false)
            : ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var temporary = Path.Combine(Path.GetDirectoryName(_statePath)!,
            $".{Path.GetFileName(_statePath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Atomically converts the persisted state to or from TPM protection.</summary>
    public async Task SetTpmProtectionAsync(AppState state, bool enabled)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled == IsTpmProtectionEnabled) return;

            if (enabled)
            {
                var createdKey = await Task.Run(TpmStateProtector.CreateKey).ConfigureAwait(false);
                _tpmKeyName = createdKey;
                try
                {
                    await SaveCoreAsync(state).ConfigureAwait(false);
                }
                catch
                {
                    _tpmKeyName = null;
                    await Task.Run(() => TpmStateProtector.DeleteKey(createdKey)).ConfigureAwait(false);
                    throw;
                }
                return;
            }

            var previousKey = _tpmKeyName!;
            _tpmKeyName = null;
            try
            {
                await SaveCoreAsync(state).ConfigureAwait(false);
            }
            catch
            {
                _tpmKeyName = previousKey;
                throw;
            }
            await Task.Run(() => TpmStateProtector.DeleteKey(previousKey)).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private byte[] ProtectWithTpm(byte[] plainText, string keyName)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipherText = new byte[plainText.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(dataKey, tagSizeInBytes: tag.Length);
            aes.Encrypt(nonce, plainText, cipherText, tag, GetAssociatedData(keyName));
            var envelope = new TpmStateEnvelope
            {
                Format = TpmStateProtector.EnvelopeFormat,
                Version = 1,
                KeyName = keyName,
                WrappedDataKey = Convert.ToBase64String(TpmStateProtector.WrapDataKey(keyName, dataKey)),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                CipherText = Convert.ToBase64String(cipherText)
            };
            var serialized = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            var result = new byte[TpmEnvelopePrefix.Length + serialized.Length];
            TpmEnvelopePrefix.CopyTo(result, 0);
            serialized.CopyTo(result, TpmEnvelopePrefix.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private byte[] UnprotectWithTpm(byte[] protectedState)
    {
        var envelope = JsonSerializer.Deserialize<TpmStateEnvelope>(protectedState.AsSpan(TpmEnvelopePrefix.Length), JsonOptions)
            ?? throw new CryptographicException("El estado protegido por TPM no tiene un formato válido.");
        if (envelope.Format != TpmStateProtector.EnvelopeFormat || envelope.Version != 1
            || string.IsNullOrWhiteSpace(envelope.KeyName))
            throw new CryptographicException("El estado protegido por TPM no es compatible.");

        var wrappedKey = Convert.FromBase64String(envelope.WrappedDataKey ?? string.Empty);
        var nonce = Convert.FromBase64String(envelope.Nonce ?? string.Empty);
        var tag = Convert.FromBase64String(envelope.Tag ?? string.Empty);
        var cipherText = Convert.FromBase64String(envelope.CipherText ?? string.Empty);
        if (nonce.Length != 12 || tag.Length != 16 || wrappedKey.Length == 0)
            throw new CryptographicException("El estado protegido por TPM está incompleto.");

        var dataKey = TpmStateProtector.UnwrapDataKey(envelope.KeyName, wrappedKey);
        try
        {
            if (dataKey.Length != 32) throw new CryptographicException("La clave de estado TPM no es válida.");
            var plainText = new byte[cipherText.Length];
            using var aes = new AesGcm(dataKey, tagSizeInBytes: tag.Length);
            aes.Decrypt(nonce, cipherText, tag, plainText, GetAssociatedData(envelope.KeyName));
            _tpmKeyName = envelope.KeyName;
            return plainText;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static bool IsTpmEnvelope(byte[] value) => value.AsSpan().StartsWith(TpmEnvelopePrefix);

    private static byte[] GetAssociatedData(string keyName) => Encoding.UTF8.GetBytes($"{TpmStateProtector.EnvelopeFormat}|1|{keyName}");

    private sealed class TpmStateEnvelope
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? KeyName { get; set; }
        public string? WrappedDataKey { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public string? CipherText { get; set; }
    }
}
