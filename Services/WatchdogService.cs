using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProtectedApp.Services;

/// <summary>
/// Best-effort, per-user process supervision. It is deliberately not presented
/// as a Windows security boundary: an administrator can still terminate both
/// processes or remove the executable.
/// </summary>
public sealed class WatchdogService : IDisposable
{
    private const int SmShuttingDown = 0x2000;
    private readonly object _sync = new();
    private readonly string _eventName = $@"Local\ProtectedApp.GracefulExit.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly EventWaitHandle _gracefulExit;
    private Timer? _healthTimer;
    private Process? _watchdogProcess;
    private bool _disposed;

    public WatchdogService() =>
        _gracefulExit = new EventWaitHandle(false, EventResetMode.ManualReset, _eventName);

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || IsWatchdogMode(Environment.GetCommandLineArgs())) return;
            StartWatchdogLocked();
            _healthTimer ??= new Timer(CheckHealth, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
    }

    private void CheckHealth(object? state)
    {
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                if (_watchdogProcess is not null && !_watchdogProcess.HasExited) return;
            }
            catch { }
            StartWatchdogLocked();
        }
    }

    private void StartWatchdogLocked()
    {
        try
        {
            _watchdogProcess?.Dispose();
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo localizar ProtectedApp.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--watchdog");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(_eventName);
            _watchdogProcess = Process.Start(startInfo);
        }
        catch
        {
            _watchdogProcess = null;
        }
    }

    public static bool IsWatchdogMode(string[] args) =>
        args.Any(a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));

    public static void RunWatchdogMode(string[] args)
    {
        var marker = Array.FindIndex(args, a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));
        if (marker < 0 || marker + 2 >= args.Length || !int.TryParse(args[marker + 1], out var parentId)) return;

        try
        {
            using var gracefulExit = EventWaitHandle.OpenExisting(args[marker + 2]);
            Process? parent = null;
            try { parent = Process.GetProcessById(parentId); } catch { }

            while (parent is not null)
            {
                if (gracefulExit.WaitOne(500)) return;
                try
                {
                    parent.Refresh();
                    if (parent.HasExited) break;
                }
                catch { break; }
            }

            // Gives a graceful shutdown signal a final chance to arrive and
            // avoids starting applications while Windows is signing out.
            if (gracefulExit.WaitOne(750) || GetSystemMetrics(SmShuttingDown) != 0) return;
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList = { "--background", "--recovered" }
            });
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _healthTimer?.Dispose();
            _gracefulExit.Set();
            try
            {
                if (_watchdogProcess is { HasExited: false }) _watchdogProcess.WaitForExit(2000);
            }
            catch { }
            _watchdogProcess?.Dispose();
            _gracefulExit.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
