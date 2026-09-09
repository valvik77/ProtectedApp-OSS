using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProtectedApp.Services;

public sealed record ManualUpdatePackage(
    string FilePath,
    Version InstalledVersion,
    Version PackageVersion,
    string SignerName,
    string SignerThumbprint,
    string Sha256);

public sealed record ManualUpdateValidationResult(
    bool Success,
    ManualUpdatePackage? Package,
    string? Error)
{
    public static ManualUpdateValidationResult Failed(string error) => new(false, null, error);
    public static ManualUpdateValidationResult Accepted(ManualUpdatePackage package) => new(true, package, null);
}

public static class ManualUpdateService
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{5F36D8C4-0E13-4ED5-9A28-5881F65735A4}_is1";
    private static readonly Regex VersionPattern = new(@"(?<!\d)(\d+\.\d+\.\d+(?:\.\d+)?)(?!\d)", RegexOptions.Compiled);
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static Version GetInstalledVersion()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(UninstallKey);
                var displayVersion = key?.GetValue("DisplayVersion")?.ToString();
                if (TryParseVersion(displayVersion, out var version)) return version;
                var displayName = key?.GetValue("DisplayName")?.ToString();
                if (TryParseVersion(displayName, out version)) return version;
            }
            catch { }
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            if (TryParseVersion(info.ProductVersion, out var version)
                || TryParseVersion(info.FileVersion, out version)) return version;
        }
        return typeof(ManualUpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    public static string GetDisplayVersion(Version version) => version.Revision > 0
        ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
        : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    public static string? GetCurrentSignerThumbprint()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return null;
        try
        {
#pragma warning disable SYSLIB0057 // This extracts an Authenticode signer from an executable; X509CertificateLoader only loads certificate files.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executable));
#pragma warning restore SYSLIB0057
            return NormalizeThumbprint(certificate.Thumbprint);
        }
        catch { return null; }
    }

    public static Task<ManualUpdateValidationResult> ValidatePackageAsync(
        string installerPath,
        string? expectedSignerThumbprint = null,
        Version? installedVersion = null) => Task.Run(() =>
            ValidatePackage(installerPath, expectedSignerThumbprint, installedVersion));

    /// <summary>
    /// Copies an already reviewed package into a directory writable only by the
    /// current user, SYSTEM and administrators, then validates that exact copy
    /// again.  Never elevate a file selected from Downloads, a network share or
    /// another mutable location: it could have changed after the initial review.
    /// </summary>
    public static async Task<ManualUpdateValidationResult> StageValidatedPackageAsync(ManualUpdatePackage package)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        var directory = GetStagingDirectory();
        Directory.CreateDirectory(directory);
        HardenStagingDirectory(directory);
        DeleteExpiredStagedPackages(directory);

        var stagedPath = Path.Combine(directory, $"protectedapp-update-{Guid.NewGuid():N}.exe");
        var stagedValidated = false;
        try
        {
            await using (var source = new FileStream(package.FilePath, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination);
                await destination.FlushAsync();
                destination.Flush(flushToDisk: true);
            }

            var validation = await ValidatePackageAsync(stagedPath, package.SignerThumbprint, package.InstalledVersion);
            if (!validation.Success || validation.Package is null
                || !string.Equals(validation.Package.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase)
                || validation.Package.PackageVersion != package.PackageVersion
                || !string.Equals(validation.Package.SignerThumbprint, package.SignerThumbprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ManualUpdateValidationResult.Failed(
                    "El instalador cambió después de su comprobación inicial y no se iniciará.");
            }
            stagedValidated = true;
            return validation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ManualUpdateValidationResult.Failed($"No se pudo preparar una copia segura del instalador: {ex.Message}");
        }
        finally
        {
            // A successfully staged package is retained until its elevated setup
            // has started. Old packages are removed on subsequent staging runs.
            if (!stagedValidated && File.Exists(stagedPath))
            {
                try { File.Delete(stagedPath); } catch { }
            }
        }
    }

    private static string GetStagingDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp", "UpdateStaging");

    private static void HardenStagingDirectory(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("No se pudo identificar al usuario de la actualización.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[]
                 {
                     user,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        }
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    private static void DeleteExpiredStagedPackages(string directory)
    {
        foreach (var candidate in Directory.EnumerateFiles(directory, "protectedapp-update-*.exe"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) > TimeSpan.FromDays(1))
                    File.Delete(candidate);
            }
            catch { /* An installer still running retains its copy. */ }
        }
    }

    public static ManualUpdateValidationResult ValidatePackage(
        string installerPath,
        string? expectedSignerThumbprint = null,
        Version? installedVersion = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
                return ManualUpdateValidationResult.Failed("El instalador seleccionado no existe.");
            var fullPath = Path.GetFullPath(installerPath);
            if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return ManualUpdateValidationResult.Failed("Selecciona un instalador de ProtectedApp en formato .exe.");

            var trustedThumbprint = NormalizeThumbprint(expectedSignerThumbprint) ?? GetCurrentSignerThumbprint();
            if (string.IsNullOrWhiteSpace(trustedThumbprint))
                return ManualUpdateValidationResult.Failed(
                    "No se puede establecer la identidad de firma de esta instalación de ProtectedApp.");

            var trustStatus = VerifyAuthenticodeTrust(fullPath);
            if (trustStatus != 0)
                return ManualUpdateValidationResult.Failed(GetTrustError(trustStatus));

#pragma warning disable SYSLIB0057 // This extracts an Authenticode signer from an executable; X509CertificateLoader only loads certificate files.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0057
            var signerThumbprint = NormalizeThumbprint(signer.Thumbprint);
            if (!string.Equals(signerThumbprint, trustedThumbprint, StringComparison.OrdinalIgnoreCase))
                return ManualUpdateValidationResult.Failed(
                    "El instalador está firmado, pero no por el mismo certificado que esta copia de ProtectedApp.");

            var versionInfo = FileVersionInfo.GetVersionInfo(fullPath);
            if (!string.Equals(versionInfo.ProductName?.Trim(), "ProtectedApp", StringComparison.Ordinal)
                || !string.Equals(versionInfo.FileDescription?.Trim(), "Instalador de ProtectedApp", StringComparison.Ordinal))
                return ManualUpdateValidationResult.Failed("El archivo firmado no es un instalador válido de ProtectedApp.");
            if (!TryParseVersion(versionInfo.ProductVersion, out var packageVersion)
                && !TryParseVersion(versionInfo.FileVersion, out packageVersion))
                return ManualUpdateValidationResult.Failed("El instalador no contiene una versión válida.");

            var currentVersion = installedVersion ?? GetInstalledVersion();
            if (packageVersion <= currentVersion)
                return ManualUpdateValidationResult.Failed(
                    $"La versión {GetDisplayVersion(packageVersion)} no es posterior a la instalada ({GetDisplayVersion(currentVersion)}).");

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return ManualUpdateValidationResult.Accepted(new ManualUpdatePackage(
                fullPath,
                currentVersion,
                packageVersion,
                signer.GetNameInfo(X509NameType.SimpleName, false),
                signerThumbprint ?? string.Empty,
                sha256));
        }
        catch (CryptographicException)
        {
            return ManualUpdateValidationResult.Failed("No se pudo leer una firma Authenticode válida del instalador.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ManualUpdateValidationResult.Failed($"No se pudo comprobar el instalador: {ex.Message}");
        }
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = VersionPattern.Match(value.Trim());
        return match.Success && Version.TryParse(match.Groups[1].Value, out version!);
    }

    private static string? NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        var normalized = new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 40 ? normalized : null;
    }

    private static int VerifyAuthenticodeTrust(string filePath)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                // Do not accept a package whose signing certificate was revoked
                // after a compromise. The package is a subsequent UAC target.
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                // Permit Windows to retrieve current revocation information;
                // cache-only validation would accept an obsolete certificate
                // status when the installer is about to be elevated.
                ProviderFlags = 0,
                UiContext = 0
            };
            return WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref trustData);
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static string GetTrustError(int status) => unchecked((uint)status) switch
    {
        0x80096010 => "La firma del instalador no coincide con su contenido; el archivo puede haber sido modificado.",
        0x800B0100 => "El instalador no contiene una firma Authenticode.",
        0x800B0101 => "El certificado del instalador ha caducado y no dispone de un sello de tiempo válido.",
        0x800B0109 => "Windows no confía en la cadena del certificado que firma el instalador.",
        _ => $"Windows rechazó la firma Authenticode del instalador (0x{unchecked((uint)status):X8})."
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);
}
