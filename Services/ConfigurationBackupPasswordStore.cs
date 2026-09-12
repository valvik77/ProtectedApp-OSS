using System.Security.Cryptography;
using System.Text;

namespace ProtectedApp.Services;

/// <summary>
/// Holds only the scheduler's local copy of the backup password. The backup
/// itself remains password-encrypted and can be restored elsewhere with the
/// password known by the user; this DPAPI value is never exported.
/// </summary>
internal static class ConfigurationBackupPasswordStore
{
    private static readonly byte[] Entropy = "ProtectedApp.ConfigurationBackupPassword.v1"u8.ToArray();

    internal static void Save(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("La contraseña no puede estar vacía.", nameof(password));
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            var path = GetPath();
            var temporary = path + ".tmp-" + Environment.ProcessId;
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string? Load()
    {
        var path = GetPath();
        if (!File.Exists(path)) return null;
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        // A profile migration, an unavailable redirected profile, or a TPM/DPAPI
        // reset must disable this local scheduler safely rather than letting a
        // timer callback fail outside the UI error path.
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static void Delete()
    {
        try { if (File.Exists(GetPath())) File.Delete(GetPath()); }
        catch { }
    }

    internal static bool Exists() => File.Exists(GetPath());

    private static string GetPath()
    {
        var overrideFolder = Environment.GetEnvironmentVariable("PROTECTEDAPP_DATA_DIR");
        var directory = string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp")
            : Path.GetFullPath(overrideFolder);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "configuration-backup-password.dat");
    }
}
