using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Security.Cryptography;
using ProtectedApp.Models;
using ProtectedApp.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Autorizar copia de seguridad")) return;
        var password = await RequestBackupPasswordAsync(creating: true);
        if (password is null) return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"ProtectedApp-{DateTime.Now:yyyy-MM-dd}"
        };
        picker.FileTypeChoices.Add("Copia cifrada de ProtectedApp", new List<string> { ".pabackup" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file;
        try { file = await picker.PickSaveFileAsync(); }
        finally { RestoreMainWindowFocus(); }
        if (file is null) return;

        try
        {
            var snapshot = CaptureBackupSnapshot();
            var bytes = await BackupService.CreateAsync(snapshot, password);
            await File.WriteAllBytesAsync(file.Path, bytes);
            AddActivity("ProtectedApp", $"Copia de seguridad cifrada exportada con {snapshot.Applications.Count} aplicaciones y {snapshot.Folders.Count} carpetas");
            await ShowMessageAsync("Copia creada", "La configuración se ha exportado correctamente. Guarda también la contraseña de la copia: no puede recuperarse si se pierde.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            AddActivity("ProtectedApp", "No se pudo exportar la copia de seguridad.");
            await ShowMessageAsync("No se pudo crear la copia", LocalizationService.UserFacingError(ex));
        }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Autorizar restauración")) return;
        if (Vaults.Any(vault => vault.IsMounted))
        {
            await ShowMessageAsync("Hay bóvedas abiertas", "Guarda y bloquea todas las bóvedas antes de reemplazar la configuración.");
            return;
        }

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pabackup");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file;
        try { file = await picker.PickSingleFileAsync(); }
        finally { RestoreMainWindowFocus(); }
        if (file is null) return;

        var password = await RequestBackupPasswordAsync(creating: false);
        if (password is null) return;

        BackupSnapshot snapshot;
        try
        {
            snapshot = await BackupService.ReadAsync(await File.ReadAllBytesAsync(file.Path), password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AddActivity("ProtectedApp", "Intento de restauración rechazado: contraseña incorrecta o copia no válida");
            await ShowMessageAsync("No se pudo abrir la copia", LocalizationService.UserFacingError(ex));
            return;
        }

        var excluded = snapshot.Applications.Count(application => IsForbiddenBackupTarget(application.Path));
        var applications = snapshot.Applications
            .Where(application => !IsForbiddenBackupTarget(application.Path))
            .ToList();
        var missing = applications.Count(application => !File.Exists(application.Path));
        var folders = snapshot.Folders.Where(folder => Directory.Exists(folder.Path)).ToList();
        var missingFolders = snapshot.Folders.Count - folders.Count;
        var vaults = snapshot.Vaults.Where(vault => File.Exists(vault.VaultFilePath)).ToList();
        var missingVaults = snapshot.Vaults.Count - vaults.Count;
        var summary = new StackPanel { Spacing = 8, Width = 440 };
        summary.Children.Add(new TextBlock
        {
            Text = $"Copia del {snapshot.ExportedAtUtc.ToLocalTime():dd/MM/yyyy 'a las' HH:mm}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        summary.Children.Add(new TextBlock
        {
            Text = $"Se reemplazarán las {Applications.Count} aplicaciones, {Folders.Count} carpetas y {Vaults.Count} bóvedas actuales por {applications.Count} aplicaciones, {folders.Count} carpetas y {vaults.Count} bóvedas. También se restaurarán {snapshot.ActivityHistory.Count} eventos.",
            TextWrapping = TextWrapping.Wrap
        });
        if (missing > 0)
            summary.Children.Add(new TextBlock
            {
                Text = $"{missing} reglas apuntan a archivos que no existen actualmente; permanecerán en la lista para poder corregirlas o instalarlas después.",
                Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 184, 108), Windows.UI.Color.FromArgb(255, 137, 85, 3)),
                TextWrapping = TextWrapping.Wrap
            });
        if (excluded > 0)
            summary.Children.Add(new TextBlock
            {
                Text = $"Se excluirán {excluded} reglas que intentan proteger componentes de ProtectedApp o procesos esenciales de Windows.",
                Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 184, 108), Windows.UI.Color.FromArgb(255, 137, 85, 3)),
                TextWrapping = TextWrapping.Wrap
            });
        if (missingFolders > 0)
            summary.Children.Add(new TextBlock
            {
                Text = $"Se omitirán {missingFolders} carpetas que ya no existen para evitar reglas imposibles de restaurar.",
                Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 184, 108), Windows.UI.Color.FromArgb(255, 137, 85, 3)),
                TextWrapping = TextWrapping.Wrap
            });
        if (missingVaults > 0)
            summary.Children.Add(new TextBlock
            {
                Text = $"Se omitirán {missingVaults} referencias a bóvedas cuyo contenedor cifrado ya no existe en esa ruta.",
                Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 184, 108), Windows.UI.Color.FromArgb(255, 137, 85, 3)),
                TextWrapping = TextWrapping.Wrap
            });

        var confirmation = CreateDialog("Restaurar copia", summary, "Reemplazar configuración", "Cancelar");
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await PrepareProtectedApplicationIconsAsync(applications);
            StartupService.SetEnabled(snapshot.StartWithWindows);

            foreach (var existing in Applications) existing.PropertyChanged -= Rule_PropertyChanged;
            Applications.Clear();
            foreach (var application in applications)
            {
                application.PropertyChanged += Rule_PropertyChanged;
                Applications.Add(application);
            }
            foreach (var existing in Folders) existing.PropertyChanged -= Folder_PropertyChanged;
            Folders.Clear();
            foreach (var folder in folders)
            {
                folder.PropertyChanged += Folder_PropertyChanged;
                Folders.Add(folder);
            }
            foreach (var existing in Vaults) existing.PropertyChanged -= Vault_PropertyChanged;
            Vaults.Clear();
            foreach (var vault in vaults)
            {
                vault.SizeBytes = new FileInfo(vault.VaultFilePath!).Length;
                vault.PropertyChanged += Vault_PropertyChanged;
                Vaults.Add(vault);
            }

            _activityHistory.Clear();
            _activityHistory.AddRange(snapshot.ActivityHistory);
            _state.StartWithWindows = snapshot.StartWithWindows;
            _state.ShowTrayIcon = snapshot.ShowTrayIcon;
            _state.LockPanelWhenHidden = snapshot.LockPanelWhenHidden;
            _state.ThemePreference = snapshot.ThemePreference;
            _state.LanguagePreference = snapshot.LanguagePreference;
            _state.ManagementAutoLockMinutes = snapshot.ManagementAutoLockMinutes;
            _state.PollIntervalMilliseconds = snapshot.PollIntervalMilliseconds;
            _state.OpenVaultsReadOnlyByDefault = snapshot.OpenVaultsReadOnlyByDefault;
            _state.VaultBackupDirectory = snapshot.VaultBackupDirectory;
            _state.VaultBackupIntervalHours = snapshot.VaultBackupIntervalHours;
            _state.VaultBackupRetentionCount = snapshot.VaultBackupRetentionCount;
            _state.VaultBackupNotificationsEnabled = snapshot.VaultBackupNotificationsEnabled;
            _state.CloseWarningNotificationsEnabled = snapshot.CloseWarningNotificationsEnabled;
            _state.SecurityAlertsEnabled = snapshot.SecurityAlertsEnabled;
            _state.ImmediateLockHotkey = snapshot.ImmediateLockHotkey;
            _monitor.IntervalMilliseconds = snapshot.PollIntervalMilliseconds;

            _updatingControls = true;
            StartupToggle.IsOn = snapshot.StartWithWindows;
            TrayIconToggle.IsOn = snapshot.ShowTrayIcon;
            LockPanelWhenHiddenToggle.IsOn = snapshot.LockPanelWhenHidden;
            VaultReadOnlyByDefaultToggle.IsOn = snapshot.OpenVaultsReadOnlyByDefault;
            VaultBackupNotificationsToggle.IsOn = snapshot.VaultBackupNotificationsEnabled;
            CloseWarningNotificationsToggle.IsOn = snapshot.CloseWarningNotificationsEnabled;
            SecurityAlertsToggle.IsOn = snapshot.SecurityAlertsEnabled;
            ApplyImmediateLockHotkey(showError: false);
            RefreshImmediateLockHotkeyStatus();
            SelectManagementAutoLock(snapshot.ManagementAutoLockMinutes);
            SelectPollInterval(snapshot.PollIntervalMilliseconds);
            _updatingControls = false;
            ApplySavedLanguagePreference();
            ApplySavedThemePreference();
            RefreshVaultBackupStatus();
            _tray.SetVisible(snapshot.ShowTrayIcon);

            RefreshVisibleApps();
            RefreshFolders();
            RefreshVaults();
            RefreshVisibleActivity();
            AddActivity("ProtectedApp", $"Copia de seguridad restaurada con {applications.Count} aplicaciones, {folders.Count} carpetas y {vaults.Count} bóvedas");
            await SaveAsync();
            await ShowMessageAsync("Copia restaurada", _guardianSyncPending
                ? "La configuración se restauró localmente. Guardian la sincronizará después de volver a validar la contraseña maestra."
                : "Las reglas, preferencias y el historial se han restaurado y sincronizado correctamente.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _updatingControls = false;
            await ShowMessageAsync("No se pudo restaurar la copia", LocalizationService.UserFacingError(ex));
        }
    }

    private async Task<string?> RequestBackupPasswordAsync(bool creating)
    {
        var password = new PasswordBox
        {
            Header = creating ? "Contraseña de la copia" : "Contraseña de la copia",
            PlaceholderText = "Contraseña",
            PasswordRevealMode = PasswordRevealMode.Peek
        };
        var confirmation = new PasswordBox
        {
            Header = "Confirmar contraseña",
            PlaceholderText = "Repite la contraseña",
            PasswordRevealMode = PasswordRevealMode.Peek,
            Visibility = creating ? Visibility.Visible : Visibility.Collapsed
        };
        var error = new TextBlock
        {
            Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 85, 85), Windows.UI.Color.FromArgb(255, 248, 81, 73)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Width = 400, Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = creating
                ? "Esta contraseña cifra el archivo y puede ser distinta de la contraseña maestra. Necesitarás conservarla para restaurar la copia."
                : "Introduce la contraseña que se utilizó al crear esta copia.",
            Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 118, 118, 118)),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(password);
        panel.Children.Add(confirmation);
        panel.Children.Add(error);
        var dialog = CreateDialog(creating ? "Proteger copia" : "Abrir copia cifrada", panel,
            creating ? "Continuar" : "Descifrar", "Cancelar");
        var valid = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (creating && password.Password.Length < PasswordService.MinimumPasswordLength)
            {
                error.Text = LocalizationService.T("La contraseña debe tener al menos 12 caracteres.");
                args.Cancel = true;
                return;
            }
            if (!creating && string.IsNullOrEmpty(password.Password))
            {
                error.Text = LocalizationService.T("Introduce la contraseña de la copia.");
                args.Cancel = true;
                return;
            }
            if (creating && password.Password != confirmation.Password)
            {
                error.Text = LocalizationService.T("Las contraseñas no coinciden.");
                args.Cancel = true;
                return;
            }
            valid = true;
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && valid ? password.Password : null;
    }

    private void RefreshConfigurationBackupStatus()
    {
        if (ConfigurationBackupStatusText is null || RunConfigurationBackupButton is null) return;
        var configured = !string.IsNullOrWhiteSpace(_state.ConfigurationBackupDirectory)
            && ConfigurationBackupPasswordStore.Exists();
        RunConfigurationBackupButton.IsEnabled = configured;
        if (!configured)
        {
            ConfigurationBackupStatusText.Text = LocalizationService.IsEnglish ? "Not configured." : "Sin configurar.";
            return;
        }

        var interval = Math.Clamp(_state.ConfigurationBackupIntervalHours, 1, 24 * 7);
        var english = LocalizationService.IsEnglish;
        var frequency = interval == 24
            ? (english ? "day" : "día")
            : interval == 168 ? (english ? "week" : "semana") : $"{interval} h";
        var last = _state.ConfigurationBackupLastRunUtc is { } timestamp
            ? english ? $" · last backup {timestamp.ToLocalTime():MM/dd HH:mm}" : $" · última copia {timestamp.ToLocalTime():dd/MM HH:mm}"
            : english ? " · first backup pending" : " · pendiente de la primera copia";
        var failure = string.IsNullOrWhiteSpace(_state.ConfigurationBackupLastError)
            ? string.Empty : english ? $" · error: {_state.ConfigurationBackupLastError}" : $" · error: {_state.ConfigurationBackupLastError}";
        ConfigurationBackupStatusText.Text = english
            ? $"Every {frequency} · retains {Math.Clamp(_state.ConfigurationBackupRetentionCount, 1, 20)} versions{last}{failure}"
            : $"Cada {frequency} · conserva {Math.Clamp(_state.ConfigurationBackupRetentionCount, 1, 20)} versiones{last}{failure}";
    }

    private async void ConfigureConfigurationBackups_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Configurar copias automáticas de configuración")) return;
        var enabled = !string.IsNullOrWhiteSpace(_state.ConfigurationBackupDirectory)
            && ConfigurationBackupPasswordStore.Exists();
        var directory = _state.ConfigurationBackupDirectory;
        var interval = Math.Clamp(_state.ConfigurationBackupIntervalHours, 1, 24 * 7);
        var retention = Math.Clamp(_state.ConfigurationBackupRetentionCount, 1, 20);

        while (true)
        {
            var toggle = new ToggleSwitch { Header = "Activar copias automáticas", IsOn = enabled };
            var destination = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(directory) ? "No se ha elegido una carpeta." : directory,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush
            };
            var frequency = new ComboBox { Header = "Frecuencia" };
            frequency.Items.Add(new ComboBoxItem { Content = "Cada día", Tag = "24" });
            frequency.Items.Add(new ComboBoxItem { Content = "Cada semana", Tag = "168" });
            frequency.SelectedIndex = interval >= 168 ? 1 : 0;
            var versions = new NumberBox
            {
                Header = "Versiones que conservar",
                Minimum = 1,
                Maximum = 20,
                Value = retention,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            var password = new PasswordBox { Header = "Contraseña de las copias", PlaceholderText = "Mínimo 12 caracteres", PasswordRevealMode = PasswordRevealMode.Peek };
            var confirm = new PasswordBox { Header = "Confirmar contraseña", PasswordRevealMode = PasswordRevealMode.Peek };
            var hint = new TextBlock
            {
                Text = "La contraseña se guarda solo protegida por Windows en este equipo para ejecutar las copias. El archivo .pabackup sigue necesitando esa contraseña y podrás restaurarlo después de formatear si la recuerdas.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush
            };
            var panel = new StackPanel { Spacing = 10, Width = 460 };
            panel.Children.Add(toggle); panel.Children.Add(new TextBlock { Text = "Destino", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(destination); panel.Children.Add(frequency); panel.Children.Add(versions);
            panel.Children.Add(password); panel.Children.Add(confirm); panel.Children.Add(hint);
            var dialog = CreateDialog("Copias automáticas de configuración", panel, "Guardar", "Cancelar");
            dialog.SecondaryButtonText = LocalizationService.T("Elegir carpeta…");
            var choice = await dialog.ShowAsync();
            enabled = toggle.IsOn;
            interval = int.Parse(((ComboBoxItem)frequency.SelectedItem).Tag!.ToString()!);
            retention = (int)Math.Round(versions.Value);

            if (choice == ContentDialogResult.Secondary)
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                try
                {
                    var selected = await picker.PickSingleFolderAsync();
                    if (selected is not null) directory = selected.Path;
                }
                finally { RestoreMainWindowFocus(); }
                continue;
            }
            if (choice != ContentDialogResult.Primary) return;
            if (enabled && (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)))
            {
                await ShowMessageAsync("Selecciona una carpeta", "Elige una carpeta existente para guardar las copias automáticas.");
                continue;
            }
            if (enabled && !string.IsNullOrEmpty(password.Password)
                && (password.Password.Length < PasswordService.MinimumPasswordLength || password.Password != confirm.Password))
            {
                await ShowMessageAsync("Contraseña no válida", "La contraseña debe tener al menos 12 caracteres y coincidir con su confirmación.");
                continue;
            }
            if (enabled && string.IsNullOrEmpty(password.Password) && !ConfigurationBackupPasswordStore.Exists())
            {
                await ShowMessageAsync("Introduce una contraseña", "Indica una contraseña de al menos 12 caracteres para cifrar las copias automáticas.");
                continue;
            }

            if (!enabled)
            {
                _state.ConfigurationBackupDirectory = null;
                _state.ConfigurationBackupLastRunUtc = null;
                _state.ConfigurationBackupLastError = null;
                ConfigurationBackupPasswordStore.Delete();
                AddActivity("ProtectedApp", "Copias automáticas de configuración desactivadas");
            }
            else
            {
                if (!string.IsNullOrEmpty(password.Password)) ConfigurationBackupPasswordStore.Save(password.Password);
                _state.ConfigurationBackupDirectory = Path.GetFullPath(directory!);
                _state.ConfigurationBackupIntervalHours = interval;
                _state.ConfigurationBackupRetentionCount = retention;
                _state.ConfigurationBackupLastRunUtc = null;
                _state.ConfigurationBackupLastError = null;
                AddActivity("ProtectedApp", "Copias automáticas de configuración configuradas");
            }
            RefreshConfigurationBackupStatus();
            await SaveAsync(synchronizeGuardian: false);
            if (enabled) _ = RunScheduledConfigurationBackupsAsync(force: true);
            return;
        }
    }

    private async void RunConfigurationBackupNow_Click(object sender, RoutedEventArgs e)
    {
        var created = await RunScheduledConfigurationBackupsAsync(force: true);
        await ShowMessageAsync(created > 0 ? "Copia creada" : "No se pudo crear la copia",
            created > 0 ? "La copia automática de configuración se creó correctamente." : _state.ConfigurationBackupLastError ?? "Configura primero las copias automáticas.");
    }

    private async Task<int> RunScheduledConfigurationBackupsAsync(bool force)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(_state.ConfigurationBackupDirectory)
            || Interlocked.Exchange(ref _configurationBackupRunning, 1) != 0) return 0;
        try
        {
            var password = ConfigurationBackupPasswordStore.Load();
            if (string.IsNullOrEmpty(password))
            {
                _state.ConfigurationBackupLastError = "No se encontró la contraseña local de las copias automáticas.";
                RefreshConfigurationBackupStatus();
                return 0;
            }
            var interval = TimeSpan.FromHours(Math.Clamp(_state.ConfigurationBackupIntervalHours, 1, 24 * 7));
            if (!force && _state.ConfigurationBackupLastRunUtc is { } last && DateTime.UtcNow - last < interval) return 0;
            try
            {
                var directory = Path.GetFullPath(_state.ConfigurationBackupDirectory);
                Directory.CreateDirectory(directory);
                var snapshot = CaptureBackupSnapshot();
                var bytes = await BackupService.CreateAsync(snapshot, password);
                var path = Path.Combine(directory, $"ProtectedApp-Configuration-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.pabackup");
                var temporary = path + ".tmp-" + Environment.ProcessId;
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes);
                    File.Move(temporary, path);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
                CleanupConfigurationBackups(directory, _state.ConfigurationBackupRetentionCount);
                _state.ConfigurationBackupLastRunUtc = DateTime.UtcNow;
                _state.ConfigurationBackupLastError = null;
                AddActivity("ProtectedApp", "Copia automática de configuración creada");
                await SaveAsync(synchronizeGuardian: false);
                RefreshConfigurationBackupStatus();
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or CryptographicException)
            {
                _state.ConfigurationBackupLastError = ex.Message;
                AddActivity("ProtectedApp", "No se pudo crear la copia automática de configuración");
                RefreshConfigurationBackupStatus();
                await SaveAsync(synchronizeGuardian: false);
                return 0;
            }
        }
        finally { Interlocked.Exchange(ref _configurationBackupRunning, 0); }
    }

    private static void CleanupConfigurationBackups(string directory, int retention)
    {
        var keep = Math.Clamp(retention, 1, 20);
        foreach (var stale in Directory.EnumerateFiles(directory, "ProtectedApp-Configuration-*.pabackup")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Skip(keep))
        {
            try { File.Delete(stale); } catch { }
        }
    }

    private BackupSnapshot CaptureBackupSnapshot() => new()
    {
        ExportedAtUtc = DateTimeOffset.UtcNow,
        StartWithWindows = StartupService.IsEnabled(),
        ShowTrayIcon = _state.ShowTrayIcon,
        LockPanelWhenHidden = _state.LockPanelWhenHidden,
        ThemePreference = _state.ThemePreference,
        LanguagePreference = _state.LanguagePreference,
        ManagementAutoLockMinutes = _state.ManagementAutoLockMinutes,
        PollIntervalMilliseconds = _state.PollIntervalMilliseconds,
        OpenVaultsReadOnlyByDefault = _state.OpenVaultsReadOnlyByDefault,
        VaultBackupDirectory = _state.VaultBackupDirectory,
        VaultBackupIntervalHours = _state.VaultBackupIntervalHours,
        VaultBackupRetentionCount = _state.VaultBackupRetentionCount,
        VaultBackupNotificationsEnabled = _state.VaultBackupNotificationsEnabled,
        CloseWarningNotificationsEnabled = _state.CloseWarningNotificationsEnabled,
        SecurityAlertsEnabled = _state.SecurityAlertsEnabled,
        ImmediateLockHotkey = _state.ImmediateLockHotkey,
        Applications = Applications.ToList(),
        Folders = Folders.ToList(),
        Vaults = Vaults.ToList(),
        ActivityHistory = _activityHistory.ToList()
    };

    private static bool IsForbiddenBackupTarget(string path)
    {
        var name = Path.GetFileName(path);
        return (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && PathsEqual(path, Environment.ProcessPath))
            || name.Equals("ProtectedApp.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ProtectedApp.Guardian.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ProtectedApp.Gate.exe", StringComparison.OrdinalIgnoreCase)
            || IsCriticalWindowsExecutable(path);
    }
}
