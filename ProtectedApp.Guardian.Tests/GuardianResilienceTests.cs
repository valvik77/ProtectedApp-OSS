using ProtectedApp.Service;
using System.Security.Principal;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public sealed class GuardianResilienceTests
{
    private const string TestSigner = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void TamperAuditTrail_DetectsDeletedOrAlteredHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp", Guid.NewGuid().ToString("N"));
        var previousOverride = Environment.GetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR", root);
            TamperState.SignalTamper("Prueba de auditoría");
            Assert.True(File.Exists(Path.Combine(root, "Policy", "guardian-tamper-audit.key")));
            Assert.True(File.Exists(Path.Combine(root, "guardian-tamper-audit.json")));
            Assert.True(TamperState.IsAuditTrailValid());

            File.WriteAllText(Path.Combine(root, "guardian-tamper-audit.json"), "[]");
            Assert.False(TamperState.IsAuditTrailValid());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROTECTEDAPP_GUARDIAN_DATA_DIR", previousOverride);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IntegrityBaseline_RestoresMissingGateFromVerifiedRecoveryCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp", Guid.NewGuid().ToString("N"));
        var guardian = Path.Combine(root, "Service", "ProtectedApp.Guardian.exe");
        var gate = Path.Combine(root, "ProtectedApp.Gate.exe");
        var manifest = Path.Combine(root, "guardian-integrity.json");
        var recovery = Path.Combine(root, "Recovery");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guardian)!);
            File.WriteAllText(guardian, "guardian-original");
            File.WriteAllText(gate, "gate-original");

            var baseline = GuardianIntegrity.InitializeBaseline(guardian, gate, manifest, recovery,
                signerReader: _ => TestSigner, expectedSignerReader: () => TestSigner);
            Assert.False(baseline.Detected);
            File.Delete(gate);

            var restored = GuardianIntegrity.VerifyAndRepair(guardian, gate, manifest, recovery,
                signerReader: _ => TestSigner, expectedSignerReader: () => TestSigner);
            Assert.True(restored.Detected);
            Assert.True(restored.Repaired, restored.Detail);
            Assert.Equal("gate-original", File.ReadAllText(gate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IntegrityBaseline_DoesNotRestoreFromModifiedRecoveryCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp", Guid.NewGuid().ToString("N"));
        var guardian = Path.Combine(root, "Service", "ProtectedApp.Guardian.exe");
        var gate = Path.Combine(root, "ProtectedApp.Gate.exe");
        var manifest = Path.Combine(root, "guardian-integrity.json");
        var recovery = Path.Combine(root, "Recovery");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guardian)!);
            File.WriteAllText(guardian, "guardian-original");
            File.WriteAllText(gate, "gate-original");
            _ = GuardianIntegrity.InitializeBaseline(guardian, gate, manifest, recovery,
                signerReader: _ => TestSigner, expectedSignerReader: () => TestSigner);
            File.WriteAllText(gate, "gate-modified");
            File.WriteAllText(Path.Combine(recovery, "ProtectedApp.Gate.exe"), "recovery-modified");

            var result = GuardianIntegrity.VerifyAndRepair(guardian, gate, manifest, recovery,
                signerReader: _ => TestSigner, expectedSignerReader: () => TestSigner);
            Assert.True(result.Detected);
            Assert.False(result.Repaired);
            Assert.Equal("gate-modified", File.ReadAllText(gate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IntegrityBaseline_IsNeverCreatedByAnOrdinaryVerification()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp", Guid.NewGuid().ToString("N"));
        var guardian = Path.Combine(root, "Service", "ProtectedApp.Guardian.exe");
        var gate = Path.Combine(root, "ProtectedApp.Gate.exe");
        var manifest = Path.Combine(root, "guardian-integrity.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guardian)!);
            File.WriteAllText(guardian, "modified-guardian");
            File.WriteAllText(gate, "modified-gate");
            var result = GuardianIntegrity.VerifyAndRepair(guardian, gate, manifest, Path.Combine(root, "Recovery"));
            Assert.True(result.Detected);
            Assert.False(File.Exists(manifest));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IntegrityBaseline_RejectsMissingSignerIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp", Guid.NewGuid().ToString("N"));
        var guardian = Path.Combine(root, "Service", "ProtectedApp.Guardian.exe");
        var gate = Path.Combine(root, "ProtectedApp.Gate.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guardian)!);
            File.WriteAllText(guardian, "guardian-original");
            File.WriteAllText(gate, "gate-original");

            var result = GuardianIntegrity.InitializeBaseline(guardian, gate,
                Path.Combine(root, "guardian-integrity.json"), Path.Combine(root, "Recovery"),
                signerReader: _ => TestSigner, expectedSignerReader: () => null);

            Assert.True(result.Detected);
            Assert.False(result.Repaired);
            Assert.False(File.Exists(Path.Combine(root, "guardian-integrity.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExpectedServiceCommandLine_UsesQuotedGuardianAndApplicationPaths()
    {
        var commandLine = ServiceConfiguration.BuildExpectedBinaryPath(
            @"C:\ProgramData\ProtectedApp\Service\ProtectedApp.Guardian.exe",
            @"C:\Program Files\ProtectedApp\ProtectedApp.exe");

        Assert.Equal(
            "\"C:\\ProgramData\\ProtectedApp\\Service\\ProtectedApp.Guardian.exe\" --app \"C:\\Program Files\\ProtectedApp\\ProtectedApp.exe\"",
            commandLine);
    }

    [Fact]
    public void ExpectedServiceRecoveryPlan_RestartsImmediatelyAndWithBackoff()
    {
        Assert.Equal(new uint[] { 0, 1_000, 5_000 },
            ServiceConfiguration.ExpectedRecoveryDelaysMilliseconds);
    }

    [Fact]
    public void HealthWatchArguments_KeepTheApplicationPathAsOneArgument()
    {
        var arguments = GuardianSystemDiagnostics.BuildHealthWatchArguments(
            @"C:\Program Files\ProtectedApp\ProtectedApp.exe");

        Assert.Equal("--health-watch --app \"C:\\Program Files\\ProtectedApp\\ProtectedApp.exe\"", arguments);
    }

    [Fact]
    [Trait("Category", "SystemIntegration")]
    public void InstalledGuardianSupervisor_IsHealthy_WhenExplicitlyRequestedAndElevated()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PROTECTEDAPP_RUN_SYSTEM_INTEGRATION_TESTS"), "1",
                StringComparison.Ordinal))
            return;
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            return;

        var guardianPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ProtectedApp", "Service", "ProtectedApp.Guardian.exe");
        var appPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ProtectedApp", "ProtectedApp.exe");
        var diagnostic = GuardianSystemDiagnostics.CheckHealthTask(guardianPath, appPath);
        if (!diagnostic.Healthy)
        {
            Assert.True(GuardianSystemDiagnostics.TryRepairHealthTask(guardianPath, appPath, out var repairDetail),
                $"La reparación de la tarea SYSTEM falló: {repairDetail}");
            diagnostic = GuardianSystemDiagnostics.CheckHealthTask(guardianPath, appPath);
        }
        Assert.True(diagnostic.Healthy, diagnostic.Detail);
        Assert.True(File.Exists(guardianPath), $"No se encuentra {guardianPath}");
        Assert.True(File.Exists(appPath), $"No se encuentra {appPath}");
        Assert.True(ServiceConfiguration.IsExpectedConfiguration(guardianPath, appPath),
            "El servicio Guardian no conserva su inicio, comando o recuperación esperados.");
    }
}
