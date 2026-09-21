using System.Diagnostics;
using ProtectedApp.Shared;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public class ProcessWorkingDirectoryTests
{
    [Fact]
    public void ReadsTheDirectoryOfTheCurrentProcess() =>
        Assert.Equal(Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory),
            ProcessWorkingDirectory.TryGet(Environment.ProcessId));

    [Fact]
    public void ReadsTheDirectoryOfAnotherProcess() =>
        AssertReadsTheStartDirectory(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

    [Fact]
    public void ReadsTheDirectoryOfA32BitProcess()
    {
        var wow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe");
        if (!File.Exists(wow64)) return; // no 32-bit subsystem on this machine
        AssertReadsTheStartDirectory(wow64);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void ReturnsNullWhenTheProcessCannotBeRead(int processId) =>
        Assert.Null(ProcessWorkingDirectory.TryGet(processId));

    private static void AssertReadsTheStartDirectory(string executable)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pa-cwd-" + Guid.NewGuid().ToString("N"), "work dir");
        Directory.CreateDirectory(directory);
        using var process = Process.Start(new ProcessStartInfo(executable, "/c ping -n 30 127.0.0.1 >nul")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                ProcessWorkingDirectory.TryGet(process.Id));
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(5_000);
            try { Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true); } catch (IOException) { }
        }
    }
}
