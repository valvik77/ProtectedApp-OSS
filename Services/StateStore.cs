using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProtectedApp.UserState.v1");
    private readonly string _statePath;
    private readonly string _legacyStatePath;

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
                var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
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
        var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var temp = _statePath + ".tmp-" + Environment.ProcessId;
        await File.WriteAllBytesAsync(temp, encrypted);
        File.Move(temp, _statePath, true);
    }
}
