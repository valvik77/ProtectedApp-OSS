using System.Runtime.InteropServices;

namespace ProtectedApp.Shared;

/// <summary>
/// Reads the current directory a process was started in. A relative script name such as
/// <c>python backup.py</c> only identifies a file once it is resolved against that
/// directory, and Windows does not expose it through WMI.
/// </summary>
public static class ProcessWorkingDirectory
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessWow64InformationClass = 26;
    private const int MaximumPathBytes = 32 * 1024;

    // Offsets in the documented-by-layout PEB and RTL_USER_PROCESS_PARAMETERS structures.
    // PEB.ProcessParameters, then CurrentDirectory.DosPath (a UNICODE_STRING) inside it.
    private const int Peb64ProcessParameters = 0x20;
    private const int Parameters64CurrentDirectory = 0x38;
    private const int Peb32ProcessParameters = 0x10;
    private const int Parameters32CurrentDirectory = 0x24;

    /// <summary>Best effort: returns null when the process is gone or cannot be read.</summary>
    public static string? TryGet(int processId)
    {
        if (processId <= 4 || !Environment.Is64BitProcess) return null;
        var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            // A 32-bit process on 64-bit Windows keeps its parameters in a separate 32-bit PEB.
            if (NtQueryWow64(process, ProcessWow64InformationClass, out var peb32, IntPtr.Size, out _) == 0
                && peb32 != IntPtr.Zero)
                return ReadCurrentDirectory32(process, peb32);

            var information = new ProcessBasicInformation();
            if (NtQueryBasic(process, ProcessBasicInformationClass, ref information,
                    Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0
                || information.PebBaseAddress == IntPtr.Zero)
                return null;
            return ReadCurrentDirectory64(process, information.PebBaseAddress);
        }
        finally { CloseHandle(process); }
    }

    private static string? ReadCurrentDirectory64(IntPtr process, IntPtr peb)
    {
        var pointer = new byte[8];
        if (!Read(process, peb + Peb64ProcessParameters, pointer)) return null;
        var parameters = new IntPtr(BitConverter.ToInt64(pointer, 0));
        // UNICODE_STRING: Length (2), MaximumLength (2), padding (4), Buffer (8).
        var header = new byte[16];
        if (parameters == IntPtr.Zero || !Read(process, parameters + Parameters64CurrentDirectory, header)) return null;
        return ReadString(process, new IntPtr(BitConverter.ToInt64(header, 8)), BitConverter.ToUInt16(header, 0));
    }

    private static string? ReadCurrentDirectory32(IntPtr process, IntPtr peb)
    {
        var pointer = new byte[4];
        if (!Read(process, peb + Peb32ProcessParameters, pointer)) return null;
        var parameters = new IntPtr(BitConverter.ToUInt32(pointer, 0));
        // UNICODE_STRING (32-bit): Length (2), MaximumLength (2), Buffer (4).
        var header = new byte[8];
        if (parameters == IntPtr.Zero || !Read(process, parameters + Parameters32CurrentDirectory, header)) return null;
        return ReadString(process, new IntPtr(BitConverter.ToUInt32(header, 4)), BitConverter.ToUInt16(header, 0));
    }

    private static string? ReadString(IntPtr process, IntPtr address, int byteLength)
    {
        if (address == IntPtr.Zero || byteLength <= 0 || byteLength > MaximumPathBytes || byteLength % 2 != 0)
            return null;
        var buffer = new byte[byteLength];
        if (!Read(process, address, buffer)) return null;
        var path = System.Text.Encoding.Unicode.GetString(buffer);
        // Windows keeps a trailing separator on the current directory ("C:\tools\").
        path = Path.TrimEndingDirectorySeparator(path);
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    private static bool Read(IntPtr process, IntPtr address, byte[] buffer) =>
        ReadProcessMemory(process, address, buffer, new IntPtr(buffer.Length), out var read)
        && read == new IntPtr(buffer.Length);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer,
        IntPtr size, out IntPtr bytesRead);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryBasic(IntPtr process, int informationClass,
        ref ProcessBasicInformation information, int length, out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryWow64(IntPtr process, int informationClass,
        out IntPtr information, int length, out int returnLength);
}
