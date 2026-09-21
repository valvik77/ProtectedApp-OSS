using ProtectedApp.Shared;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public class WindowsCommandLineTests
{
    // The worked examples of Microsoft's "Parsing C++ command-line arguments" rules.
    [Theory]
    [InlineData("prog \"abc\" d e", new[] { "prog", "abc", "d", "e" })]
    [InlineData(@"prog a\\b d""e f""g h", new[] { "prog", @"a\\b", "de fg", "h" })]
    [InlineData(@"prog a\\\""b c d", new[] { "prog", @"a\""b", "c", "d" })]
    [InlineData(@"prog a\\\\""b c"" d e", new[] { "prog", @"a\\b c", "d", "e" })]
    // A backslash-escaped quote is a literal quote and does not open a quoted section.
    [InlineData(@"python -X\"" backup.py", new[] { "python", "-X\"", "backup.py" })]
    [InlineData(@"python \""a b\"" backup.py", new[] { "python", "\"a", "b\"", "backup.py" })]
    [InlineData(@"python ""say \""hi\"""" backup.py", new[] { "python", "say \"hi\"", "backup.py" })]
    // Backslashes before a closing quote are literal; the quote still closes the argument.
    [InlineData(@"python ""C:\tools\\"" backup.py", new[] { "python", @"C:\tools\", "backup.py" })]
    public void SplitsLikeWindows(string commandLine, string[] expected) =>
        Assert.Equal(expected, WindowsCommandLine.Split(commandLine));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingToSplit(string? commandLine) => Assert.Empty(WindowsCommandLine.Split(commandLine));

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("tab\there")]
    [InlineData("has\"quote")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData(@"\")]
    [InlineData(@"ends\")]
    [InlineData(@"ends with space\")]
    [InlineData(@"C:\dir with space\")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"a\""b")]
    [InlineData(@"a\\""b")]
    [InlineData("a&b|c;d")]
    [InlineData("unicode-ñ-日本")]
    public void QuoteIsTheInverseOfSplit(string argument) =>
        Assert.Equal(new[] { "prog", argument }, WindowsCommandLine.Split("prog " + WindowsCommandLine.Quote(argument)));

    [Fact]
    public void JoinedArgumentsSplitBackUnchanged()
    {
        string[] arguments = ["-u", @"C:\my tools\backup.py", "he said \"hi\"", "", @"dir\", "a&b"];
        Assert.Equal(new[] { "prog" }.Concat(arguments),
            WindowsCommandLine.Split("prog " + WindowsCommandLine.Join(arguments)));
    }
}
