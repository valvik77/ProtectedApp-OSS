using Microsoft.UI.Xaml;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        try
        {
            await Root_LoadedAsync();
        }
        catch (Exception ex)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "root-loaded-error.log"),
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }

            // A background/service launch must never surface a partially
            // initialized management panel or an authentication dialog.
            if (AppLaunchMode.IsSilent(Environment.GetCommandLineArgs()))
                HideImmediatelyForSilentLaunch();
            else
            {
                ShowMainWindow();
                try
                {
                    if (ex is TpmStateProtectionUnavailableException)
                    {
                        await ShowMessageAsync("Configuración TPM no disponible",
                            "ProtectedApp no ha modificado tu configuración. La clave TPM que la protege no está disponible; puede ocurrir después de restablecer el TPM, reinstalar Windows o cambiar la placa base. Restaura una copia de configuración o vuelve a configurarla cuando hayas comprobado el estado del equipo.");
                        return;
                    }
                    var persistenceFailure = ex is IOException or UnauthorizedAccessException
                        or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException;
                    await ShowMessageAsync("No se pudo iniciar ProtectedApp", persistenceFailure
                        ? "ProtectedApp no modificó tu configuración porque Windows no permitió leerla. Cierra cualquier programa que pueda estar usando los archivos de ProtectedApp y reinicia la aplicación."
                        : "Se produjo un error inesperado al iniciar. No se ha modificado tu configuración. Consulta root-loaded-error.log en la carpeta local de ProtectedApp para ver el detalle.");
                }
                catch { }
            }
        }
    }

    private async Task Root_LoadedAsync()
    {
        var commandLine = Environment.GetCommandLineArgs();
        var skipAutomaticServiceInstall = commandLine.Any(argument =>
            argument.Equals("--no-service-install", StringComparison.OrdinalIgnoreCase));
        var interactiveLaunch = !AppLaunchMode.IsSilent(commandLine)
            && VaultActivationRequest.TryGetVaultPath(commandLine) is null
            && VaultActivationRequest.TryGetVaultUnmountDrive(commandLine) is null;
        FitWindowToDisplay();
        _state = await _store.LoadAsync();
        var installerLanguage = TryGetInstallerLanguage(commandLine);
        if (installerLanguage is not null)
        {
            _state.LanguagePreference = installerLanguage;
            await _store.SaveAsync(_state);
        }
        ApplySavedLanguagePreference();
        ApplySavedThemePreference();
        _vaultService.PreferredVirtualDriveLetter = TryGetPreferredVaultDriveLetter();
        _activityHistory.AddRange(_state.ActivityHistory
            .OrderByDescending(entry => entry.Timestamp)
            .Take(500));
        RefreshVisibleActivity();
        await PrepareProtectedApplicationIconsAsync(_state.Applications);
        foreach (var app in _state.Applications.OrderBy(a => a.Name))
        {
            app.PropertyChanged += Rule_PropertyChanged;
            Applications.Add(app);
        }
        // Folder rules from prior releases are intentionally retired. Guardian
        // restores their ACLs during its startup migration; here we restore the
        // cosmetic folder metadata kept by the UI and remove the local rules.
        if (_state.Folders.Count > 0)
        {
            foreach (var folder in _state.Folders) FolderIconService.TryRestore(folder);
            AddActivity("ProtectedApp", $"Retiradas {_state.Folders.Count} regla(s) heredada(s) de carpetas NTFS; usa bóvedas cifradas para proteger su contenido");
            _state.Folders.Clear();
            await _store.SaveAsync(_state);
        }
        foreach (var vault in _state.Vaults.OrderBy(vault => vault.Name))
        {
            vault.IsMounted = false;
            vault.MountPath = null;
            vault.SessionExpiresUtc = null;
            if (!string.IsNullOrWhiteSpace(vault.VaultFilePath) && File.Exists(vault.VaultFilePath))
                vault.SizeBytes = new FileInfo(vault.VaultFilePath).Length;
            vault.PropertyChanged += Vault_PropertyChanged;
            Vaults.Add(vault);
        }

        if (GuardianServiceDetector.IsFullyConfigured())
        {
            var guardianStatus = await _guardianClient.GetStatusAsync();
            _guardianManaged = guardianStatus.Success && guardianStatus.PolicyConfigured;
        }
        _guardianPolicyRecoveryRequired = _guardianManaged
            && !string.IsNullOrWhiteSpace(_state.MasterPasswordHash)
            && Applications.Count == 0;
        _monitor.IntervalMilliseconds = _state.PollIntervalMilliseconds;
        _monitor.IsPaused = true;
        _updatingControls = true;
        StartupToggle.IsOn = StartupService.IsEnabled();
        TrayIconToggle.IsOn = _state.ShowTrayIcon;
        LockPanelWhenHiddenToggle.IsOn = _state.LockPanelWhenHidden;
        SelectLanguagePreference(_state.LanguagePreference);
        VaultReadOnlyByDefaultToggle.IsOn = _state.OpenVaultsReadOnlyByDefault;
        SelectPreferredVaultDriveLetter(_state.PreferredVaultDriveLetter);
        VaultBackupNotificationsToggle.IsOn = _state.VaultBackupNotificationsEnabled;
        CloseWarningNotificationsToggle.IsOn = _state.CloseWarningNotificationsEnabled;
        SecurityAlertsToggle.IsOn = _state.SecurityAlertsEnabled;
        ApplyImmediateLockHotkey(showError: false);
        RefreshImmediateLockHotkeyStatus();
        WindowsHelloToggle.IsOn = _state.UseWindowsHello;
        TpmProtectionToggle.IsOn = _store.IsTpmProtectionEnabled;
        RefreshTpmProtectionStatus();
        SelectManagementAutoLock(_state.ManagementAutoLockMinutes);
        SelectPollInterval(_state.PollIntervalMilliseconds);
        RefreshVaultBackupStatus();
        _updatingControls = false;
        UpdateStatusText.Text = $"Versión instalada {ManualUpdateService.GetDisplayVersion(ManualUpdateService.GetInstalledVersion())}";
        _ = UpdateWindowsHelloStatusAsync();
        _tray.SetVisible(_state.ShowTrayIcon);
        _initialized = true;

        // A protected application can be launched while this background
        // agent is still preparing icons, vault recovery and the dashboard.
        // Start consuming Guardian's pending request immediately so its
        // password window is never delayed by those non-security tasks.
        _guardianTimer.Start();
        _guardianHeartbeatTimer.Start();
        GuardianTimer_Tick(null, null!);
        GuardianHeartbeatTimer_Tick(null, null!);

        RefreshCategoryChips();
        RefreshVisibleApps();
        RefreshFolders();
        RefreshVaults();
        await RefreshVaultRecoveryItemsAsync();
        RefreshGuardianStatus();

        if (!string.IsNullOrWhiteSpace(_state.MasterPasswordHash) && TamperSignalService.TryPeekBatch(out var tamperSignals))
        {
            interactiveLaunch = false;
            await ApplyTamperResponseAsync(tamperSignals);
        }
        _tamperTimer.Start();
        _managementAutoLockTimer.Start();
        _folderStatusTimer.Start();
        _vaultTimer.Start();
        _vaultBackupTimer.Start();
        _ = RunScheduledVaultBackupsAsync(force: false);

        var startupUiAction = AppLaunchMode.ResolveStartupUiAction(
            silentLaunch: !interactiveLaunch,
            hasMasterPassword: !string.IsNullOrWhiteSpace(_state.MasterPasswordHash),
            guardianManaged: _guardianManaged);

        switch (startupUiAction)
        {
            case StartupUiAction.KeepHidden:
                HideToTray(showNotification: false);
                break;
            case StartupUiAction.RecoverProtectedState:
                HideToTray(showNotification: false);
                if (await RecoverLocalStateFromGuardianAsync()) ShowMainWindow();
                break;
            case StartupUiAction.CreateMasterPassword:
                ShowMainWindow();
                await CreateMasterPasswordAsync("Crea tu contraseña maestra", "La necesitarás para abrir ProtectedApp y cambiar ajustes.");
                _sessionUnlocked = true;
                RecordManagementActivity();
                break;
            case StartupUiAction.UnlockManagementPanel:
                ShowMainWindow();
                if (!await VerifyMasterAsync("Desbloquear ProtectedApp"))
                    HideToTray(showNotification: false);
                break;
        }

        if (interactiveLaunch && _sessionUnlocked && !skipAutomaticServiceInstall)
            await EnsureGuardianInstalledAutomaticallyAsync();

        if (_showRequestedWhileLoading)
        {
            _showRequestedWhileLoading = false;
            await ShowAndAuthenticateAsync();
        }
        else if (_sessionUnlocked && _appWindow.IsVisible)
        {
            await ShowPendingVaultRecoveryWarningAsync();
        }
        await ProcessPendingFolderRequestsAsync();
        await ProcessPendingVaultRequestsAsync();
        await ProcessPendingVaultUnmountRequestsAsync();
    }

    private static string? TryGetInstallerLanguage(IReadOnlyList<string> commandLine)
    {
        for (var index = 0; index < commandLine.Count - 1; index++)
        {
            if (!commandLine[index].Equals("--install-language", StringComparison.OrdinalIgnoreCase)) continue;
            return commandLine[index + 1].Equals("english", StringComparison.OrdinalIgnoreCase) ? "en"
                : commandLine[index + 1].Equals("spanish", StringComparison.OrdinalIgnoreCase) ? "es"
                : null;
        }
        return null;
    }

    private async Task EnsureGuardianInstalledAutomaticallyAsync()
    {
        if (GuardianServiceDetector.IsFullyConfigured())
        {
            var status = await _guardianClient.GetStatusAsync();
            if (status.Success && status.PolicyConfigured) return;
        }
        var installer = Path.Combine(AppContext.BaseDirectory, "Service", "Install-Service.ps1");
        if (!File.Exists(installer)) return;
        await SetGuardianInstalledAsync(true);
    }
}
