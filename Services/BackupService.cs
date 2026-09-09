using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProtectedApp.Models;
using ProtectedApp.Shared;

namespace ProtectedApp.Services;

public static class BackupService
{
    private const string FormatName = "ProtectedAppBackup";
    private const int FormatVersion = 1;
    private const int KdfIterations = 600_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int MaximumBackupBytes = 16 * 1024 * 1024;
    private const int MaximumApplications = 1_000;
    private const int MaximumFolders = 250;
    private const int MaximumVaults = 250;
    private const int MaximumActivityEntries = 500;
    private static readonly byte[] AdditionalData = Encoding.UTF8.GetBytes(
        $"{FormatName}|{FormatVersion}|PBKDF2-SHA256|{KdfIterations}|AES-256-GCM");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 32
    };

    public static Task<byte[]> CreateAsync(BackupSnapshot snapshot, string password) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(password) || password.Length < PasswordService.MinimumPasswordLength)
            throw new ArgumentException("La contraseña de la copia debe tener al menos 12 caracteres.", nameof(password));

        var normalized = NormalizeSnapshot(snapshot);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        if (plaintext.Length > MaximumBackupBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("La configuración es demasiado grande para crear una copia.");
        }
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations,
            HashAlgorithmName.SHA256, KeySize);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData);
            var envelope = new BackupEnvelope
            {
                Format = FormatName,
                Version = FormatVersion,
                Kdf = "PBKDF2-SHA256",
                Iterations = KdfIterations,
                Cipher = "AES-256-GCM",
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Data = Convert.ToBase64String(ciphertext)
            };
            return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    });

    public static Task<BackupSnapshot> ReadAsync(byte[] backupBytes, string password) => Task.Run(() =>
    {
        if (backupBytes is null || backupBytes.Length == 0 || backupBytes.Length > MaximumBackupBytes)
            throw new InvalidDataException("El archivo no es una copia válida de ProtectedApp.");
        if (string.IsNullOrEmpty(password))
            throw new InvalidDataException("Introduce la contraseña de la copia.");

        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<BackupEnvelope>(backupBytes, JsonOptions)
                ?? throw new InvalidDataException("El archivo no contiene una copia válida.");
            if (envelope.Format != FormatName || envelope.Version != FormatVersion
                || envelope.Kdf != "PBKDF2-SHA256" || envelope.Iterations != KdfIterations
                || envelope.Cipher != "AES-256-GCM")
                throw new InvalidDataException("La versión o el formato de la copia no es compatible.");

            var salt = DecodeExact(envelope.Salt, SaltSize);
            var nonce = DecodeExact(envelope.Nonce, NonceSize);
            var tag = DecodeExact(envelope.Tag, TagSize);
            var ciphertext = Convert.FromBase64String(envelope.Data ?? string.Empty);
            plaintext = new byte[ciphertext.Length];
            key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations,
                HashAlgorithmName.SHA256, KeySize);
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalData);

            var snapshot = JsonSerializer.Deserialize<BackupSnapshot>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("El contenido de la copia no es válido.");
            return NormalizeSnapshot(snapshot);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("La contraseña es incorrecta o el archivo ha sido modificado.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("El archivo no es una copia válida de ProtectedApp.", ex);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    });

    private static BackupSnapshot NormalizeSnapshot(BackupSnapshot source)
    {
        if (source.Version != FormatVersion)
            throw new InvalidDataException("La versión interna de la copia no es compatible.");
        source.Applications ??= [];
        source.Folders ??= [];
        source.Vaults ??= [];
        source.ActivityHistory ??= [];
        if (source.Applications.Count > MaximumApplications)
            throw new InvalidDataException("La copia contiene demasiadas reglas.");
        if (source.Folders.Count > MaximumFolders)
            throw new InvalidDataException("La copia contiene demasiadas carpetas.");
        if (source.Vaults.Count > MaximumVaults)
            throw new InvalidDataException("La copia contiene demasiadas bóvedas.");

        var applications = new Dictionary<string, ProtectedApplication>(StringComparer.OrdinalIgnoreCase);
        var applicationIds = new HashSet<Guid>();
        foreach (var application in source.Applications)
        {
            if (application is null) throw new InvalidDataException("La copia contiene una regla vacía.");
            if (string.IsNullOrWhiteSpace(application.Name) || application.Name.Length > 200
                || string.IsNullOrWhiteSpace(application.Path) || application.Path.Length > 32_767
                || !Path.IsPathFullyQualified(application.Path) || !ProtectedTarget.IsSupported(application.Path))
                throw new InvalidDataException("La copia contiene una regla no válida.");

            var fullPath = Path.GetFullPath(application.Path);
            if (applications.ContainsKey(fullPath)) continue;
            var (hash, salt) = NormalizeCredential(application.PasswordHash, application.PasswordSalt);
            var forcedClose = Math.Clamp(application.ForceCloseAfterMinutes, 0, 10_080);
            var inactiveClose = Math.Clamp(application.ForceCloseAfterInactivityMinutes, 0, 10_080);
            if (forcedClose > 0) inactiveClose = 0;
            var unlockGrace = Math.Clamp(application.UnlockGraceMinutes, 0, 10_080);
            var closeLimit = Math.Max(forcedClose, inactiveClose);
            if (closeLimit > 0 && unlockGrace > closeLimit) unlockGrace = closeLimit;
            var scheduleDays = application.ScheduleDays & (int)ScheduleDays.EveryDay;
            if (application.ScheduleEnabled && scheduleDays == 0)
                throw new InvalidDataException("La copia contiene un horario sin días seleccionados.");
            var id = application.Id;
            if (id == Guid.Empty || !applicationIds.Add(id))
            {
                do id = Guid.NewGuid(); while (!applicationIds.Add(id));
            }
            applications[fullPath] = new ProtectedApplication
            {
                Id = id,
                Name = application.Name.Trim(),
                Path = fullPath,
                Category = string.IsNullOrWhiteSpace(application.Category) ? "General" : application.Category.Trim(),
                IsEnabled = application.IsEnabled,
                PasswordHash = hash,
                PasswordSalt = salt,
                UnlockGraceMinutes = unlockGrace,
                ForceCloseAfterMinutes = forcedClose,
                ForceCloseAfterInactivityMinutes = inactiveClose,
                ScheduleEnabled = application.ScheduleEnabled,
                ScheduleDays = scheduleDays,
                ScheduleStartMinutes = Math.Clamp(application.ScheduleStartMinutes, 0, 1_439),
                ScheduleEndMinutes = Math.Clamp(application.ScheduleEndMinutes, 0, 1_439),
                BlockOutsideSchedule = application.BlockOutsideSchedule,
                AddedAt = application.AddedAt,
                BlockCount = Math.Max(0, application.BlockCount)
            };
        }

        var folders = new Dictionary<string, ProtectedFolder>(StringComparer.OrdinalIgnoreCase);
        var folderIds = new HashSet<Guid>();
        foreach (var folder in source.Folders)
        {
            if (folder is null || string.IsNullOrWhiteSpace(folder.Name) || folder.Name.Length > 200
                || string.IsNullOrWhiteSpace(folder.Path) || folder.Path.Length > 32_767
                || !Path.IsPathFullyQualified(folder.Path))
                throw new InvalidDataException("La copia contiene una carpeta no válida.");
            var fullPath = Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar);
            if (folders.ContainsKey(fullPath)) continue;
            var (hash, salt) = NormalizeCredential(folder.PasswordHash, folder.PasswordSalt);
            var id = folder.Id;
            if (id == Guid.Empty || !folderIds.Add(id))
            {
                do id = Guid.NewGuid(); while (!folderIds.Add(id));
            }
            folders[fullPath] = new ProtectedFolder
            {
                Id = id,
                Name = folder.Name.Trim(),
                Path = fullPath,
                IsEnabled = folder.IsEnabled,
                PasswordHash = hash,
                PasswordSalt = salt,
                UnlockMinutes = Math.Clamp(folder.UnlockMinutes, 1, 10_080)
            };
        }

        var vaults = new Dictionary<string, VaultContainer>(StringComparer.OrdinalIgnoreCase);
        var vaultIds = new HashSet<Guid>();
        foreach (var vault in source.Vaults)
        {
            if (vault is null || string.IsNullOrWhiteSpace(vault.Name) || vault.Name.Length > 200
                || string.IsNullOrWhiteSpace(vault.VaultFilePath) || vault.VaultFilePath.Length > 32_767
                || !Path.IsPathFullyQualified(vault.VaultFilePath)
                || !string.Equals(Path.GetExtension(vault.VaultFilePath), ".pavault", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La copia contiene una referencia de bóveda no válida.");
            var fullPath = Path.GetFullPath(vault.VaultFilePath);
            if (vaults.ContainsKey(fullPath)) continue;
            var id = vault.Id;
            if (id == Guid.Empty || !vaultIds.Add(id))
            {
                do id = Guid.NewGuid(); while (!vaultIds.Add(id));
            }
            vaults[fullPath] = new VaultContainer
            {
                Id = id,
                Name = vault.Name.Trim(),
                Description = Truncate(vault.Description, 300),
                IsEnabled = vault.IsEnabled,
                AutoLockMinutes = Math.Clamp(vault.AutoLockMinutes, 1, 10_080),
                InactivityAutoLockMinutes = Math.Clamp(vault.InactivityAutoLockMinutes, 0, 10_080),
                VaultFilePath = fullPath,
                CreatedUtc = vault.CreatedUtc,
                ModifiedUtc = vault.ModifiedUtc
            };
        }

        var allowedAutoLockValues = new[] { 0, 1, 5, 15, 30 };
        var autoLock = allowedAutoLockValues.Contains(source.ManagementAutoLockMinutes)
            ? source.ManagementAutoLockMinutes
            : 0;
        var pollInterval = source.PollIntervalMilliseconds switch
        {
            < 350 => 200,
            < 750 => 500,
            _ => 1_000
        };
        var activity = source.ActivityHistory
            .Where(entry => entry is not null)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaximumActivityEntries)
            .Select(entry => new ActivityEntry
            {
                Timestamp = entry.Timestamp,
                AppName = Truncate(entry.AppName, 500),
                Message = Truncate(entry.Message, 2_000),
                Kind = Enum.IsDefined(entry.Kind) ? entry.Kind : ActivityEventKind.System
            })
            .ToList();

        return new BackupSnapshot
        {
            Version = FormatVersion,
            ExportedAtUtc = source.ExportedAtUtc == default ? DateTimeOffset.UtcNow : source.ExportedAtUtc,
            StartWithWindows = source.StartWithWindows,
            ShowTrayIcon = source.ShowTrayIcon,
            LockPanelWhenHidden = source.LockPanelWhenHidden,
            ThemePreference = source.ThemePreference?.ToLowerInvariant() switch
            {
                "dark" => "dark",
                "light" => "light",
                _ => null
            },
            LanguagePreference = source.LanguagePreference?.ToLowerInvariant() switch
            {
                "es" => "es",
                "en" => "en",
                _ => null
            },
            ManagementAutoLockMinutes = autoLock,
            PollIntervalMilliseconds = pollInterval,
            VaultBackupDirectory = source.VaultBackupDirectory,
            VaultBackupIntervalHours = Math.Clamp(source.VaultBackupIntervalHours, 1, 24 * 7),
            VaultBackupRetentionCount = Math.Clamp(source.VaultBackupRetentionCount, 1, 20),
            VaultBackupNotificationsEnabled = source.VaultBackupNotificationsEnabled,
            CloseWarningNotificationsEnabled = source.CloseWarningNotificationsEnabled,
            SecurityAlertsEnabled = source.SecurityAlertsEnabled,
            ImmediateLockHotkey = source.ImmediateLockHotkey,
            Applications = applications.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Folders = folders.Values.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Vaults = vaults.Values.OrderBy(vault => vault.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            ActivityHistory = activity
        };
    }

    private static (string? Hash, string? Salt) NormalizeCredential(string? hash, string? salt)
    {
        if (string.IsNullOrWhiteSpace(hash) && string.IsNullOrWhiteSpace(salt)) return (null, null);
        try
        {
            var encodedSalt = salt?.StartsWith("pbkdf2-sha256:600000:", StringComparison.Ordinal) == true
                ? salt["pbkdf2-sha256:600000:".Length..]
                : salt;
            if (Convert.FromBase64String(hash ?? string.Empty).Length != 32
                || Convert.FromBase64String(encodedSalt ?? string.Empty).Length != 16)
                throw new InvalidDataException("La copia contiene una credencial de aplicación no válida.");
            return (hash, salt);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("La copia contiene una credencial de aplicación no válida.", ex);
        }
    }

    private static byte[] DecodeExact(string? value, int expectedLength)
    {
        var decoded = Convert.FromBase64String(value ?? string.Empty);
        return decoded.Length == expectedLength
            ? decoded
            : throw new InvalidDataException("La cabecera criptográfica de la copia no es válida.");
    }

    private static string Truncate(string? value, int maximumLength)
    {
        value ??= string.Empty;
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private sealed class BackupEnvelope
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Kdf { get; set; }
        public int Iterations { get; set; }
        public string? Cipher { get; set; }
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public string? Data { get; set; }
    }
}
