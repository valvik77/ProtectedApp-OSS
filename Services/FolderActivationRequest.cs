using System.Runtime.InteropServices;

namespace ProtectedApp.Services;

public static class FolderActivationRequest
{
    public const string Argument = "--open-folder";
    public const string ActionArgument = "--folder-action";

    public static string? TryGetFolderPath(IEnumerable<string> arguments)
        => TryGetArgumentPath(arguments, Argument);

    public static string? TryGetFolderActionPath(IEnumerable<string> arguments)
        => TryGetArgumentPath(arguments, ActionArgument);

    private static string? TryGetArgumentPath(IEnumerable<string> arguments, string expectedArgument)
    {
        var values = arguments.ToArray();
        for (var index = 0; index < values.Length - 1; index++)
        {
            if (!values[index].Equals(expectedArgument, StringComparison.OrdinalIgnoreCase)) continue;
            return Normalize(values[index + 1]);
        }
        return null;
    }

    public static string? TryGetFolderPath(string? commandLineArguments)
        => TryGetArgumentPath(commandLineArguments, Argument);

    public static string? TryGetFolderActionPath(string? commandLineArguments)
        => TryGetArgumentPath(commandLineArguments, ActionArgument);

    private static string? TryGetArgumentPath(string? commandLineArguments, string expectedArgument)
    {
        if (string.IsNullOrWhiteSpace(commandLineArguments)) return null;
        var commandLine = $"ProtectedApp.exe {commandLineArguments}";
        var argv = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero) return null;
        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                var value = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(value) ?? string.Empty;
            }
            return TryGetArgumentPath(arguments, expectedArgument);
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
