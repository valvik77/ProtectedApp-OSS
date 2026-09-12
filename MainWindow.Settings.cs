using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Models;
using ProtectedApp.Services;
using System.Security.Cryptography;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private void SettingsColumns_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.Children.Count < 2) return;
        var compact = e.NewSize.Width < 1000;
        if (grid.RowDefinitions.Count < 3)
        {
            grid.RowDefinitions.Clear();
            for (var i = 0; i < 3; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn((FrameworkElement)grid.Children[1], compact ? 0 : 1);
        Grid.SetRow((FrameworkElement)grid.Children[1], compact ? 1 : 0);
        if (grid.Children.Count > 2)
            Grid.SetRow((FrameworkElement)grid.Children[2], compact ? 2 : 1);
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        try
        {
            StartupService.SetEnabled(StartupToggle.IsOn);
            _state.StartWithWindows = StartupToggle.IsOn;
            await SaveAsync();
        }
        catch (Exception ex)
        {
            _updatingControls = true;
            StartupToggle.IsOn = !StartupToggle.IsOn;
            _updatingControls = false;
            await ShowMessageAsync("No se pudo cambiar el inicio automático", LocalizationService.UserFacingError(ex));
        }
    }

    private async void TrayIconToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        _state.ShowTrayIcon = TrayIconToggle.IsOn;
        _tray.SetVisible(_state.ShowTrayIcon);
        await SaveAsync();
        AddActivity("ProtectedApp", _state.ShowTrayIcon ? "Icono de bandeja mostrado" : "Icono de bandeja ocultado");
    }

    private async void ManagementAutoLockBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingControls || ManagementAutoLockBox.SelectedItem is not ComboBoxItem item
            || !int.TryParse(item.Tag?.ToString(), out var minutes)) return;
        _state.ManagementAutoLockMinutes = minutes;
        RecordManagementActivity();
        AddActivity("ProtectedApp", minutes == 0 ? "Bloqueo automático del panel desactivado" : $"Bloqueo automático del panel configurado en {minutes} min");
        await SaveAsync();
    }

    private async void LockPanelWhenHiddenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        _state.LockPanelWhenHidden = LockPanelWhenHiddenToggle.IsOn;
        AddActivity("ProtectedApp", _state.LockPanelWhenHidden ? "Bloqueo del panel al cerrar o salir activado" : "Bloqueo del panel al cerrar o salir desactivado");
        await SaveAsync();
    }

    private async void VaultReadOnlyByDefaultToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        _state.OpenVaultsReadOnlyByDefault = VaultReadOnlyByDefaultToggle.IsOn;
        AddActivity("ProtectedApp", _state.OpenVaultsReadOnlyByDefault ? "Doble clic en bóvedas configurado para consulta segura" : "Doble clic en bóvedas configurado para editar");
        await SaveAsync();
    }

    private async void VaultDriveLetterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingControls || VaultDriveLetterBox.SelectedItem is not ComboBoxItem item) return;
        var configured = item.Tag?.ToString();
        _state.PreferredVaultDriveLetter = string.IsNullOrWhiteSpace(configured) ? null : configured.ToUpperInvariant();
        _vaultService.PreferredVirtualDriveLetter = TryGetPreferredVaultDriveLetter();
        AddActivity("ProtectedApp", _vaultService.PreferredVirtualDriveLetter is { } letter
            ? $"Letra preferida para bóvedas configurada en {letter}:"
            : "Letra de unidad de bóvedas configurada automáticamente");
        await SaveAsync();
    }

    private char? TryGetPreferredVaultDriveLetter()
    {
        var value = _state.PreferredVaultDriveLetter;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var letter = char.ToUpperInvariant(value.Trim()[0]);
        return letter is >= 'D' and <= 'Z' ? letter : null;
    }

    private void SelectPreferredVaultDriveLetter(string? preferred)
    {
        var expected = TryGetPreferredVaultDriveLetter()?.ToString() ?? string.Empty;
        foreach (var candidate in VaultDriveLetterBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(candidate.Tag?.ToString() ?? string.Empty, expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                VaultDriveLetterBox.SelectedItem = candidate;
                return;
            }
        }
        VaultDriveLetterBox.SelectedIndex = 0;
    }

    private async void VaultBackupNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        _state.VaultBackupNotificationsEnabled = VaultBackupNotificationsToggle.IsOn;
        AddActivity("ProtectedApp", _state.VaultBackupNotificationsEnabled
            ? "Avisos de copias de bóvedas activados"
            : "Avisos de copias de bóvedas silenciados");
        await SaveAsync();
    }

    private async void CloseWarningNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        _state.CloseWarningNotificationsEnabled = CloseWarningNotificationsToggle.IsOn;
        AddActivity("ProtectedApp", _state.CloseWarningNotificationsEnabled
            ? "Avisos previos de cierre automático activados"
            : "Avisos previos de cierre automático desactivados");
        await SaveAsync();
    }

    private async void SecurityAlertsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        _state.SecurityAlertsEnabled = SecurityAlertsToggle.IsOn;
        AddActivity("ProtectedApp", _state.SecurityAlertsEnabled
            ? "Alertas de seguridad activadas"
            : "Alertas de seguridad silenciadas");
        await SaveAsync();
    }

    private async void ConfigureTamperWebhook_Click(object sender, RoutedEventArgs e)
    {
        if (!_guardianManaged) { await ShowMessageAsync("Guardian no disponible", "Instala y activa Guardian antes de configurar alertas remotas."); return; }
        if (!await VerifyMasterAsync("Configurar alerta remota") || string.IsNullOrWhiteSpace(_guardianToken)) return;
        var current = await _guardianClient.GetTamperWebhookAsync(_guardianToken);
        if (!current.Success) { await ShowMessageAsync("No se pudo consultar", current.Error ?? "Guardian rechazó la solicitud."); return; }
        var enabled = new ToggleSwitch { Header = "Activar webhook de manipulación", IsOn = current.WebhookEnabled };
        var url = new TextBox { Header = "URL HTTPS", Text = current.WebhookUrl ?? string.Empty, PlaceholderText = "https://ejemplo.com/webhook" };
        var installationId = new TextBox { Header = "Identificador de instalación", Text = current.WebhookInstallationId ?? string.Empty, IsReadOnly = true };
        var useHmac = new ToggleSwitch { Header = "Firmar el contenido con HMAC-SHA256", IsOn = current.WebhookUseHmac };
        var secret = new PasswordBox { Header = "Secreto HMAC (vacío conserva el actual)", PasswordRevealMode = PasswordRevealMode.Peek };
        var warning = new TextBlock { Text = "Solo se envían códigos de manipulación y recuperación fallida. Es una alerta informativa; un administrador podría desactivarla junto con Guardian.", TextWrapping = TextWrapping.Wrap, Foreground = Application.Current.Resources["MutedTextBrush"] as Brush };
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(enabled); panel.Children.Add(url); panel.Children.Add(installationId); panel.Children.Add(useHmac); panel.Children.Add(secret); panel.Children.Add(warning);
        var dialog = CreateDialog("Alerta remota de manipulación", panel, "Guardar", "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var response = await _guardianClient.ConfigureTamperWebhookAsync(enabled.IsOn, url.Text.Trim(), useHmac.IsOn, secret.Password, _guardianToken);
        if (!response.Success) await ShowMessageAsync("No se pudo guardar", LocalizationService.UserFacingMessage(response.Error, "Guardian rechazó la configuración."));
        else AddActivity("ProtectedApp", enabled.IsOn ? "Webhook de manipulación configurado" : "Webhook de manipulación desactivado");
    }

    private async void WindowsHelloToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;
        _state.UseWindowsHello = WindowsHelloToggle.IsOn;
        await SaveAsync();
        AddActivity("ProtectedApp", WindowsHelloToggle.IsOn ? "Desbloqueo con Windows Hello activado" : "Desbloqueo con Windows Hello desactivado");
    }

    private async Task UpdateWindowsHelloStatusAsync()
    {
        var available = await WindowsHelloService.IsAvailableAsync();
        WindowsHelloStatusText.Text = await WindowsHelloService.GetStatusDescriptionAsync();
        if (!available && WindowsHelloToggle.IsOn)
        {
            _updatingControls = true;
            WindowsHelloToggle.IsOn = false;
            _state.UseWindowsHello = false;
            _updatingControls = false;
        }
    }

    private async void TpmProtectionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls) return;

        var enable = TpmProtectionToggle.IsOn;
        if (!await VerifyMasterAsync(enable
                ? "Activar protección TPM de la configuración"
                : "Desactivar protección TPM de la configuración"))
        {
            RestoreTpmProtectionToggle();
            return;
        }

        if (enable)
        {
            var confirmation = CreateDialog(
                "Vincular configuración al TPM",
                "La configuración de ProtectedApp se cifrará con una clave no exportable del TPM de este equipo. " +
                "No protege ni modifica las bóvedas. Si se restablece el TPM, se reinstala Windows o se cambia la placa base, deberás restaurar una copia o configurar ProtectedApp de nuevo.",
                "Activar", "Cancelar");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                RestoreTpmProtectionToggle();
                return;
            }
        }

        try
        {
            await SaveAsync(synchronizeGuardian: false);
            await _store.SetTpmProtectionAsync(_state, enable);
            await SaveAsync();
            AddActivity("ProtectedApp", enable
                ? "Protección TPM de la configuración activada"
                : "Protección TPM de la configuración desactivada");
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            await ShowMessageAsync("TPM no disponible", enable
                ? "No se pudo activar la protección TPM. Comprueba que el equipo tenga un TPM disponible y que Windows permita crear claves de plataforma. La configuración actual se ha conservado con la protección anterior."
                : "No se pudo retirar la protección TPM. La configuración no se ha modificado.");
            RestoreTpmProtectionToggle();
        }
        finally
        {
            RefreshTpmProtectionStatus();
        }
    }

    private void RestoreTpmProtectionToggle()
    {
        _updatingControls = true;
        TpmProtectionToggle.IsOn = _store.IsTpmProtectionEnabled;
        _updatingControls = false;
    }

    private void RefreshTpmProtectionStatus()
    {
        TpmProtectionStatusText.Text = LocalizationService.T(_store.IsTpmProtectionEnabled
            ? "Activa: una clave no exportable del TPM protege el estado local de este equipo."
            : "Usa una clave no exportable del TPM para proteger el estado local.");
    }

    private void SelectManagementAutoLock(int minutes) => ManagementAutoLockBox.SelectedItem = ManagementAutoLockBox.Items.Cast<ComboBoxItem>()
        .FirstOrDefault(item => item.Tag?.ToString() == minutes.ToString()) ?? ManagementAutoLockBox.Items.Cast<ComboBoxItem>().First();

    private void RecordManagementActivity()
    {
        if (_sessionUnlocked) _lastManagementActivityUtc = DateTimeOffset.UtcNow;
    }

    private async void ManagementAutoLockTimer_Tick(object? sender, object e)
    {
        var minutes = _state.ManagementAutoLockMinutes;
        if (!_initialized || !_sessionUnlocked || !_appWindow.IsVisible || _openDialogCount > 0 || _dialogGate.CurrentCount == 0 || minutes <= 0
            || DateTimeOffset.UtcNow - _lastManagementActivityUtc < TimeSpan.FromMinutes(minutes)) return;
        _sessionUnlocked = false;
        AddActivity("ProtectedApp", $"Panel bloqueado automáticamente tras {minutes} min sin actividad");
        await SaveAsync();
        HideToTray(showNotification: false);
    }

    private async void PollIntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingControls || PollIntervalBox.SelectedItem is not ComboBoxItem item || !int.TryParse(item.Tag?.ToString(), out var interval)) return;
        _monitor.IntervalMilliseconds = interval;
        _state.PollIntervalMilliseconds = interval;
        await SaveAsync();
        RefreshStats();
    }

    private void SelectPollInterval(int interval)
    {
        PollIntervalBox.SelectedItem = PollIntervalBox.Items.Cast<ComboBoxItem>()
            .OrderBy(item => Math.Abs(int.Parse(item.Tag!.ToString()!) - interval)).First();
    }

    private void RefreshVaultBackupStatus()
    {
        if (VaultBackupStatusText is null || RunVaultBackupButton is null) return;
        RefreshVaultScheduledBackupStatus();
        var configured = !string.IsNullOrWhiteSpace(_state.VaultBackupDirectory);
        RunVaultBackupButton.IsEnabled = configured;
        if (!configured)
        {
            VaultBackupStatusText.Text = "Sin configurar.";
            return;
        }

        var interval = Math.Clamp(_state.VaultBackupIntervalHours, 1, 24 * 7);
        var lastRun = _state.VaultBackupLastRunUtc is { } last
            ? $" · última copia {last.ToLocalTime():dd/MM HH:mm}"
            : " · pendiente de la primera copia";
        var copies = Vaults.Sum(vault => vault.ScheduledBackupCount);
        var size = Vaults.Sum(vault => vault.ScheduledBackupSizeBytes);
        VaultBackupStatusText.Text = $"Cada {(interval == 24 ? "día" : interval == 24 * 7 ? "semana" : $"{interval} h")} · {copies} copias · {FormatBackupSize(size)} · conserva {Math.Clamp(_state.VaultBackupRetentionCount, 1, 20)} versiones{lastRun}";
    }

    private void RefreshVaultScheduledBackupStatus(bool recordTransitions = false)
    {
        var configured = !string.IsNullOrWhiteSpace(_state.VaultBackupDirectory);
        var interval = TimeSpan.FromHours(Math.Clamp(_state.VaultBackupIntervalHours, 1, 24 * 7));
        foreach (var vault in Vaults)
        {
            vault.ScheduledBackupConfigured = configured;
            vault.ScheduledBackupDirectory = configured ? _state.VaultBackupDirectory : null;
            var info = configured ? _vaultService.InspectScheduledBackups(vault, _state.VaultBackupDirectory) : new VaultScheduledBackupInfo(0, null, 0);
            vault.ScheduledBackupCount = info.Count;
            vault.LastScheduledBackupUtc = info.LastCreatedUtc;
            vault.ScheduledBackupSizeBytes = info.TotalSizeBytes;
            var health = !configured ? VaultScheduledBackupHealth.NotConfigured
                : _vaultBackupFailures.ContainsKey(vault.Id) ? VaultScheduledBackupHealth.Failed
                : info.Count == 0 ? VaultScheduledBackupHealth.Pending
                : info.LastCreatedUtc is { } last && DateTime.UtcNow - last > interval ? VaultScheduledBackupHealth.Overdue
                : VaultScheduledBackupHealth.Healthy;
            vault.ScheduledBackupError = _vaultBackupFailures.GetValueOrDefault(vault.Id);
            UpdateVaultBackupHealth(vault, health, recordTransitions);
        }
    }

    private static string FormatBackupSize(long bytes) => bytes <= 0 ? "0 KB" : bytes < 1024 * 1024
        ? $"{Math.Max(1, bytes / 1024)} KB"
        : $"{bytes / (1024d * 1024d):0.0} MB";

    private async void OpenVaultBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _state.VaultBackupDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            await ShowMessageAsync("Sin destino configurado", "Configura primero una carpeta para las copias de bóvedas.");
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { await ShowMessageAsync("No se pudo abrir la carpeta", LocalizationService.UserFacingError(ex)); }
    }

    private async void CleanupVaultBackups_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_state.VaultBackupDirectory))
        {
            await ShowMessageAsync("Sin destino configurado", "Configura primero una carpeta para las copias de bóvedas.");
            return;
        }
        if (!await VerifyMasterAsync("Limpiar copias antiguas")) return;
        RefreshVaultScheduledBackupStatus();
        var excess = Vaults.Sum(vault => Math.Max(0, vault.ScheduledBackupCount - Math.Clamp(_state.VaultBackupRetentionCount, 1, 20)));
        if (excess == 0)
        {
            await ShowMessageAsync("No hay copias que limpiar", "Todas las bóvedas ya conservan solo las versiones configuradas.");
            return;
        }
        var confirmation = CreateDialog("Limpiar copias antiguas",
            $"Se eliminarán hasta {excess} copias antiguas. Se conservarán las {_state.VaultBackupRetentionCount} versiones más recientes de cada bóveda.", "Limpiar", "Cancelar");
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        var deleted = 0;
        var freed = 0L;
        var failed = 0;
        foreach (var vault in Vaults)
        {
            var result = _vaultService.CleanupScheduledBackups(vault, _state.VaultBackupDirectory, _state.VaultBackupRetentionCount);
            deleted += result.DeletedCount;
            freed += result.FreedBytes;
            failed += result.FailedCount;
        }
        RefreshVaultBackupStatus();
        AddActivity("ProtectedApp", $"Limpieza de copias de bóvedas: {deleted} eliminadas, {FormatBackupSize(freed)} liberados");
        await SaveAsync();
        await ShowMessageAsync(failed == 0 ? "Limpieza completada" : "Limpieza parcial",
            $"Se eliminaron {deleted} copias y se liberaron {FormatBackupSize(freed)}.{(failed == 0 ? string.Empty : $" {failed} copia(s) no se pudieron eliminar.")}");
    }

    private void UpdateVaultBackupHealth(VaultContainer vault, VaultScheduledBackupHealth health, bool recordTransition)
    {
        var changed = !_vaultBackupHealthStates.TryGetValue(vault.Id, out var previous) || previous != health;
        _vaultBackupHealthStates[vault.Id] = health;
        vault.ScheduledBackupHealth = health;
        if (!recordTransition || !changed || !_initialized) return;

        var message = health switch
        {
            VaultScheduledBackupHealth.Pending => "Copia programada pendiente: todavía no existe ninguna versión",
            VaultScheduledBackupHealth.Overdue => "Copia programada vencida: la última versión superó la frecuencia configurada",
            VaultScheduledBackupHealth.Failed => $"No se pudo crear la copia programada: {vault.ScheduledBackupError}",
            VaultScheduledBackupHealth.Healthy when previous is VaultScheduledBackupHealth.Pending or VaultScheduledBackupHealth.Overdue or VaultScheduledBackupHealth.Failed
                => "Copia programada recuperada",
            _ => null
        };
        if (message is not null) AddActivity(vault.Name, message);
        if (message is not null && _state.VaultBackupNotificationsEnabled)
            _tray.ShowBalloon("Protección de bóvedas", $"{vault.Name}: {message}");
    }

    private async void ConfigureVaultBackups_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Configurar copias de bóvedas")) return;
        var backupsEnabled = !string.IsNullOrWhiteSpace(_state.VaultBackupDirectory);
        var destinationDirectory = _state.VaultBackupDirectory;
        var intervalHours = Math.Clamp(_state.VaultBackupIntervalHours, 1, 24 * 7);
        var retentionCount = Math.Clamp(_state.VaultBackupRetentionCount, 1, 20);

        while (true)
        {
            // Cada iteración crea controles nuevos. Un ContentDialog se cierra antes de
            // abrir FolderPicker; reutilizar sus controles al volver podía dejar la
            // ventana en un estado inválido al cancelar el selector.
            var enabled = new ToggleSwitch { Header = "Activar copias programadas", IsOn = backupsEnabled };
            var destination = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(destinationDirectory) ? "No se ha elegido una carpeta." : destinationDirectory,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush
            };
            var frequency = new ComboBox { Header = "Frecuencia" };
            frequency.Items.Add(new ComboBoxItem { Content = "Cada día", Tag = "24" });
            frequency.Items.Add(new ComboBoxItem { Content = "Cada semana", Tag = "168" });
            frequency.SelectedIndex = intervalHours >= 24 * 7 ? 1 : 0;
            var retention = new NumberBox
            {
                Header = "Versiones por bóveda", Minimum = 1, Maximum = 20,
                Value = retentionCount, SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            var panel = new StackPanel { Spacing = 10, Width = 440 };
            panel.Children.Add(enabled);
            panel.Children.Add(new TextBlock { Text = "Destino", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(destination);
            panel.Children.Add(frequency);
            panel.Children.Add(retention);
            panel.Children.Add(new TextBlock
            {
                Text = "Las bóvedas abiertas se omiten. Cada copia es un contenedor .pavault completo e independiente.",
                FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush
            });
            var dialog = CreateDialog("Copias programadas de bóvedas", panel, "Guardar", "Cancelar");
            dialog.SecondaryButtonText = LocalizationService.T("Elegir carpeta…");
            var choice = await dialog.ShowAsync();

            backupsEnabled = enabled.IsOn;
            intervalHours = int.Parse(((ComboBoxItem)frequency.SelectedItem).Tag!.ToString()!);
            retentionCount = (int)Math.Round(retention.Value);

            if (choice == ContentDialogResult.Secondary)
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                Windows.Storage.StorageFolder? selected = null;
                Exception? pickerError = null;
                try
                {
                    selected = await picker.PickSingleFolderAsync();
                }
                catch (Exception ex)
                {
                    pickerError = ex;
                }
                finally
                {
                    RestoreMainWindowFocus();
                }

                if (pickerError is not null)
                {
                    AddActivity("ProtectedApp", $"No se pudo abrir el selector de copias de bóvedas: {pickerError.Message}");
                    await ShowMessageAsync("No se pudo elegir la carpeta", pickerError.Message);
                }
                else if (selected is not null)
                {
                    destinationDirectory = selected.Path;
                }

                // Cancelar no cambia nada: simplemente vuelve al mismo diálogo.
                continue;
            }
            if (choice != ContentDialogResult.Primary) return;
            if (backupsEnabled && (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory)))
            {
                await ShowMessageAsync("Selecciona una carpeta", "Elige una carpeta existente para guardar las copias programadas.");
                continue;
            }

            _state.VaultBackupDirectory = backupsEnabled ? Path.GetFullPath(destinationDirectory!) : null;
            _state.VaultBackupIntervalHours = intervalHours;
            _state.VaultBackupRetentionCount = retentionCount;
            _state.VaultBackupLastRunUtc = null;
            RefreshVaultBackupStatus();
            AddActivity("ProtectedApp", backupsEnabled
                ? "Copias programadas de bóvedas configuradas"
                : "Copias programadas de bóvedas desactivadas");
            await SaveAsync();
            if (backupsEnabled) _ = RunScheduledVaultBackupsAsync(force: false);
            return;
        }
    }

    private async void RunVaultBackupNow_Click(object sender, RoutedEventArgs e)
    {
        var created = await RunScheduledVaultBackupsAsync(force: true);
        if (created > 0) await ShowMessageAsync("Copias creadas", $"Se crearon {created} copias de bóveda.");
        else if (!string.IsNullOrWhiteSpace(_state.VaultBackupDirectory))
            await ShowMessageAsync("Sin copias pendientes", "No hay bóvedas cerradas disponibles para copiar ahora.");
    }

    private async void VaultBackupTimer_Tick(object? sender, object e) => await RunScheduledVaultBackupsAsync(force: false);

    private async Task<int> RunScheduledVaultBackupsAsync(bool force)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(_state.VaultBackupDirectory)
            || Interlocked.Exchange(ref _vaultBackupRunning, 1) != 0) return 0;
        try
        {
            var interval = TimeSpan.FromHours(Math.Clamp(_state.VaultBackupIntervalHours, 1, 24 * 7));
            if (!force && _state.VaultBackupLastRunUtc is { } last && DateTime.UtcNow - last < interval) return 0;
            if (!await _vaultOperationGate.WaitAsync(0)) return 0;
            try
            {
                var created = 0;
                foreach (var vault in Vaults.Where(candidate => !candidate.IsMounted && !candidate.IsClosing).ToArray())
                {
                    var result = await _vaultService.CreateScheduledBackupAsync(vault, _state.VaultBackupDirectory,
                        _state.VaultBackupRetentionCount);
                    if (result.Success)
                    {
                        created++;
                        _vaultBackupFailures.Remove(vault.Id);
                        AddActivity(vault.Name, "Copia programada de bóveda creada");
                    }
                    else
                    {
                        _vaultBackupFailures[vault.Id] = result.Error ?? "Error desconocido";
                    }
                }
                if (created > 0) _state.VaultBackupLastRunUtc = DateTime.UtcNow;
                RefreshVaultScheduledBackupStatus(recordTransitions: true);
                RefreshVaultBackupStatus();
                await SaveAsync();
                return created;
            }
            finally { _vaultOperationGate.Release(); }
        }
        finally { Interlocked.Exchange(ref _vaultBackupRunning, 0); }
    }

    private async void ChangeMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Confirmar contraseña actual")) return;
        await CreateMasterPasswordAsync("Nueva contraseña maestra", "Este cambio no modifica las contraseñas propias de las aplicaciones.");
        await ShowMessageAsync("Contraseña actualizada", "La nueva contraseña maestra ya está activa.");
    }
}
