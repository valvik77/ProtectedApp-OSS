using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ProtectedApp.Service;
using ProtectedApp.Shared;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

// Path text that a caller controls reaches the SYSTEM service: a process image path, a command
// line, a string sent over the pipe. Anything that reads the file system for it would contact a
// server named by a UNC path (about 21 s when it does not answer, an authentication attempt with
// the computer account when it does). Each check uses addresses reserved for documentation
// (RFC 5737), which never answer, and a different one each time so the SMB client's memory of a
// failed server cannot hide a lookup.
public class SafePathTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private static string Unreachable(int offset = 0) => $"192.0.2.{Random.Shared.Next(1, 200) + offset}";

    [Fact]
    public void ANetworkPathIsNormalizedWithoutContactingTheServer()
    {
        var first = $@"\\{Unreachable(0)}\share";
        var second = $@"\\{Unreachable(50)}\sh~1";
        var stopwatch = Stopwatch.StartNew();

        // A "~" in the path, and a "~" in the directory a relative name is resolved against.
        Assert.Equal($@"{first}\PROGRA~1\x.py", SafePath.GetFullPath($@"{first}\PROGRA~1\sub\..\x.py"));
        Assert.Equal($@"{second}\y.py", SafePath.GetFullPath("y.py", second));
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void LexicalNormalizationCollapsesDotsAndKeepsTheTildeUntouched() =>
        Assert.Equal(@"C:\b\X~1.PY", SafePath.GetLexicalFullPath(@"C:\a\..\b\.\X~1.PY"));

    [Theory]
    [InlineData(@"\\server\share\x.py", false)]
    [InlineData(@"\\?\C:\x.py", false)]
    [InlineData(@"x.py", false)]
    [InlineData(@"C:", false)]
    [InlineData(@"C:x.py", false)]
    public void OnlyADriveLetterPathCanBeOnAFixedLocalDrive(string path, bool expected) =>
        Assert.Equal(expected, SafePath.IsOnFixedLocalDrive(path));

    [Fact]
    public void TheTemporaryDirectoryIsOnAFixedLocalDrive() =>
        Assert.True(SafePath.IsOnFixedLocalDrive(Path.GetFullPath(Path.GetTempPath())));

    [Fact]
    public void AnExistingEightDotThreeAliasOnALocalDiskIsResolved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ProtectedApp long directory " + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "long file name.py");
        Directory.CreateDirectory(directory);
        File.WriteAllText(file, "#");
        try
        {
            var alias = ShortPath(file);
            if (alias is null || !alias.Contains('~')) return; // 8.3 names are disabled on this volume.

            Assert.Equal(file, SafePath.GetFullPath(alias), ignoreCase: true);
            Assert.Equal(alias, SafePath.GetLexicalFullPath(alias), ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RecognizingAPythonHostNeverProbesANetworkPath()
    {
        var gate = new ExecutionGateManager(new GuardianOptions("ProtectedApp.exe", DiagnosticMode: true),
            NullLogger<ExecutionGateManager>.Instance);
        var stopwatch = Stopwatch.StartNew();

        Assert.False(gate.ObservePythonHost($@"\\{Unreachable(0)}\share\python.exe"));
        Assert.False(gate.IsPythonHost("S-1-5-21-1-2-3-1001", $@"\\{Unreachable(20)}\share\PYTHON~1\python.exe"));
        Assert.Equal([], gate.GetHostAuthorizationFamily("S-1-5-21-1-2-3-1001", $@"\\{Unreachable(40)}\share\python.exe"));
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ARelaunchNeverProbesANetworkStartDirectory()
    {
        var scriptDirectory = Path.Combine(Path.GetTempPath(), "pa-unc-" + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(scriptDirectory, "backup.py");
        var share = $@"\\{Unreachable(0)}\share";
        var stopwatch = Stopwatch.StartNew();

        Assert.Null(GuardianEnforcer.UsableStartDirectory(share));
        var launch = GuardianEnforcer.BuildScriptRelaunch(@"C:\Python312\python.exe",
            $"\"C:\\Python312\\python.exe\" backup.py", share, script, pythonLauncher: null);

        Assert.Equal(scriptDirectory, launch.WorkingDirectory);
        Assert.True(stopwatch.Elapsed < Budget, $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ALocalStartDirectoryIsStillUsedForTheRelaunch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pa-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { Assert.Equal(directory, GuardianEnforcer.UsableStartDirectory(directory)); }
        finally { Directory.Delete(directory); }
    }

    private static string? ShortPath(string path)
    {
        var required = GetShortPathName(path, null, 0);
        if (required == 0) return null;
        var buffer = new StringBuilder(checked((int)required + 1));
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder? shortPath, int shortPathBufferLength);
}
