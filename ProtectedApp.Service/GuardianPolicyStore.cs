using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProtectedApp.Shared;

namespace ProtectedApp.Service;

internal sealed class GuardianPolicyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProtectedApp.Guardian.Policy.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private GuardianPolicyDatabase _database;

    public GuardianPolicyStore()
    {
        _database = Load();
        RemoveLegacyFolderRules();
        if (_database.Policies.Count > 0) WriteProtectionVersion();
    }

    public bool HasPolicy(string userSid)
    {
        lock (_sync) return _database.Policies.Any(p => SidEquals(p.UserSid, userSid));
    }

    public bool ValidateBootstrapSecret(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var expected = File.ReadAllText(GuardianConstants.BootstrapSecretPath).Trim();
            var left = Encoding.UTF8.GetBytes(expected);
            var right = Encoding.UTF8.GetBytes(candidate);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch { return false; }
    }

    public void ClearBootstrapSecret()
    {
        try { File.Delete(GuardianConstants.BootstrapSecretPath); }
        catch { }
    }

    public GuardianPolicy? GetPolicy(string userSid)
    {
        lock (_sync)
        {
            var policy = _database.Policies.FirstOrDefault(p => SidEquals(p.UserSid, userSid));
            return policy is null ? null : Clone(policy);
        }
    }

    public IReadOnlyList<GuardianPolicy> GetPolicies()
    {
        lock (_sync) return _database.Policies.Select(Clone).ToArray();
    }

    public bool MayProtectProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        lock (_sync)
        {
            return _database.Policies.Any(policy => policy.Rules.Any(rule =>
                rule.IsEnabled &&
                (string.Equals(Path.GetFileName(rule.Path), processName, StringComparison.OrdinalIgnoreCase)
                 || ProtectedTarget.IsScript(rule.Path) && ProtectedTarget.IsPotentialScriptHost(processName))));
        }
    }

    public int GetScanIntervalMilliseconds()
    {
        lock (_sync)
        {
            return _database.Policies.Count == 0
                ? 500
                : _database.Policies.Min(policy => Math.Clamp(policy.ScanIntervalMilliseconds, 500, 5000));
        }
    }

    public void SetPolicy(GuardianPolicy policy)
    {
        Validate(policy);
        lock (_sync)
        {
            var index = _database.Policies.FindIndex(p => SidEquals(p.UserSid, policy.UserSid));
            var copy = Clone(policy);
            if (index >= 0) _database.Policies[index] = copy;
            else _database.Policies.Add(copy);
            SaveLocked();
            WriteProtectionVersion();
        }
    }

    public void RemovePolicy(string userSid)
    {
        lock (_sync)
        {
            _database.Policies.RemoveAll(policy => SidEquals(policy.UserSid, userSid));
            SaveLocked();
        }
    }

    private static void Validate(GuardianPolicy policy)
    {
        if (!policy.UserSid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SID de usuario no válido.");
        if (string.IsNullOrWhiteSpace(policy.MasterPasswordHash) || string.IsNullOrWhiteSpace(policy.MasterPasswordSalt))
            throw new InvalidDataException("La política no contiene una contraseña maestra válida.");
        if (policy.Rules.Count > 1000) throw new InvalidDataException("La política contiene demasiadas reglas.");
        // Folder ACL locking was retired in favour of encrypted vaults. Ignore
        // legacy or crafted folder rules so Guardian can only restore old ACLs,
        // never create a new lock.
        policy.FolderRules.Clear();
        policy.ScanIntervalMilliseconds = Math.Clamp(policy.ScanIntervalMilliseconds, 500, 5000);
        foreach (var rule in policy.Rules)
        {
            if (rule.Id == Guid.Empty || string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Path))
                throw new InvalidDataException("Regla incompleta.");
            rule.Path = Path.GetFullPath(rule.Path);
            if (!Path.IsPathRooted(rule.Path) || !ProtectedTarget.IsSupported(rule.Path))
                throw new InvalidDataException("Solo se pueden proteger archivos .exe, .bat y .py.");
            if (IsCriticalWindowsExecutable(rule.Path)
                || new[] { "ProtectedApp.exe", "ProtectedApp.Guardian.exe", "ProtectedApp.Gate.exe" }
                    .Contains(Path.GetFileName(rule.Path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("No se pueden proteger componentes esenciales de Windows o de Guardian.");
            rule.UnlockGraceMinutes = Math.Clamp(rule.UnlockGraceMinutes, 0, 10_080);
            rule.ForceCloseAfterMinutes = Math.Clamp(rule.ForceCloseAfterMinutes, 0, 10_080);
            rule.ForceCloseAfterInactivityMinutes = Math.Clamp(rule.ForceCloseAfterInactivityMinutes, 0, 10_080);
            if (rule.ForceCloseAfterMinutes > 0) rule.ForceCloseAfterInactivityMinutes = 0;
            var closeLimit = Math.Max(rule.ForceCloseAfterMinutes, rule.ForceCloseAfterInactivityMinutes);
            if (closeLimit > 0 && rule.UnlockGraceMinutes > closeLimit)
                rule.UnlockGraceMinutes = closeLimit;
            rule.ScheduleDays &= (int)ScheduleDays.EveryDay;
            rule.ScheduleStartMinutes = Math.Clamp(rule.ScheduleStartMinutes, 0, 1_439);
            rule.ScheduleEndMinutes = Math.Clamp(rule.ScheduleEndMinutes, 0, 1_439);
            if (rule.ScheduleEnabled && rule.ScheduleDays == 0)
                throw new InvalidDataException("Un horario activo debe incluir al menos un día.");
        }
    }

    private static bool IsCriticalWindowsExecutable(string path)
    {
        var name = Path.GetFileName(path);
        if (name is null || !new[] { "csrss.exe", "lsass.exe", "services.exe", "smss.exe", "wininit.exe", "winlogon.exe" }
            .Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase);
    }

    private GuardianPolicyDatabase Load()
    {
        try
        {
            if (!File.Exists(GuardianConstants.PolicyPath)) return new GuardianPolicyDatabase();
            var encrypted = File.ReadAllBytes(GuardianConstants.PolicyPath);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<GuardianPolicyDatabase>(json, JsonOptions) ?? new GuardianPolicyDatabase();
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or InvalidDataException)
        {
            try
            {
                var corrupt = GuardianConstants.PolicyPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(GuardianConstants.PolicyPath, corrupt, true);
            }
            catch { }
            return new GuardianPolicyDatabase();
        }
    }

    private void RemoveLegacyFolderRules()
    {
        if (!_database.Policies.Any(policy => policy.FolderRules.Count > 0)) return;
        foreach (var policy in _database.Policies) policy.FolderRules.Clear();
        SaveLocked();
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(GuardianConstants.PolicyFolder);
        var json = JsonSerializer.SerializeToUtf8Bytes(_database, JsonOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.LocalMachine);
        var temp = GuardianConstants.PolicyPath + ".tmp-" + Environment.ProcessId;
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, GuardianConstants.PolicyPath, true);
    }

    private static void WriteProtectionVersion()
    {
        Directory.CreateDirectory(GuardianConstants.StateFolder);
        File.WriteAllText(GuardianConstants.VersionPath, GuardianConstants.ProtectionVersion);
    }

    private static GuardianPolicy Clone(GuardianPolicy policy) => new()
    {
        UserSid = policy.UserSid,
        Revision = policy.Revision,
        MasterPasswordHash = policy.MasterPasswordHash,
        MasterPasswordSalt = policy.MasterPasswordSalt,
        ScanIntervalMilliseconds = policy.ScanIntervalMilliseconds,
        CloseWarningNotificationsEnabled = policy.CloseWarningNotificationsEnabled,
        Rules = policy.Rules.Select(rule => new GuardianRule
        {
            Id = rule.Id,
            Name = rule.Name,
            Path = rule.Path,
            Category = string.IsNullOrWhiteSpace(rule.Category) ? "General" : rule.Category,
            IsEnabled = rule.IsEnabled,
            PasswordHash = rule.PasswordHash,
            PasswordSalt = rule.PasswordSalt,
            UnlockGraceMinutes = rule.UnlockGraceMinutes,
            ForceCloseAfterMinutes = rule.ForceCloseAfterMinutes,
            ForceCloseAfterInactivityMinutes = rule.ForceCloseAfterInactivityMinutes,
            ScheduleEnabled = rule.ScheduleEnabled,
            ScheduleDays = rule.ScheduleDays,
            ScheduleStartMinutes = rule.ScheduleStartMinutes,
            ScheduleEndMinutes = rule.ScheduleEndMinutes,
            BlockOutsideSchedule = rule.BlockOutsideSchedule
        }).ToList(),
        FolderRules = policy.FolderRules.Select(folder => new GuardianFolderRule
        {
            Id = folder.Id,
            Name = folder.Name,
            Path = folder.Path,
            IsEnabled = folder.IsEnabled,
            PasswordHash = folder.PasswordHash,
            PasswordSalt = folder.PasswordSalt,
            UnlockMinutes = folder.UnlockMinutes
        }).ToList()
    };

    private static bool SidEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
