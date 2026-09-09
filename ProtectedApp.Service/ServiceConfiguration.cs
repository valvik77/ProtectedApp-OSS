using System.Runtime.InteropServices;

namespace ProtectedApp.Service;

internal static class ServiceConfiguration
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ServiceConfigFailureActionsFlag = 4;
    private const uint ServiceActionRestart = 1;
    private const uint RecoveryResetPeriodSeconds = 86_400;

    internal static IReadOnlyList<uint> ExpectedRecoveryDelaysMilliseconds { get; } = [0, 1_000, 5_000];

    public static bool EnsureExpectedConfiguration(string? guardianExecutable = null, string? appPath = null)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, GuardianConstants.ServiceName, ServiceQueryConfig | ServiceChangeConfig);
            if (service == IntPtr.Zero) return false;
            try
            {
                QueryServiceConfig(service, IntPtr.Zero, 0, out var bytesNeeded);
                if (bytesNeeded == 0) return false;
                var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    if (!QueryServiceConfig(service, buffer, bytesNeeded, out _)) return false;
                    var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
                    var expectedBinaryPath = BuildExpectedBinaryPath(guardianExecutable, appPath);
                    var currentBinaryPath = Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty;
                    var configurationChanged = config.StartType != ServiceAutoStart
                        || (expectedBinaryPath is not null && !PathsEqual(currentBinaryPath, expectedBinaryPath));
                    if (configurationChanged && !ChangeServiceConfig(service, ServiceNoChange, ServiceAutoStart,
                            ServiceNoChange, expectedBinaryPath, null, IntPtr.Zero, null, null, null, null))
                        return false;
                    return EnsureRecoveryActions(service) || configurationChanged;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static bool IsExpectedConfiguration(string guardianExecutable, string appPath)
    {
        var expectedBinaryPath = BuildExpectedBinaryPath(guardianExecutable, appPath);
        if (expectedBinaryPath is null) return false;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, GuardianConstants.ServiceName, ServiceQueryConfig);
            if (service == IntPtr.Zero) return false;
            try
            {
                QueryServiceConfig(service, IntPtr.Zero, 0, out var bytesNeeded);
                if (bytesNeeded == 0) return false;
                var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    if (!QueryServiceConfig(service, buffer, bytesNeeded, out _)) return false;
                    var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
                    var currentBinaryPath = Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty;
                    return config.StartType == ServiceAutoStart
                        && PathsEqual(currentBinaryPath, expectedBinaryPath)
                        && HasExpectedRecoveryActions(service, ExpectedRecoveryDelaysMilliseconds
                            .Select(delay => new ServiceAction { Type = ServiceActionRestart, DelayMilliseconds = delay })
                            .ToArray())
                        && HasExpectedFailureActionsFlag(service);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static string? BuildExpectedBinaryPath(string? guardianExecutable, string? appPath)
    {
        if (string.IsNullOrWhiteSpace(guardianExecutable) || string.IsNullOrWhiteSpace(appPath)) return null;
        try
        {
            return $"\"{Path.GetFullPath(guardianExecutable)}\" --app \"{Path.GetFullPath(appPath)}\"";
        }
        catch { return null; }
    }

    private static bool EnsureRecoveryActions(IntPtr service)
    {
        var expected = ExpectedRecoveryDelaysMilliseconds
            .Select(delay => new ServiceAction { Type = ServiceActionRestart, DelayMilliseconds = delay })
            .ToArray();
        var actionsChanged = false;
        if (!HasExpectedRecoveryActions(service, expected))
        {
            actionsChanged = SetRecoveryActions(service, expected);
            if (!actionsChanged) return false;
        }
        if (HasExpectedFailureActionsFlag(service)) return actionsChanged;
        return SetFailureActionsFlag(service) || actionsChanged;
    }

    private static bool SetRecoveryActions(IntPtr service, IReadOnlyList<ServiceAction> expected)
    {
        var actionsSize = Marshal.SizeOf<ServiceAction>() * expected.Count;
        var actions = Marshal.AllocHGlobal(actionsSize);
        var configuration = IntPtr.Zero;
        try
        {
            for (var index = 0; index < expected.Count; index++)
                Marshal.StructureToPtr(expected[index], IntPtr.Add(actions, index * Marshal.SizeOf<ServiceAction>()), false);
            configuration = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceFailureActions>());
            Marshal.StructureToPtr(new ServiceFailureActions
            {
                ResetPeriodSeconds = RecoveryResetPeriodSeconds,
                ActionCount = (uint)expected.Count,
                Actions = actions
            }, configuration, false);
            return ChangeServiceConfig2(service, ServiceConfigFailureActions, configuration);
        }
        finally
        {
            if (configuration != IntPtr.Zero) Marshal.FreeHGlobal(configuration);
            Marshal.FreeHGlobal(actions);
        }
    }

    private static bool HasExpectedFailureActionsFlag(IntPtr service)
    {
        QueryServiceConfig2(service, ServiceConfigFailureActionsFlag, IntPtr.Zero, 0, out var bytesNeeded);
        if (bytesNeeded < Marshal.SizeOf<ServiceFailureActionsFlag>()) return false;
        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            return QueryServiceConfig2(service, ServiceConfigFailureActionsFlag, buffer, bytesNeeded, out _)
                && Marshal.PtrToStructure<ServiceFailureActionsFlag>(buffer).FailureActionsOnNonCrashFailures != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool SetFailureActionsFlag(IntPtr service)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceFailureActionsFlag>());
        try
        {
            Marshal.StructureToPtr(new ServiceFailureActionsFlag { FailureActionsOnNonCrashFailures = 1 }, buffer, false);
            return ChangeServiceConfig2(service, ServiceConfigFailureActionsFlag, buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool HasExpectedRecoveryActions(IntPtr service, IReadOnlyList<ServiceAction> expected)
    {
        QueryServiceConfig2(service, ServiceConfigFailureActions, IntPtr.Zero, 0, out var bytesNeeded);
        if (bytesNeeded == 0) return false;
        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!QueryServiceConfig2(service, ServiceConfigFailureActions, buffer, bytesNeeded, out _)) return false;
            var current = Marshal.PtrToStructure<ServiceFailureActions>(buffer);
            if (current.ResetPeriodSeconds != RecoveryResetPeriodSeconds
                || current.ActionCount != expected.Count || current.Actions == IntPtr.Zero) return false;
            for (var index = 0; index < expected.Count; index++)
            {
                var action = Marshal.PtrToStructure<ServiceAction>(
                    IntPtr.Add(current.Actions, index * Marshal.SizeOf<ServiceAction>()));
                if (action.Type != expected[index].Type
                    || action.DelayMilliseconds != expected[index].DelayMilliseconds) return false;
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceAction
    {
        public uint Type;
        public uint DelayMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriodSeconds;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActionsFlag
    {
        public int FailureActionsOnNonCrashFailures;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr serviceConfig, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType,
        uint errorControl, string? binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfig2(IntPtr service, uint infoLevel, IntPtr buffer,
        uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
