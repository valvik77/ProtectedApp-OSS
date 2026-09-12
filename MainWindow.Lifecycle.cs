using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ProtectedApp.Services;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    public void HideImmediatelyForSilentLaunch()
    {
        Interlocked.Increment(ref _windowVisibilityGeneration);
        _sessionUnlocked = false;
        _appWindow.Hide();
        ShowWindow(WindowNative.GetWindowHandle(this), SwHide);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        HideToTray(showNotification: true);
    }

    private void HideToTray(bool showNotification, bool forceLock = false)
    {
        Interlocked.Increment(ref _windowVisibilityGeneration);
        if (forceLock || _state.LockPanelWhenHidden) _sessionUnlocked = false;
        _appWindow.Hide();
        ShowWindow(WindowNative.GetWindowHandle(this), SwHide);
    }

    private async Task ExitWithAuthenticationAsync()
    {
        var guardianRunning = GuardianServiceDetector.IsRunning();
        if (!await VerifyMasterAsync(guardianRunning ? "Bloquear y ocultar ProtectedApp" : "Autorizar salida")) return;
        if (guardianRunning)
        {
            HideToTray(showNotification: false);
            return;
        }
        if (await LockAllVaultsAsync(showErrors: true) < 0) return;
        _allowClose = true;
        Close();
    }

    private void Cleanup()
    {
        UnregisterImmediateLockHotkey();
        _tamperTimer.Stop();
        _guardianTimer.Stop();
        _guardianHeartbeatTimer.Stop();
        _activitySaveTimer.Stop();
        _managementAutoLockTimer.Stop();
        _folderStatusTimer.Stop();
        _vaultTimer.Stop();
        _vaultBackupTimer.Stop();
        _watchdog?.Dispose();
        _monitor.Dispose();
        _tray.SecurityEventReceived -= Tray_SecurityEventReceived;
        _tray.Dispose();
        _dialogGate.Dispose();
        _unlockGate.Dispose();
        _saveGate.Dispose();
        _guardianSyncRecoveryGate.Dispose();
        _guardianInstallationGate.Dispose();
        _vaultOperationGate.Dispose();
        _vaultSecurityEventGate.Dispose();
        _vaultService.Dispose();
    }

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var titleBar = _appWindow.TitleBar;
        var light = Root.ActualTheme == ElementTheme.Light;
        var background = light
            ? Windows.UI.Color.FromArgb(255, 248, 248, 248)
            : Windows.UI.Color.FromArgb(255, 16, 22, 30);
        var hover = light
            ? Windows.UI.Color.FromArgb(255, 229, 229, 229)
            : Windows.UI.Color.FromArgb(255, 32, 43, 56);
        var foreground = light
            ? Windows.UI.Color.FromArgb(255, 31, 31, 31)
            : Windows.UI.Color.FromArgb(255, 242, 247, 251);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = light
            ? Windows.UI.Color.FromArgb(255, 204, 204, 204)
            : Windows.UI.Color.FromArgb(255, 49, 67, 86);
    }

    private void MainCloseButton_Click(object sender, RoutedEventArgs e) => HideToTray(showNotification: true);

    private void MainMinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void FitWindowToDisplay()
    {
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var scale = Root.XamlRoot?.RasterizationScale ?? 1d;
        var width = Math.Min((int)(1120 * scale), Math.Max(640, area.WorkArea.Width - 48));
        var height = Math.Min((int)(900 * scale), Math.Max(480, area.WorkArea.Height - 48));
        var x = area.WorkArea.X + (area.WorkArea.Width - width) / 2;
        var y = area.WorkArea.Y + (area.WorkArea.Height - height) / 2;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }
}
