using ProtectedApp.Service;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public sealed class ScriptRelaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pa-relaunch-" + Guid.NewGuid().ToString("N"));
    private string Script => Path.Combine(_directory, "backup.py");
    private const string Python = @"C:\Python312\python.exe";
    private const string Launcher = @"C:\Windows\py.exe";

    public ScriptRelaunchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ReplaysTheInterpreterTheScriptItsArgumentsAndTheStartDirectory()
    {
        var launch = GuardianEnforcer.BuildScriptRelaunch(Python,
            $"\"{Python}\" -u backup.py --mode full", _directory, Script, Launcher);

        Assert.Equal(Python, launch.Executable);
        Assert.Equal($"-u \"{Script}\" --mode full", launch.Arguments);
        Assert.Equal(_directory, launch.WorkingDirectory);
    }

    [Fact]
    public void FallsBackToTheScriptFolderWhenTheStartDirectoryIsGone()
    {
        var launch = GuardianEnforcer.BuildScriptRelaunch(Python,
            $"\"{Python}\" \"{Script}\"", Path.Combine(_directory, "missing"), Script, Launcher);

        Assert.Equal(_directory, launch.WorkingDirectory);
    }

    [Fact]
    public void NeverReplaysCodeThatAnInterceptedCommandLineCarried()
    {
        var launch = GuardianEnforcer.BuildScriptRelaunch(Python,
            $"\"{Python}\" -c \"import os; os.system('calc')\" \"{Script}\"", _directory, Script, Launcher);

        Assert.Equal(Python, launch.Executable);
        Assert.Equal($"\"{Script}\"", launch.Arguments);
    }

    [Fact]
    public void StartsAShellWrappedScriptThroughThePythonLauncher()
    {
        var launch = GuardianEnforcer.BuildScriptRelaunch(@"C:\Windows\System32\cmd.exe",
            "cmd.exe /c python backup.py first", _directory, Script, Launcher);

        Assert.Equal(Launcher, launch.Executable);
        Assert.Equal($"\"{Script}\" first", launch.Arguments);
    }

    [Fact]
    public void WithoutALauncherTheCmdFallbackCarriesNoTrailingArguments()
    {
        // cmd.exe would run anything after "&" as a second command.
        var launch = GuardianEnforcer.BuildScriptRelaunch(@"C:\Windows\System32\cmd.exe",
            "cmd.exe /c python backup.py & calc", _directory, Script, pythonLauncher: null);

        Assert.EndsWith("cmd.exe", launch.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"/d /c python \"{Script}\"", launch.Arguments);
    }
}
