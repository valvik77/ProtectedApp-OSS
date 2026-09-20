using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class AppDiagnosticLogTests
{
    [Fact]
    public void AppendTo_RotatesAtTheLimitAndKeepsDiskUseBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp.Log.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "launch.log");
        try
        {
            const long limit = 1024;
            var line = new string('x', 99) + "\n";
            for (var index = 0; index < 500; index++)
                AppDiagnosticLog.AppendTo(path, line, limit);

            // 500 lines x 100 bytes would be ~50 KB without rotation.
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            Assert.True(new FileInfo(path).Length <= limit + line.Length);
            Assert.True(new FileInfo(path + ".1").Length <= limit + line.Length);
            Assert.False(File.Exists(path + ".2"));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AppendTo_CreatesTheFolderAndPreservesContentBelowTheLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProtectedApp.Log.Tests", Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(root, "crash.log");
        try
        {
            AppDiagnosticLog.AppendTo(path, "first\n");
            AppDiagnosticLog.AppendTo(path, "second\n");

            Assert.Equal("first\nsecond\n", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".1"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AppendTo_NeverThrowsWhenThePathIsUnusable()
    {
        // A file where a directory is required makes Directory.CreateDirectory fail.
        var blocker = Path.Combine(Path.GetTempPath(), "ProtectedApp.Log.Tests." + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var exception = Record.Exception(() =>
                AppDiagnosticLog.AppendTo(Path.Combine(blocker, "launch.log"), "ignored\n"));
            Assert.Null(exception);
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }
}
