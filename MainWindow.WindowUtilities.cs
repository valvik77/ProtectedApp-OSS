using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Services;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmHotkey = 0x0312;
    private const int MinimumWindowWidth = 1060;
    private const int MinimumWindowHeight = 540;

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message, "Aceptar", null);
        await dialog.ShowAsync();
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private SolidColorBrush ThemeBrush(Windows.UI.Color dark, Windows.UI.Color light)
    {
        // Keep older dynamically-created forms on the workspace palette too.
        dark = (dark.R, dark.G, dark.B) switch
        {
            (98, 114, 164) => Windows.UI.Color.FromArgb(dark.A, 190, 200, 210),
            (255, 85, 85) => Windows.UI.Color.FromArgb(dark.A, 255, 153, 164),
            (255, 184, 108) => Windows.UI.Color.FromArgb(dark.A, 240, 184, 73),
            (80, 250, 123) => Windows.UI.Color.FromArgb(dark.A, 78, 222, 163),
            _ => dark
        };
        return new(Root.ActualTheme == ElementTheme.Light ? light : dark);
    }

    private void RestoreMainWindowFocus()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        if (!_appWindow.IsVisible) _appWindow.Show(true);
        ShowWindow(hwnd, SwRestore);
        ShowWindow(hwnd, SwShowNoActivate);
        ShowWindow(hwnd, SwShow);
        Activate();
        SetForegroundWindow(hwnd);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            if (_appWindow.Presenter is OverlappedPresenter laterPresenter) laterPresenter.Restore();
            if (!_appWindow.IsVisible) _appWindow.Show(true);
            ShowWindow(hwnd, SwRestore);
            ShowWindow(hwnd, SwShowNoActivate);
            ShowWindow(hwnd, SwShow);
            Activate();
            SetForegroundWindow(hwnd);
        });
    }

    private static bool IsCriticalWindowsExecutable(string path)
    {
        var criticalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csrss.exe", "lsass.exe", "services.exe", "smss.exe", "wininit.exe", "winlogon.exe"
        };
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase)
            && criticalNames.Contains(Path.GetFileName(fullPath));
    }

    private sealed record PendingFolderRequest(string Path, bool UseContextAction);
    private sealed record PendingVaultRequest(string Path, bool UseContextAction);

    private IntPtr EnforceMinimumWindowSize(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == WmHotkey && wParam.ToInt32() == ImmediateLockHotkeyId)
        {
            DispatcherQueue.TryEnqueue(() => _ = ExecuteImmediateLockFromHotkeyAsync());
            return IntPtr.Zero;
        }

        if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var sizing = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var scale = Root.XamlRoot?.RasterizationScale ?? 1d;
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            sizing.MinimumTrackSize.X = Math.Min(area.Width, Math.Max(sizing.MinimumTrackSize.X, (int)Math.Ceiling(MinimumWindowWidth * scale)));
            sizing.MinimumTrackSize.Y = Math.Min(area.Height, Math.Max(sizing.MinimumTrackSize.Y, (int)Math.Ceiling(MinimumWindowHeight * scale)));
            Marshal.StructureToPtr(sizing, lParam, false);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximumSize;
        public NativePoint MaximumPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    private delegate IntPtr WindowSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, WindowSubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, WindowSubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
