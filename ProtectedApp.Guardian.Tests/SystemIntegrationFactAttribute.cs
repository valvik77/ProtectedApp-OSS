using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

/// <summary>
/// Marks a test that only means anything against a real installation: the
/// Guardian service, its SYSTEM supervisor or Dokany must already be present.
/// </summary>
/// <remarks>
/// These tests used to start with an <c>if (...) return;</c> guard, which xunit
/// reports as <em>passed</em>. The suite therefore claimed 195 passing tests
/// while several of them had asserted nothing, so a green run expressed more
/// confidence than it had earned. Setting <see cref="FactAttribute.Skip"/>
/// instead makes the run report them as skipped, which is what they are.
/// </remarks>
public sealed class SystemIntegrationFactAttribute : FactAttribute
{
    public const string EnableVariable = "PROTECTEDAPP_RUN_SYSTEM_INTEGRATION_TESTS";

    public SystemIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            Skip = $"Requires a real installation: set {EnableVariable}=1 to run it.";
        else if (!IsElevated())
            Skip = $"{EnableVariable}=1 is set, but this process is not elevated.";
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

/// <summary>
/// Marks a test that needs 8.3 short-name aliases, which a volume can have
/// disabled. The condition is probed once per run.
/// </summary>
/// <remarks>
/// Like <see cref="SystemIntegrationFactAttribute"/>, this replaces an
/// <c>if (... is null) return;</c> guard that xunit reported as a pass.
/// </remarks>
public sealed class EightDotThreeFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public EightDotThreeFactAttribute()
    {
        if (!Available.Value)
            Skip = "8.3 short names are disabled on this volume, so the alias cannot be exercised.";
    }

    private static bool Probe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ProtectedApp 8dot3 probe " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "probe file name.py");
            File.WriteAllText(file, "#");
            var buffer = new StringBuilder(1024);
            return GetShortPathName(file, buffer, buffer.Capacity) != 0 && buffer.ToString().Contains('~');
        }
        catch { return false; }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int capacity);
}

/// <summary>
/// Marks a test that needs the 32-bit (WOW64) subsystem, which is absent on
/// some Windows installations. Replaces a guard that xunit reported as a pass.
/// </summary>
public sealed class Wow64FactAttribute : FactAttribute
{
    public Wow64FactAttribute()
    {
        var wow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe");
        if (!File.Exists(wow64))
            Skip = @"This machine has no 32-bit subsystem (SysWOW64\cmd.exe is absent).";
    }
}
