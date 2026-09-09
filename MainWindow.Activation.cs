using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async Task ShowAndAuthenticateAsync()
    {
        var discardQueuedShowRequest = false;
        if (_activeUnlockWindow is { } activeUnlockWindow)
        {
            activeUnlockWindow.BringToForeground();
            return;
        }
        if (_activeNoticeWindow is { } activeNoticeWindow)
        {
            activeNoticeWindow.BringToForeground();
            return;
        }
        if (!_initialized)
        {
            _showRequestedWhileLoading = true;
            return;
        }
        if (Interlocked.Exchange(ref _showRequestBusy, 1) != 0)
        {
            // Do not lose an explicit user activation while a previous
            // authentication window is closing or recovering its policy.
            _showRequestedWhileBusy = true;
            return;
        }
        try
        {
            if (!_sessionUnlocked)
            {
                if (string.IsNullOrWhiteSpace(_state.MasterPasswordHash))
                {
                    if (_guardianManaged)
                    {
                        if (!await RecoverLocalStateFromGuardianAsync()) return;
                    }
                    else
                    {
                        ShowMainWindow();
                        await CreateMasterPasswordAsync("Crea tu contraseña maestra", "La necesitarás para abrir ProtectedApp y cambiar ajustes.");
                        _sessionUnlocked = true;
                        RecordManagementActivity();
                    }
                }
                else
                {
                    // The authentication window is independent. Do not
                    // restore the management panel before verification: a
                    // cancelled prompt must leave the panel hidden.
                    if (!await VerifyMasterAsync("Desbloquear ProtectedApp"))
                    {
                        // Any duplicate activation received while the prompt
                        // was visible must not reopen it after Cancel.
                        discardQueuedShowRequest = true;
                        HideToTray(showNotification: false);
                        return;
                    }
                }
            }
            ShowMainWindow();
            await ShowPendingVaultRecoveryWarningAsync();
        }
        catch (Exception ex)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "activation-error.log"),
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _showRequestBusy, 0);
            if (discardQueuedShowRequest)
                _showRequestedWhileBusy = false;
            if (_showRequestedWhileBusy)
            {
                _showRequestedWhileBusy = false;
                DispatcherQueue.TryEnqueue(() => _ = ShowAndAuthenticateAsync());
            }
        }
    }

    public void ShowMainWindow()
    {
        var visibilityGeneration = Interlocked.Increment(ref _windowVisibilityGeneration);
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "launch.log"),
                $"{DateTimeOffset.Now:O} ShowMainWindow called (initialized={_initialized}){Environment.NewLine}");
        }
        catch { }

        var hwnd = WindowNative.GetWindowHandle(this);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();

        if (!_appWindow.IsVisible)
            _appWindow.Show(true);

        ShowWindow(hwnd, SwRestore);
        ShowWindow(hwnd, SwShowNoActivate);
        ShowWindow(hwnd, SwShow);

        if (_initialized)
            RecordManagementActivity();

        Activate();
        SetForegroundWindow(hwnd);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (visibilityGeneration != Volatile.Read(ref _windowVisibilityGeneration)) return;
            if (_appWindow.Presenter is OverlappedPresenter laterPresenter)
                laterPresenter.Restore();
            if (!_appWindow.IsVisible)
                _appWindow.Show(true);
            ShowWindow(hwnd, SwRestore);
            ShowWindow(hwnd, SwShowNoActivate);
            ShowWindow(hwnd, SwShow);
            Activate();
            SetForegroundWindow(hwnd);
        });
    }

    public void RequestShowFromActivation() => _ = ShowAndAuthenticateAsync();

    public void RequestUpdateShutdown() => _ = ShutdownForUpdateAsync();

    private async Task ShutdownForUpdateAsync()
    {
        if (Interlocked.Exchange(ref _updateShutdownRequested, 1) != 0) return;
        if (await LockAllVaultsAsync(showErrors: false) < 0)
        {
            Interlocked.Exchange(ref _updateShutdownRequested, 0);
            return;
        }

        _allowClose = true;
        Close();
    }

    public void RequestFolderFromActivation(string folderPath, bool useContextAction = false)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        _pendingFolderRequests.Enqueue(new PendingFolderRequest(folderPath, useContextAction));
        // A folder request is an interactive operation. Keep a recoverable
        // entry point available even when the optional tray setting was off.
        _tray.SetVisible(true);
        if (_initialized) _ = ProcessPendingFolderRequestsAsync();
    }

    public void RequestVaultFromActivation(string vaultPath, bool useContextAction = false)
    {
        if (string.IsNullOrWhiteSpace(vaultPath)) return;
        // Explorer may send the same activation repeatedly while a password
        // prompt is visible. One request is enough; queuing all of them can
        // reopen the mounted drive and make the panel appear unresponsive.
        if (_pendingVaultRequests.Any(request => PathsEqual(request.Path, vaultPath))) return;
        _pendingVaultRequests.Enqueue(new PendingVaultRequest(vaultPath, useContextAction));
        _tray.SetVisible(true);
        if (_initialized) _ = ProcessPendingVaultRequestsAsync();
    }

    public void RequestVaultUnmountFromActivation(string mountPath)
    {
        if (string.IsNullOrWhiteSpace(mountPath)) return;
        var normalized = Path.GetPathRoot(mountPath);
        if (string.IsNullOrWhiteSpace(normalized)) return;
        if (_pendingVaultUnmountDrives.Any(candidate => PathsEqual(candidate, normalized))) return;
        _pendingVaultUnmountDrives.Enqueue(normalized);
        _tray.SetVisible(true);
        if (_initialized) _ = ProcessPendingVaultUnmountRequestsAsync();
    }
}
