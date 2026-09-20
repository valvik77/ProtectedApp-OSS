using System.Text;
using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class AppDiagnosticLogTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "ProtectedApp.Log.Tests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void AppendTo_RotatesAndNeverExceedsTheLimit()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "launch.log");
        try
        {
            const long limit = 1024;
            var line = new string('x', 99) + "\n";
            for (var index = 0; index < 500; index++)
            {
                AppDiagnosticLog.AppendTo(path, line, limit);
                Assert.True(new FileInfo(path).Length <= limit, $"launch.log exceeded the limit after write {index}");
            }

            // 500 lines x 100 bytes would be ~50 KB without rotation.
            Assert.True(File.Exists(path + ".1"));
            Assert.True(new FileInfo(path + ".1").Length <= limit);
            Assert.False(File.Exists(path + ".2"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void AppendTo_TruncatesASingleEntryLargerThanTheLimit()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "crash.log");
        try
        {
            const long limit = 1024;
            AppDiagnosticLog.AppendTo(path, "before\n", limit);
            AppDiagnosticLog.AppendTo(path, "BEGIN" + new string('y', 10 * (int)limit) + "END", limit);

            var written = File.ReadAllText(path);
            Assert.True(new FileInfo(path).Length <= limit);
            Assert.StartsWith("BEGIN", written, StringComparison.Ordinal);
            Assert.EndsWith(AppDiagnosticLog.TruncationMarker, written, StringComparison.Ordinal);
            Assert.DoesNotContain("END", written.Replace(AppDiagnosticLog.TruncationMarker, string.Empty), StringComparison.Ordinal);
            // The earlier entry was rotated out, not lost or overwritten.
            Assert.Equal("before\n", File.ReadAllText(path + ".1"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void AppendTo_NeverSplitsAMultiByteCharacterWhenTruncating()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "crash.log");
        try
        {
            // Every character is two bytes in UTF-8, so a cut can land mid-character
            // for either parity of the byte budget.
            foreach (var limit in new long[] { 1000, 1001, 1002, 1003 })
            {
                if (File.Exists(path)) File.Delete(path);
                AppDiagnosticLog.AppendTo(path, new string('é', 5000), limit);

                var bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length <= limit, $"limit {limit}: {bytes.Length} bytes");
                var decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                Assert.DoesNotContain('�', decoded);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void AppendTo_CreatesTheFolderAndPreservesContentBelowTheLimit()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "nested", "crash.log");
        try
        {
            AppDiagnosticLog.AppendTo(path, "first\n");
            AppDiagnosticLog.AppendTo(path, "second\n");

            Assert.Equal("first\nsecond\n", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".1"));
        }
        finally { Cleanup(root); }
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
