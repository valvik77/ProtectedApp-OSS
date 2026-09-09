using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ProtectedApp.Services;

public static class VaultActivationRequest
{
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
            command?.SetValue(string.Empty, $"\"{appPath}\" \"%1\"");
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    public static string? TryGetVaultActionPath(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        for (var index = 0; index < values.Length - 1; index++)
        {
            if (!values[index].Equals("--vault-action", StringComparison.OrdinalIgnoreCase)) continue;
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
        if (string.IsNullOrWhiteSpace(commandLineArguments)) return null;
        var commandLine = $"ProtectedApp.exe {commandLineArguments}";
        var argv = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero) return null;
        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            return TryGetVaultPath(arguments);
        }
        finally { LocalFree(argv); }
    }

    public static string? TryGetVaultActionPath(string? commandLineArguments)
    {
        if (string.IsNullOrWhiteSpace(commandLineArguments)) return null;
        var commandLine = $"ProtectedApp.exe {commandLineArguments}";
        var argv = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero) return null;
        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            return TryGetVaultActionPath(arguments);
        }
        finally { LocalFree(argv); }
    }

    public static string? TryGetVaultUnmountDrive(string? commandLineArguments)
    {
        if (string.IsNullOrWhiteSpace(commandLineArguments)) return null;
        var commandLine = $"ProtectedApp.exe {commandLineArguments}";
        var argv = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero) return null;
        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
                arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            return TryGetVaultUnmountDrive(arguments);
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
