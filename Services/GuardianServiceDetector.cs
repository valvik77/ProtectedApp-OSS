using System.Runtime.InteropServices;
using ProtectedApp.Shared;

namespace ProtectedApp.Services;

public static class GuardianServiceDetector
{
    public const string ServiceName = "ProtectedAppGuardian";
    public const string ExpectedProtectionVersion = GuardianProtocol.ProtectionVersion;
    public const string HealthTaskName = "ProtectedApp Guardian Health Check";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStartPending = 0x00000002;

    public static bool IsRunning()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return false;
            try
            {
                if (!QueryServiceStatus(service, out var status)) return false;
                return status.dwCurrentState is ServiceRunning or ServiceStartPending;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static bool IsInstalled()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return false;
            CloseServiceHandle(service);
            return true;
        }
        finally { CloseServiceHandle(manager); }
    }

    public static bool IsFullyConfigured()
    {
        // La política se confirma mediante el canal autenticado de Guardian.
        // El archivo de versión es solo diagnóstico: puede faltar durante una
        // reparación aunque el servicio y su política estén perfectamente
        // operativos, y no debe ocultar ese estado a la interfaz.
        return IsRunning();
    }

    public static string? GetInstalledProtectionVersion()
    {
        try
        {
            var versionPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ProtectedApp", "guardian-protection.version");
            return File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
            dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
