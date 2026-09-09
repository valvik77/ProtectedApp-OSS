using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ProtectedApp.Service;

internal static class GuardianIntegrity
{
    private const int ManifestVersion = 1;
    private const string GuardianFileName = "ProtectedApp.Guardian.exe";
    private const string GateFileName = "ProtectedApp.Gate.exe";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GuardianIntegrityResult VerifyAndRepair(string guardianPath) =>
        VerifyAndRepair(guardianPath, GuardianConstants.GatePath, GuardianConstants.IntegrityPath,
            GuardianConstants.RecoveryFolder);

    public static GuardianIntegrityResult Verify(string guardianPath) =>
        VerifyAndRepair(guardianPath, GuardianConstants.GatePath, GuardianConstants.IntegrityPath,
            GuardianConstants.RecoveryFolder, allowRepair: false);

    internal static GuardianIntegrityResult VerifyAndRepair(string guardianPath, string gatePath,
        string manifestPath, string recoveryFolder, Func<string, string?>? signerReader = null,
        Func<string?>? expectedSignerReader = null, bool allowRepair = true)
    {
        try
        {
            guardianPath = Path.GetFullPath(guardianPath);
            gatePath = Path.GetFullPath(gatePath);
            manifestPath = Path.GetFullPath(manifestPath);
            recoveryFolder = Path.GetFullPath(recoveryFolder);
            var targets = new[]
            {
                new IntegrityTarget(GuardianFileName, guardianPath),
                new IntegrityTarget(GateFileName, gatePath)
            };
            var manifest = LoadManifest(manifestPath);
            if (manifest is null)
                return new(true, false, "Falta la línea base de integridad. Solo una instalación o actualización autorizada puede crearla.");
            if (manifest.Version != ManifestVersion || manifest.Files.Count != targets.Length)
                return new(true, false, "El manifiesto de integridad de Guardian no es válido.");
            var expectedSigner = NormalizeThumbprint(manifest.SignerThumbprint);
            if (expectedSigner is null)
                return new(true, false, "El manifiesto de integridad no contiene una identidad de firma válida.");
            var installedSigner = NormalizeThumbprint((expectedSignerReader ?? ReadExpectedSigner).Invoke());
            if (!string.Equals(installedSigner, expectedSigner, StringComparison.OrdinalIgnoreCase))
                return new(true, false, "Falta o no coincide la identidad de firma esperada de Guardian.");
            var readSigner = signerReader ?? GetSignerThumbprint;

            var detected = false;
            var repaired = false;
            var failures = new List<string>();
            foreach (var target in targets)
            {
                var expected = manifest.Files.FirstOrDefault(file =>
                    file.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
                if (expected is null || string.IsNullOrWhiteSpace(expected.Sha256))
                {
                    detected = true;
                    failures.Add($"Falta la referencia de {target.Name} en el manifiesto.");
                    continue;
                }
                var signerMatches = string.Equals(readSigner(target.Path), expectedSigner,
                    StringComparison.OrdinalIgnoreCase);
                if (signerMatches && File.Exists(target.Path) && HashesEqual(ComputeHash(target.Path), expected.Sha256)) continue;

                detected = true;
                if (!allowRepair)
                {
                    failures.Add($"{target.Name} no coincide con la línea base de integridad.");
                    continue;
                }
                var recoveryPath = Path.Combine(recoveryFolder, target.Name);
                if (!File.Exists(recoveryPath) || !HashesEqual(ComputeHash(recoveryPath), expected.Sha256))
                {
                    failures.Add($"{target.Name} cambió y no hay una copia verificada para restaurarlo.");
                    continue;
                }
                if (!string.Equals(readSigner(recoveryPath), expectedSigner, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"La copia de recuperación de {target.Name} no conserva la firma esperada.");
                    continue;
                }
                if (!TryRestore(recoveryPath, target.Path) || !HashesEqual(ComputeHash(target.Path), expected.Sha256))
                {
                    failures.Add($"{target.Name} cambió, pero Windows impidió restaurarlo en este momento.");
                    continue;
                }
                repaired = true;
            }

            if (!detected) return new(false, false, null);
            return new(true, repaired && failures.Count == 0,
                failures.Count == 0
                    ? "Se restauraron los binarios protegidos desde una copia verificada."
                    : string.Join(" ", failures));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or CryptographicException or ArgumentException)
        {
            return new(true, false, $"No se pudo comprobar la integridad de Guardian: {ex.Message}");
        }
    }

    private static IntegrityManifest? LoadManifest(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<IntegrityManifest>(File.ReadAllText(path), JsonOptions);
    }

    internal static GuardianIntegrityResult InitializeBaseline(string guardianPath, string gatePath,
        string manifestPath, string recoveryFolder, Func<string, string?>? signerReader = null,
        Func<string?>? expectedSignerReader = null)
    {
        try
        {
            var targets = new[]
            {
                new IntegrityTarget(GuardianFileName, Path.GetFullPath(guardianPath)),
                new IntegrityTarget(GateFileName, Path.GetFullPath(gatePath))
            };
            if (targets.Any(target => !File.Exists(target.Path)))
                return new(true, false, "No se puede crear la línea base: falta un binario de Guardian.");
            var expectedSigner = NormalizeThumbprint((expectedSignerReader ?? ReadExpectedSigner).Invoke());
            if (expectedSigner is null)
                return new(true, false, "No se puede crear la línea base sin una identidad de firma válida.");
            var readSigner = signerReader ?? GetSignerThumbprint;
            if (targets.Any(target => !string.Equals(readSigner(target.Path), expectedSigner, StringComparison.OrdinalIgnoreCase)))
                return new(true, false, "No se puede crear la línea base: los binarios no conservan la firma esperada.");
            CreateBaseline(targets, Path.GetFullPath(manifestPath), Path.GetFullPath(recoveryFolder), expectedSigner);
            return new(false, true, "Se creó una línea base autorizada de integridad de Guardian.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return new(true, false, $"No se pudo crear la línea base de integridad: {ex.Message}");
        }
    }

    private static string? ReadExpectedSigner()
    {
        try
        {
            if (!File.Exists(GuardianConstants.SignerIdentityPath)) return null;
            var thumbprint = new string(File.ReadAllText(GuardianConstants.SignerIdentityPath)
                .Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            return thumbprint.Length == 40 ? thumbprint : null;
        }
        catch { return null; }
    }

    private static string? NormalizeThumbprint(string? value)
    {
        var thumbprint = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return thumbprint.Length == 40 ? thumbprint : null;
    }

    private static string? GetSignerThumbprint(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // This extracts an Authenticode signer from an executable; X509CertificateLoader only loads certificate files.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var thumbprint = new string((certificate.Thumbprint ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            return thumbprint.Length == 40 ? thumbprint : null;
        }
        catch { return null; }
    }

    private static void CreateBaseline(IEnumerable<IntegrityTarget> targets, string manifestPath, string recoveryFolder,
        string? signerThumbprint)
    {
        Directory.CreateDirectory(recoveryFolder);
        var files = new List<IntegrityFile>();
        foreach (var target in targets)
        {
            var recoveryPath = Path.Combine(recoveryFolder, target.Name);
            CopyAtomically(target.Path, recoveryPath);
            files.Add(new IntegrityFile(target.Name, ComputeHash(target.Path)));
        }
        var temporary = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new IntegrityManifest(ManifestVersion, files, signerThumbprint), JsonOptions));
        File.Move(temporary, manifestPath, true);
    }

    private static bool TryRestore(string source, string target)
    {
        try
        {
            CopyAtomically(source, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void CopyAtomically(string source, string target)
    {
        var directory = Path.GetDirectoryName(target) ?? throw new IOException("La ruta de destino no es válida.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.integritytmp");
        try
        {
            File.Copy(source, temporary, true);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HashesEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record IntegrityTarget(string Name, string Path);
    private sealed record IntegrityManifest(int Version, List<IntegrityFile> Files, string? SignerThumbprint = null);
    private sealed record IntegrityFile(string Name, string Sha256);
}

internal sealed record GuardianIntegrityResult(bool Detected, bool Repaired, string? Detail);
