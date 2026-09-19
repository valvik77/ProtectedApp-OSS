using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ProtectedApp.Services;

public static class VaultActivationRequest
{
    private const int MaximumCommandLineArguments = 4_096;

    public static void EnsureFileAssociation()
    {
        try
        {
            var appPath = Path.Combine(AppContext.BaseDirectory, "ProtectedApp.exe");
            if (!File.Exists(appPath)) return;

            using var extension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pavault");
            extension?.SetValue(string.Empty, "ProtectedApp.Vault");
            using var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ProtectedApp.Vault");
            progId?.SetValue(string.Empty, "Bóveda cifrada de ProtectedApp");
            using var shell = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ProtectedApp.Vault\shell");
            shell?.SetValue(string.Empty, "open");
            using var command = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ProtectedApp.Vault\shell\open\command");
            command?.SetValue(string.Empty, $"\"{appPath}\" --open-vault \"%1\"");
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    public static string? TryGetVaultActionPath(IEnumerable<string> arguments)
        => TryGetPathAfterArgument(arguments, "--vault-action");

    public static string? TryGetVaultOpenPath(IEnumerable<string> arguments)
        => TryGetPathAfterArgument(arguments, "--open-vault");

    private static string? TryGetPathAfterArgument(IEnumerable<string> arguments, string marker)
    {
        var values = arguments.ToArray();
        for (var index = 0; index < values.Length - 1; index++)
        {
            if (!values[index].Equals(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Normalize(values[index + 1]);
            return path is not null && path.EndsWith(".pavault", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        return null;
    }

    public static string? TryGetVaultUnmountDrive(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        for (var index = 0; index < values.Length - 1; index++)
        {
            if (!values[index].Equals("--unmount-vault-drive", StringComparison.OrdinalIgnoreCase)) continue;
            return NormalizeDriveRoot(values[index + 1]);
        }
        return null;
    }

    public static string? TryGetVaultPath(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments.Skip(1))
        {
            var path = Normalize(argument);
            if (path is not null && path.EndsWith(".pavault", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    public static string? TryGetVaultPath(string? commandLineArguments)
    {
        var arguments = ParseCommandLineArguments(commandLineArguments);
        return arguments is null ? null : TryGetVaultPath(arguments);
    }

    public static string? TryGetVaultActionPath(string? commandLineArguments)
    {
        var arguments = ParseCommandLineArguments(commandLineArguments);
        return arguments is null ? null : TryGetVaultActionPath(arguments);
    }

    public static string? TryGetVaultOpenPath(string? commandLineArguments)
    {
        var arguments = ParseCommandLineArguments(commandLineArguments);
        return arguments is null ? null : TryGetVaultOpenPath(arguments);
    }

    public static string? TryGetVaultUnmountDrive(string? commandLineArguments)
    {
        var arguments = ParseCommandLineArguments(commandLineArguments);
        return arguments is null ? null : TryGetVaultUnmountDrive(arguments);
    }

    private static string[]? ParseCommandLineArguments(string? commandLineArguments)
    {
        if (string.IsNullOrWhiteSpace(commandLineArguments)) return null;
        var commandLine = $"ProtectedApp.exe {commandLineArguments}";
        var argv = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero) return null;
        try
        {
            if (argumentCount is <= 0 or > MaximumCommandLineArguments) return null;
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            return arguments;
        }
        finally { LocalFree(argv); }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? NormalizeDriveRoot(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null) return null;
        var root = Path.GetPathRoot(normalized);
        return !string.IsNullOrWhiteSpace(root)
            && string.Equals(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            ? root
            : null;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
