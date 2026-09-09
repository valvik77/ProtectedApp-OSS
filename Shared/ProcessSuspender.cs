using System.Runtime.InteropServices;

namespace ProtectedApp.Shared;

public static class ProcessSuspender
{
    private const uint ProcessSuspendResume = 0x0800;

    public static bool TrySuspend(int processId) => Invoke(processId, NtSuspendProcess);

    public static bool TryResume(int processId) => Invoke(processId, NtResumeProcess);

    private static bool Invoke(int processId, Func<IntPtr, int> operation)
    {
        if (processId is 0 or 4 || processId == Environment.ProcessId) return false;
        var handle = OpenProcess(ProcessSuspendResume, false, processId);
        if (handle == IntPtr.Zero) return false;
        try { return operation(handle) >= 0; }
        catch { return false; }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
