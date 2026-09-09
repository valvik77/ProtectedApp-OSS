using System;
using System.Text;

namespace ProtectedApp.ShellExtension;

internal static class WindowsCommandLine
{
    // Escapes one argv element using the CommandLineToArgvW/MSVCRT rules.
    // In particular, trailing backslashes must be doubled before the closing quote.
    internal static string Quote(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
