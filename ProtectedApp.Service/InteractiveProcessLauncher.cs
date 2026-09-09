using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ProtectedApp.Service;

internal static class InteractiveProcessLauncher
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const uint TokenAllAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    public static uint? GetActiveSessionId()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        return sessionId == InvalidSessionId ? null : sessionId;
    }

    public static string? GetSessionUserSid(uint sessionId)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(sessionId, out token)) return null;
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch { return null; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    public static bool StartInSession(string executable, string arguments, uint sessionId,
        out uint processId, out int error, string? requestedWorkingDirectory = null)
    {
        processId = 0;
        error = 0;
        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken)) { error = Marshal.GetLastWin32Error(); return false; }
            if (!DuplicateTokenEx(userToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            { error = Marshal.GetLastWin32Error(); return false; }
            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            { error = Marshal.GetLastWin32Error(); return false; }

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"winsta0\default"
            };
            var commandLine = new StringBuilder(string.IsNullOrWhiteSpace(arguments)
                ? $"\"{executable}\""
                : $"\"{executable}\" {arguments}");
            var workingDirectory = !string.IsNullOrWhiteSpace(requestedWorkingDirectory)
                && Directory.Exists(requestedWorkingDirectory)
                    ? requestedWorkingDirectory
                    : Path.GetDirectoryName(executable);
            if (!CreateProcessAsUser(primaryToken, executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, environment, workingDirectory, ref startup, out var processInfo))
            { error = Marshal.GetLastWin32Error(); return false; }

            processId = processInfo.dwProcessId;
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
