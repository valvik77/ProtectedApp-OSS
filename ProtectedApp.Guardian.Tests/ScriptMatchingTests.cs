using ProtectedApp.Shared;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public class ScriptMatchingTests
{
    private const string Script = @"C:\tools\backup.py";

    [Theory]
    // Full paths, however they are written.
    [InlineData("\"C:\\Windows\\py.exe\" \"C:\\tools\\backup.py\"", null)]
    [InlineData("python C:\\tools\\backup.py --flag 1", null)]
    [InlineData("python C:/tools/backup.py", null)]
    [InlineData("python C:\\TOOLS\\BACKUP.PY", null)]
    [InlineData("python \\\\?\\C:\\tools\\backup.py", null)]
    [InlineData("python --config=C:\\tools\\backup.py", null)]
    // Relative names resolve against the directory the process started in.
    [InlineData("python backup.py", @"C:\tools")]
    [InlineData("python .\\backup.py", @"C:\tools")]
    [InlineData("python ..\\tools\\backup.py", @"C:\work")]
    [InlineData("python \\tools\\backup.py", @"C:\elsewhere")]
    [InlineData("python -c \"exec(open('backup.py').read())\"", @"C:\tools")]
    // Quoting and escapes must not hide the script from the check (Windows parsing rules).
    [InlineData(@"python -X\"" backup.py", @"C:\tools")]
    [InlineData(@"python \""a b\"" backup.py", @"C:\tools")]
    [InlineData(@"python ""say \""hi\"""" backup.py", @"C:\tools")]
    [InlineData(@"python ""C:\other\\"" backup.py", @"C:\tools")]
    [InlineData(@"python ""backup.py""", @"C:\tools")]
    [InlineData(@"python -u ""./backup.py"" ""x y""", @"C:\tools")]
    // A shell string carries the whole command in one argument.
    [InlineData(@"cmd.exe /c ""python backup.py""", @"C:\tools")]
    [InlineData(@"powershell -Command ""python backup.py; pause""", @"C:\tools")]
    [InlineData(@"cmd.exe /c ""echo hi | python .\backup.py""", @"C:\tools")]
    public void RecognizesTheScript(string commandLine, string? workingDirectory) =>
        Assert.True(ProtectedTarget.CommandLineReferences(commandLine, Script, workingDirectory));

    [Theory]
    [InlineData("python backup.py", null)]                       // directory unknown
    [InlineData("python backup.py", @"C:\other")]                // same name, another folder
    [InlineData("python .\\backup.py", @"C:\other")]
    [InlineData("python backup.py", "relative\\dir")]            // unusable directory is ignored
    [InlineData("python -m backup", @"C:\tools")]                // module, not a file path
    [InlineData("python C:\\tmp\\backup.py", @"C:\tools")]       // a copy elsewhere
    [InlineData("python C:\\tools\\backup2.py", @"C:\tools")]
    [InlineData("python C:\\tools\\backup.py.txt", @"C:\tools")]
    [InlineData("python C:\\to*ols\\backup.py", @"C:\tools")]    // wildcard is not the same folder
    [InlineData(@"cmd.exe /c ""python other\backup.py""", @"C:\tools")]
    [InlineData(@"powershell -Command ""python backup2.py; pause""", @"C:\tools")]
    [InlineData(@"python ""not backup.py.txt""", @"C:\tools")]
    [InlineData("", @"C:\tools")]
    [InlineData("   ", @"C:\tools")]
    public void DoesNotRecognizeOtherFiles(string commandLine, string? workingDirectory) =>
        Assert.False(ProtectedTarget.CommandLineReferences(commandLine, Script, workingDirectory));

    [Fact]
    public void RecognizesAQuotedPathWithSpacesRelativeToTheWorkingDirectory() =>
        Assert.True(ProtectedTarget.CommandLineReferences(
            "python \"..\\my tools\\backup.py\"", @"C:\my tools\backup.py", @"C:\work"));

    [Fact]
    public void RecognizesAnExistingEightDotThreePath()
    {
        using var files = EightDotThreeFiles.TryCreate();
        if (files is null) return; // 8.3 names are disabled on this volume: nothing to exercise.

        Assert.True(ProtectedTarget.CommandLineReferences(
            "python " + WindowsCommandLine.Quote(files.ShortScript), files.Script, workingDirectory: null));
    }

    [Fact]
    public void RecognizesAnEightDotThreeNameRelativeToTheWorkingDirectory()
    {
        using var files = EightDotThreeFiles.TryCreate();
        if (files is null) return;

        Assert.True(ProtectedTarget.CommandLineReferences(
            "python " + Path.GetFileName(files.ShortScript), files.Script, files.Directory));
    }

    [Fact]
    public void RecognizesAnEightDotThreeNameInsideAShellString()
    {
        using var files = EightDotThreeFiles.TryCreate();
        if (files is null) return;

        Assert.True(ProtectedTarget.CommandLineReferences(
            $"cmd.exe /c \"python {files.ShortScript}\"", files.Script, workingDirectory: null));
    }

    [Fact]
    public void AnEightDotThreeAliasOfAnotherFileDoesNotMatch()
    {
        using var files = EightDotThreeFiles.TryCreate();
        if (files is null) return;

        Assert.False(ProtectedTarget.CommandLineReferences(
            "python " + files.ShortOtherScript, files.Script, workingDirectory: null));
    }

    [Fact]
    public void NetworkPathsAreNeverLookedUpWhileComparing()
    {
        // Resolving a UNC path from the SYSTEM service would contact whichever server a caller
        // names: about 21 s of blocking when it is unreachable, or an authentication attempt
        // with the computer account when it is not. .NET's own Path.GetFullPath does this for a
        // path containing "~" (it expands 8.3 aliases), so every place a "~" can reach it counts.
        // The addresses are reserved for documentation (RFC 5737) and never answer; each check
        // uses a different one so the SMB client's memory of a failed server cannot hide a lookup.
        var first = Random.Shared.Next(1, 240);
        string Host(int index) => $"192.0.2.{first + index}";

        var stopwatch = Stopwatch.StartNew();
        Assert.False(ProtectedTarget.CommandLineReferences(
            $@"python \\{Host(0)}\share\BACKUP~1.PY", Script, workingDirectory: null));
        Assert.False(ProtectedTarget.CommandLineReferences(
            $@"python \\{Host(1)}\share\backup.py", Script, workingDirectory: null));
        Assert.False(ProtectedTarget.CommandLineReferences(
            "python backup.py", $@"\\{Host(2)}\share\BACKUP~1.PY", @"C:\tools"));
        Assert.False(ProtectedTarget.CommandLineReferences(
            "python BACKUP~1.PY", Script, $@"\\{Host(3)}\sh~1"));
        Assert.False(ProtectedTarget.CommandLineReferences(
            $@"cmd.exe /c ""python \\{Host(4)}\share\BACKUP~1.PY""", Script, workingDirectory: null));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ARuleOnANetworkShareStillMatchesItsOwnPathWithoutContactingTheServer()
    {
        var share = $@"\\192.0.2.{Random.Shared.Next(1, 250)}\share";
        var stopwatch = Stopwatch.StartNew();

        Assert.True(ProtectedTarget.CommandLineReferences($@"python {share}\jobs\..\job.py", $@"{share}\job.py", null));
        Assert.False(ProtectedTarget.CommandLineReferences($@"python {share}\other.py", $@"{share}\job.py", null));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    private sealed class EightDotThreeFiles : IDisposable
    {
        public required string Directory { get; init; }
        public required string Script { get; init; }
        public required string ShortScript { get; init; }
        public required string ShortOtherScript { get; init; }

        // Null when the volume does not create 8.3 names: GetShortPathName then returns the
        // long path unchanged, which would make an assertion pass without testing anything.
        public static EightDotThreeFiles? TryCreate()
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "ProtectedApp long script directory " + Guid.NewGuid().ToString("N"));
            var script = Path.Combine(directory, "backup script.py");
            var other = Path.Combine(directory, "other script.py");
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(script, "# test");
            File.WriteAllText(other, "# test");
            var shortScript = TryGetShortPath(script);
            var shortOther = TryGetShortPath(other);
            if (shortScript is null || shortOther is null || !shortScript.Contains('~') || !shortOther.Contains('~'))
            {
                try { System.IO.Directory.Delete(directory, recursive: true); } catch (IOException) { }
                return null;
            }
            return new EightDotThreeFiles
            {
                Directory = directory, Script = script, ShortScript = shortScript, ShortOtherScript = shortOther
            };
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void OnlyScriptsCanBeReferenced() =>
        Assert.False(ProtectedTarget.CommandLineReferences("app.exe", @"C:\tools\app.exe", @"C:\tools"));

    [Theory]
    [InlineData("python backup.py", @"C:\tools", true, "\"C:\\tools\\backup.py\"")]
    [InlineData("python backup.py --mode full", @"C:\tools", true, "\"C:\\tools\\backup.py\" --mode full")]
    [InlineData("python -u backup.py a \"b c\"", @"C:\tools", true, "-u \"C:\\tools\\backup.py\" a \"b c\"")]
    [InlineData("python -OO -B C:\\tools\\backup.py", null, true, "-OO -B \"C:\\tools\\backup.py\"")]
    [InlineData("py -3.11 -X utf8 C:\\tools\\backup.py x", null, true, "-3.11 -X utf8 \"C:\\tools\\backup.py\" x")]
    [InlineData("py -V:3.12 -Xdev C:\\tools\\backup.py", null, true, "-V:3.12 -Xdev \"C:\\tools\\backup.py\"")]
    [InlineData("python -W error backup.py -c notcode", @"C:\tools", true, "-W error \"C:\\tools\\backup.py\" -c notcode")]
    public void KeepsTheScriptItsArgumentsAndHarmlessOptions(string commandLine, string? workingDirectory,
        bool keepOptions, string expected) =>
        Assert.Equal(expected,
            ProtectedTarget.BuildCanonicalScriptArguments(commandLine, Script, workingDirectory, keepOptions));

    [Theory]
    // Options that execute other code are never carried into the approved relaunch.
    [InlineData("python -c \"import os; os.system('calc')\" C:\\tools\\backup.py")]
    [InlineData("python -m http.server C:\\tools\\backup.py")]
    [InlineData("python -i -c print(1) C:\\tools\\backup.py")]
    [InlineData("python -uc \"print(1)\" C:\\tools\\backup.py")]
    [InlineData("python --unknown C:\\tools\\backup.py")]
    public void DropsOptionsThatRunOtherCode(string commandLine) =>
        Assert.Equal("\"C:\\tools\\backup.py\"",
            ProtectedTarget.BuildCanonicalScriptArguments(commandLine, Script, null, keepInterpreterOptions: true));

    [Fact]
    public void TrailingArgumentsSurviveQuotingUnchanged()
    {
        var canonical = ProtectedTarget.BuildCanonicalScriptArguments(
            @"python backup.py ""he said \""hi\"""" x ""C:\a b\\""", Script, @"C:\tools", keepInterpreterOptions: true);

        // What the approved relaunch passes to the script equals what the caller typed.
        Assert.Equal(new[] { "prog", Script, "he said \"hi\"", "x", @"C:\a b\" },
            ProtectedApp.Shared.WindowsCommandLine.Split("prog " + canonical));
    }

    [Fact]
    public void AnOptionSpelledWithAnEscapedQuoteIsRebuiltExactly()
    {
        var canonical = ProtectedTarget.BuildCanonicalScriptArguments(
            @"python -X\"" backup.py", Script, @"C:\tools", keepInterpreterOptions: true);

        Assert.Equal(new[] { "prog", "-X\"", Script },
            ProtectedApp.Shared.WindowsCommandLine.Split("prog " + canonical));
    }

    [Fact]
    public void ScriptInsideAShellStringIsRunAloneWithoutTrailingText() =>
        Assert.Equal("\"C:\\tools\\backup.py\"", ProtectedTarget.BuildCanonicalScriptArguments(
            @"cmd.exe /c ""python backup.py & calc""", Script, @"C:\tools", keepInterpreterOptions: false));

    [Fact]
    public void ScriptMentionedInsideCodeIsRunAloneWithoutTrailingArguments() =>
        Assert.Equal("\"C:\\tools\\backup.py\"", ProtectedTarget.BuildCanonicalScriptArguments(
            "python -c \"exec(open('backup.py').read())\" extra", Script, @"C:\tools", keepInterpreterOptions: true));

    [Fact]
    public void ShellWrappersAreDroppedWhenOptionsAreNotKept() =>
        Assert.Equal("\"C:\\tools\\backup.py\" arg", ProtectedTarget.BuildCanonicalScriptArguments(
            "cmd.exe /c python C:\\tools\\backup.py arg", Script, null, keepInterpreterOptions: false));

    [Theory]
    [InlineData("python other.py", @"C:\tools")]
    [InlineData("python backup.py", null)]
    [InlineData("", @"C:\tools")]
    public void NoCanonicalArgumentsWhenTheScriptIsNotRun(string commandLine, string? workingDirectory) =>
        Assert.Null(ProtectedTarget.BuildCanonicalScriptArguments(commandLine, Script, workingDirectory, true));

    [Theory]
    [InlineData(@"C:\Python312\python.exe", true)]
    [InlineData(@"C:\Python312\pythonw.exe", true)]
    [InlineData(@"C:\Windows\py.exe", true)]
    [InlineData(@"C:\Windows\pyw.exe", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false)]
    public void IdentifiesPythonInterpreters(string path, bool expected) =>
        Assert.Equal(expected, ProtectedTarget.IsPythonInterpreter(path));

    private static string? TryGetShortPath(string path)
    {
        var required = GetShortPathName(path, null, 0);
        if (required == 0) return null;
        var buffer = new StringBuilder(checked((int)required + 1));
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder? shortPath,
        int shortPathBufferLength);
}
