using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProtectedApp.Shared;

namespace ProtectedApp.Service;

internal sealed class FolderProtectionService(
    GuardianPolicyStore policyStore,
    GuardianOptions options,
    ILogger<FolderProtectionService> logger) : BackgroundService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProtectedApp.Guardian.FolderAcl.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const FileSystemRights ShellMetadataReadRights = FileSystemRights.ReadData
        | FileSystemRights.ReadAttributes
        | FileSystemRights.ReadExtendedAttributes
        | FileSystemRights.ReadPermissions
        | FileSystemRights.Synchronize;
    // Preserve only the metadata rights needed by Explorer and Directory Opus
    // to resolve desktop.ini. Listing, opening and changing folder contents
    // remain denied while the folder is locked.
    private const FileSystemRights FolderLockDeniedRights = FileSystemRights.FullControl
        & ~(FileSystemRights.ReadAttributes
            | FileSystemRights.ReadExtendedAttributes
            | FileSystemRights.Synchronize);
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _unlockLeases = new(StringComparer.OrdinalIgnoreCase);
    private FolderAclDatabase _database = LoadDatabase();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!TamperState.IsMaintenanceActive()) Synchronize(policyStore.GetPolicies());
            }
            catch (Exception ex) { logger.LogError(ex, "No se pudo reconciliar la protección de carpetas."); }
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    public void ValidatePolicy(IEnumerable<GuardianPolicy> policies)
    {
        foreach (var policy in policies)
        {
            var folders = policy.FolderRules.Where(rule => rule.IsEnabled).ToArray();
            foreach (var rule in folders)
            {
                ValidateFolder(rule.Path, policy.UserSid);
                if (policy.Rules.Any(app => app.IsEnabled && IsInside(app.Path, rule.Path)))
                    throw new InvalidDataException("Una carpeta protegida no puede contener una aplicación o script que también esté protegido.");
            }
            for (var first = 0; first < folders.Length; first++)
            for (var second = first + 1; second < folders.Length; second++)
                if (PathsOverlap(folders[first].Path, folders[second].Path))
                    throw new InvalidDataException("Las carpetas protegidas no pueden contenerse unas dentro de otras.");
        }
    }

    public void Synchronize(IEnumerable<GuardianPolicy> policies)
    {
        lock (_sync)
        {
            var desired = policies
                .SelectMany(policy => policy.FolderRules
                    .Where(rule => rule.IsEnabled)
                    .Select(rule => new DesiredFolder(policy.UserSid, rule)))
                .ToDictionary(item => RecordKey(item.UserSid, item.Rule.Id), StringComparer.OrdinalIgnoreCase);
            ValidatePolicy(policies);

            foreach (var record in _database.Records.ToArray())
            {
                var key = RecordKey(record.UserSid, record.RuleId);
                if (desired.TryGetValue(key, out var item) && PathsEqual(record.Path, item.Rule.Path)) continue;
                Restore(record);
                _database.Records.Remove(record);
                _unlockLeases.Remove(key);
                SaveDatabaseLocked();
            }

            foreach (var (key, item) in desired)
            {
                var record = _database.Records.FirstOrDefault(candidate =>
                    candidate.RuleId == item.Rule.Id && SidEquals(candidate.UserSid, item.UserSid));
                if (record is null)
                {
                    record = Capture(item.UserSid, item.Rule);
                    _database.Records.Add(record);
                    // Persist the recovery descriptor before denying access.
                    SaveDatabaseLocked();
                }

                if (_unlockLeases.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow)
                    continue;
                _unlockLeases.Remove(key);
                if (EnsureLocked(record)) SaveDatabaseLocked();
                EnsureFolderIcon(record);
            }
        }
    }

    public DateTimeOffset Unlock(string userSid, GuardianFolderRule rule)
    {
        lock (_sync)
        {
            var record = FindRecord(userSid, rule.Id)
                ?? throw new InvalidOperationException("Guardian no conserva los permisos originales de la carpeta.");
            if (!Directory.Exists(record.Path))
                throw new DirectoryNotFoundException("La carpeta protegida ya no existe.");
            if (Restore(record)) SaveDatabaseLocked();
            EnsureFolderIcon(record);
            var until = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(rule.UnlockMinutes, 1, 10_080));
            _unlockLeases[RecordKey(userSid, rule.Id)] = until;
            logger.LogInformation("Carpeta desbloqueada temporalmente: {Folder}, SID {Sid}, hasta {Until}.",
                rule.Path, userSid, until);
            return until;
        }
    }

    public void Lock(string userSid, Guid ruleId)
    {
        lock (_sync)
        {
            var record = FindRecord(userSid, ruleId)
                ?? throw new InvalidOperationException("La carpeta protegida no está registrada.");
            _unlockLeases.Remove(RecordKey(userSid, ruleId));
            if (EnsureLocked(record)) SaveDatabaseLocked();
            EnsureFolderIcon(record);
            logger.LogInformation("Carpeta bloqueada: {Folder}, SID {Sid}.", record.Path, userSid);
        }
    }

    public void RestoreOrphanedGuardianLock(string userSid, string candidate)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
                throw new InvalidDataException("La ruta de la carpeta no es válida.");
            var path = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("La carpeta seleccionada no existe.");

            var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Access);
            var sid = new SecurityIdentifier(userSid);
            var locks = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(rule => !rule.IsInherited
                    && rule.IdentityReference == sid
                    && rule.AccessControlType == AccessControlType.Deny
                    && IsFolderLockRights(rule.FileSystemRights)
                    && rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)
                    && rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)
                    && rule.PropagationFlags == PropagationFlags.None)
                .ToArray();
            if (locks.Length == 0) return;

            foreach (var lockRule in locks) security.RemoveAccessRuleSpecific(lockRule);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        }
    }

    public int LockAll(string userSid)
    {
        lock (_sync)
        {
            var records = _database.Records.Where(record => SidEquals(record.UserSid, userSid)).ToArray();
            var metadataChanged = false;
            foreach (var record in records)
            {
                try
                {
                    _unlockLeases.Remove(RecordKey(userSid, record.RuleId));
                    metadataChanged |= EnsureLocked(record);
                    EnsureFolderIcon(record);
                }
                catch (Exception ex) { logger.LogWarning(ex, "No se pudo bloquear la carpeta {Folder}.", record.Path); }
            }
            if (metadataChanged) SaveDatabaseLocked();
            return records.Length;
        }
    }

    public void RestoreAll()
    {
        lock (_sync)
        {
            List<Exception> failures = [];
            foreach (var record in _database.Records.ToArray())
            {
                try
                {
                    Restore(record);
                    _database.Records.Remove(record);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            SaveDatabaseLocked();
            if (failures.Count > 0)
                throw new AggregateException("No se pudieron restaurar todas las carpetas protegidas.", failures);
        }
    }

    public void PrepareForUninstall()
    {
        Directory.CreateDirectory(GuardianConstants.StateFolder);
        File.WriteAllText(GuardianConstants.MaintenancePath, DateTimeOffset.UtcNow.ToString("O"));
        try { RestoreAll(); }
        catch
        {
            try { File.Delete(GuardianConstants.MaintenancePath); } catch { }
            throw;
        }
    }

    private FolderAclRecord Capture(string userSid, GuardianFolderRule rule)
    {
        ValidateFolder(rule.Path, userSid);
        var path = Path.GetFullPath(rule.Path).TrimEnd(Path.DirectorySeparatorChar);
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Access);
        var sid = new SecurityIdentifier(userSid);
        if (security.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(access => access.IdentityReference == sid && access.AccessControlType == AccessControlType.Deny
                && IsFolderLockRights(access.FileSystemRights)))
            throw new InvalidDataException("La carpeta ya contiene un bloqueo total para este usuario; no es seguro reemplazar sus permisos.");
        return new FolderAclRecord
        {
            RuleId = rule.Id,
            UserSid = userSid,
            Path = path,
            OriginalAccessSddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access)
        };
    }

    private static bool EnsureLocked(FolderAclRecord record)
    {
        if (!Directory.Exists(record.Path))
            throw new DirectoryNotFoundException($"La carpeta protegida ya no existe: {record.Path}");
        var current = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(record.Path), AccessControlSections.Access);
        var sid = new SecurityIdentifier(record.UserSid);
        var existingLock = current.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => rule.IdentityReference == sid && rule.AccessControlType == AccessControlType.Deny
                && rule.FileSystemRights == FolderLockDeniedRights
                && rule.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)
                && rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit));
        if (existingLock)
            return EnsureShellMetadataReadable(record);

        // Rebuild from the saved original ACL. This also migrates the legacy
        // FullControl deny rule to the metadata-compatible lock on the next
        // Guardian reconciliation without ever widening content access.
        var locked = SecurityFromSddl(record.OriginalAccessSddl);
        locked.AddAccessRule(new FileSystemAccessRule(sid, FolderLockDeniedRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(record.Path), locked);
        return EnsureShellMetadataReadable(record);
    }

    private static bool Restore(FolderAclRecord record)
    {
        if (!Directory.Exists(record.Path)) return false;
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(record.Path),
            SecurityFromSddl(record.OriginalAccessSddl));
        return RestoreShellMetadataAccess(record);
    }

    private static bool EnsureShellMetadataReadable(FolderAclRecord record)
    {
        if (record.ShellMetadataReadGrantAdded) return false;
        var desktopIniPath = Path.Combine(record.Path, "desktop.ini");
        if (!File.Exists(desktopIniPath)) return false;

        var file = new FileInfo(desktopIniPath);
        var security = FileSystemAclExtensions.GetAccessControl(file, AccessControlSections.Access);
        var sid = new SecurityIdentifier(record.UserSid);
        var alreadyReadable = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => !rule.IsInherited
                && rule.IdentityReference == sid
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & ShellMetadataReadRights) == ShellMetadataReadRights);
        if (alreadyReadable) return false;

        security.AddAccessRule(new FileSystemAccessRule(sid, ShellMetadataReadRights,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(file, security);
        record.ShellMetadataReadGrantAdded = true;
        return true;
    }

    private static bool RestoreShellMetadataAccess(FolderAclRecord record)
    {
        if (!record.ShellMetadataReadGrantAdded) return false;
        var desktopIniPath = Path.Combine(record.Path, "desktop.ini");
        if (!File.Exists(desktopIniPath))
        {
            record.ShellMetadataReadGrantAdded = false;
            return true;
        }

        var file = new FileInfo(desktopIniPath);
        var security = FileSystemAclExtensions.GetAccessControl(file, AccessControlSections.Access);
        security.RemoveAccessRuleSpecific(new FileSystemAccessRule(new SecurityIdentifier(record.UserSid),
            ShellMetadataReadRights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(file, security);
        record.ShellMetadataReadGrantAdded = false;
        return true;
    }

    private void EnsureFolderIcon(FolderAclRecord record)
    {
        try
        {
            var applicationDirectory = Path.GetDirectoryName(options.AppPath);
            if (string.IsNullOrWhiteSpace(applicationDirectory)) return;
            var iconPath = Path.Combine(applicationDirectory, "Assets", "ProtectedFolderIcon.ico");
            var desktopIniPath = Path.Combine(record.Path, "desktop.ini");
            if (!File.Exists(iconPath) || !File.Exists(desktopIniPath)) return;

            var existing = ReadText(desktopIniPath, out var encoding);
            var iconResource = iconPath.Replace("\"", string.Empty);
            var content = UpsertIconResource(existing, iconResource);
            if (string.Equals(existing, content, StringComparison.Ordinal)) return;

            var attributes = File.GetAttributes(desktopIniPath);
            File.SetAttributes(desktopIniPath, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            File.WriteAllText(desktopIniPath, content, encoding ?? new UTF8Encoding(false));
            File.SetAttributes(desktopIniPath, attributes | FileAttributes.Hidden | FileAttributes.System);
            RefreshShell(record.Path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo actualizar el icono de la carpeta protegida {Folder}.", record.Path);
        }
    }

    private static string ReadText(string path, out Encoding? encoding)
    {
        var bytes = File.ReadAllBytes(path);
        encoding = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
            ? Encoding.Unicode
            : bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
                ? Encoding.BigEndianUnicode
                : bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                    : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return encoding.GetString(bytes);
    }

    private static string UpsertIconResource(string existing, string iconResource)
    {
        const string iconResourcePattern = @"(?im)^\s*IconResource\s*=.*$";
        var replacement = "IconResource=" + iconResource + ",0";
        if (Regex.IsMatch(existing, iconResourcePattern))
            return Regex.Replace(existing, iconResourcePattern, replacement);

        return existing.TrimEnd() + Environment.NewLine + "[.ShellClassInfo]" + Environment.NewLine
            + replacement + Environment.NewLine;
    }

    private static void RefreshShell(string path)
    {
        const uint shcneAttributes = 0x00000800;
        const uint shcneUpdateItem = 0x00002000;
        const uint shcneUpdateDir = 0x00001000;
        const uint shcnfPathW = 0x0005;
        const uint shcnfFlush = 0x1000;
        var notificationFlags = shcnfPathW | shcnfFlush;

        SHChangeNotify(shcneUpdateItem, notificationFlags, Path.Combine(path, "desktop.ini"), IntPtr.Zero);
        SHChangeNotify(shcneAttributes, notificationFlags, path, IntPtr.Zero);
        SHChangeNotify(shcneUpdateItem, notificationFlags, path, IntPtr.Zero);

        var parentPath = Directory.GetParent(path)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentPath))
            SHChangeNotify(shcneUpdateDir, notificationFlags, parentPath, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

    private static DirectorySecurity SecurityFromSddl(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        return security;
    }

    private static bool IsFolderLockRights(FileSystemRights rights) =>
        rights == FileSystemRights.FullControl || rights == FolderLockDeniedRights;

    private void ValidateFolder(string candidate, string userSid)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
            throw new InvalidDataException("La ruta de la carpeta no es válida.");
        var path = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("La carpeta seleccionada no existe.");
        var root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || PathsEqual(path, root))
            throw new InvalidDataException("No se puede proteger la raíz de una unidad.");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Solo se pueden proteger carpetas de unidades NTFS locales.");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("No se pueden proteger enlaces, puntos de montaje ni carpetas redirigidas.");

        var critical = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            GuardianConstants.StateFolder,
            Path.GetDirectoryName(options.AppPath) ?? options.AppPath
        }.Where(value => !string.IsNullOrWhiteSpace(value));
        if (critical.Any(value => PathsOverlap(path, value)))
            throw new InvalidDataException("La carpeta contiene o pertenece a un componente crítico de Windows o ProtectedApp.");

        var profile = GetProfilePath(userSid);
        if (!string.IsNullOrWhiteSpace(profile) && PathsEqual(path, profile))
            throw new InvalidDataException("No se puede proteger el perfil de usuario completo; elige una subcarpeta.");
    }

    private static string? GetProfilePath(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            return key?.GetValue("ProfileImagePath") is string value
                ? Environment.ExpandEnvironmentVariables(value)
                : null;
        }
        catch { return null; }
    }

    private FolderAclRecord? FindRecord(string sid, Guid ruleId) => _database.Records.FirstOrDefault(record =>
        record.RuleId == ruleId && SidEquals(record.UserSid, sid));
    private static string RecordKey(string sid, Guid ruleId) => $"{sid}|{ruleId:N}";
    private static bool SidEquals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool PathsOverlap(string left, string right)
    {
        var first = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar);
        var second = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar);
        return PathsEqual(first, second)
            || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsInside(string candidate, string folder)
    {
        var path = Path.GetFullPath(candidate);
        var parent = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static FolderAclDatabase LoadDatabase()
    {
        foreach (var path in new[] { GuardianConstants.FolderAclPath, GuardianConstants.FolderAclBackupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
                var database = JsonSerializer.Deserialize<FolderAclDatabase>(plain, JsonOptions);
                if (database is not null) return database;
            }
            catch { }
        }
        return new FolderAclDatabase();
    }

    private void SaveDatabaseLocked()
    {
        Directory.CreateDirectory(GuardianConstants.PolicyFolder);
        var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_database, JsonOptions), Entropy,
            DataProtectionScope.LocalMachine);
        var temporary = GuardianConstants.FolderAclPath + ".tmp-" + Environment.ProcessId;
        File.WriteAllBytes(temporary, encrypted);
        if (File.Exists(GuardianConstants.FolderAclPath))
            File.Replace(temporary, GuardianConstants.FolderAclPath, GuardianConstants.FolderAclBackupPath, true);
        else
            File.Move(temporary, GuardianConstants.FolderAclPath);
        File.Copy(GuardianConstants.FolderAclPath, GuardianConstants.FolderAclBackupPath, true);
    }

    private sealed record DesiredFolder(string UserSid, GuardianFolderRule Rule);
    private sealed class FolderAclDatabase { public List<FolderAclRecord> Records { get; set; } = []; }
    private sealed class FolderAclRecord
    {
        public Guid RuleId { get; set; }
        public string UserSid { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string OriginalAccessSddl { get; set; } = string.Empty;
        public bool ShellMetadataReadGrantAdded { get; set; }
    }
}
