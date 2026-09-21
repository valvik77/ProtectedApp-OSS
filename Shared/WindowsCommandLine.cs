using System.Runtime.InteropServices;
using System.Text;

namespace ProtectedApp.Shared;

/// <summary>
/// Splits and joins command lines with the rules Windows itself uses (CommandLineToArgvW,
/// which the C runtime, .NET and Python follow). A protection decision must see the same
/// arguments the launched program will see: a hand-written splitter that treats a
/// backslash-escaped quote differently lets a crafted command line hide a script name.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary>The arguments of <paramref name="commandLine"/>; the first is the program.</summary>
    public static string[] Split(string? commandLine)
    {
        // CommandLineToArgvW answers an empty string with the path of the calling process.
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            return arguments;
        }
        finally { LocalFree(argv); }
    }

    /// <summary>One argument, escaped so that <see cref="Split"/> returns exactly it.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;
        var result = new StringBuilder("\"");
        for (var index = 0; ; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                index++;
                backslashes++;
            }
            if (index == argument.Length)
            {
                // Backslashes before the closing quote must not escape it.
                result.Append('\\', backslashes * 2);
                break;
            }
            if (argument[index] == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
            else result.Append('\\', backslashes).Append(argument[index]);
        }
        return result.Append('"').ToString();
    }

    /// <summary>The arguments joined into one command line.</summary>
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
