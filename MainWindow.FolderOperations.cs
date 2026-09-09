using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ProtectedApp.Models;
using ProtectedApp.Services;
using ProtectedApp.Shared;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async Task<bool> ConvertFolderToVaultAsync(string selectedPath, string? suggestedName = null)
    {
        var path = Path.GetFullPath(selectedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(path))
        {
            await ShowMessageAsync("Carpeta no disponible", "La carpeta seleccionada ya no existe.");
            return false;
        }

        var name = new TextBox
        {
            Header = "Nombre",
            Text = string.IsNullOrWhiteSpace(suggestedName) ? Path.GetFileName(path) : suggestedName
        };
        var destinationDirectory = Directory.GetParent(path)?.FullName
            ?? Path.GetPathRoot(Path.GetFullPath(selectedPath))
            ?? path;
        var destination = new TextBox
        {
            Header = "Guardar como",
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var chooseDestination = new Button
        {
            Content = "Cambiar ubicación…",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var destinationRow = new Grid { ColumnSpacing = 8 };
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chooseDestination, 1);
        destinationRow.Children.Add(destination);
        destinationRow.Children.Add(chooseDestination);
        void UpdateDestinationPath() => destination.Text = Path.Combine(
            destinationDirectory,
            SanitizeFileName(name.Text.Trim()) + ".pavault");
        UpdateDestinationPath();
        name.TextChanged += (_, _) => UpdateDestinationPath();
        chooseDestination.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            Windows.Storage.StorageFolder? selected = null;
            chooseDestination.IsEnabled = false;
            try { selected = await picker.PickSingleFolderAsync(); }
            finally
            {
                chooseDestination.IsEnabled = true;
                RestoreMainWindowFocus();
            }
            if (selected is null) return;
            destinationDirectory = selected.Path;
            UpdateDestinationPath();
        };
        var description = new TextBox { Header = "Descripción", PlaceholderText = "Opcional", MaxLength = 300 };
        var password = new PasswordBox { Header = "Contraseña de la bóveda", PlaceholderText = "Mínimo 12 caracteres", PasswordRevealMode = PasswordRevealMode.Peek };
        var confirmation = new PasswordBox { Header = "Confirmar contraseña", PasswordRevealMode = PasswordRevealMode.Peek };
        var minutes = new NumberBox
        {
            Header = "Bloquear automáticamente después de (minutos)",
            Minimum = 1, Maximum = 10_080, Value = 30, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var error = CreateDialogErrorText();
        var panel = new StackPanel { Width = 420, Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"Carpeta de origen: {path}", Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 118, 118, 118)), FontSize = 10, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(name);
        panel.Children.Add(destinationRow);
        panel.Children.Add(description);
        panel.Children.Add(password);
        panel.Children.Add(confirmation);
        panel.Children.Add(minutes);
        panel.Children.Add(new TextBlock { Text = "Se creará y verificará una bóveda .pavault cifrada. La carpeta original solo se eliminará si lo confirmas después de verificar la bóveda.", Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 118, 118, 118)), FontSize = 10, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(error);
        var dialog = CreateDialog("Convertir carpeta en bóveda", panel, "Guardar", "Cancelar");
        var valid = false;
        string? vaultPath = null;
        dialog.PrimaryButtonClick += (dialogSender, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = LocalizationService.T("Indica un nombre."); args.Cancel = true; return; }
            if (password.Password != confirmation.Password) { error.Text = LocalizationService.T("Las contraseñas no coinciden."); args.Cancel = true; return; }
            if (password.Password.Length < PasswordService.MinimumPasswordLength) { error.Text = LocalizationService.T("La contraseña de la bóveda debe tener al menos 12 caracteres."); args.Cancel = true; return; }
            if (double.IsNaN(minutes.Value) || minutes.Value < 1 || minutes.Value > 10_080)
            { error.Text = LocalizationService.T("El tiempo debe estar entre 1 y 10.080 minutos."); args.Cancel = true; return; }
            vaultPath = Path.GetFullPath(destination.Text);
            if (vaultPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { error.Text = LocalizationService.T("Guarda la bóveda fuera de la carpeta que se va a cifrar."); args.Cancel = true; return; }
            if (Vaults.Any(candidate => PathsEqual(candidate.VaultFilePath ?? string.Empty, vaultPath)))
            { error.Text = LocalizationService.T("Ya existe una bóveda registrada con esa ubicación. Elige otra ubicación o retira primero la referencia anterior."); args.Cancel = true; return; }
            if (File.Exists(vaultPath) && new FileInfo(vaultPath).Length > 0)
            { error.Text = LocalizationService.T("Ya existe un archivo con ese nombre. Cambia el nombre o la ubicación."); args.Cancel = true; return; }
            valid = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !valid) return false;

        var vault = new VaultContainer
        {
            Name = name.Text.Trim(), Description = description.Text.Trim(),
            AutoLockMinutes = (int)minutes.Value, VaultFilePath = vaultPath!
        };
        if (!await _vaultService.CreateVaultFromFolderAsync(vault, path, vaultPath!, password.Password))
        {
            await ShowMessageAsync("No se pudo convertir", LocalizationService.UserFacingMessage(_vaultService.LastError, "No se pudo crear y verificar la bóveda cifrada."));
            return false;
        }

        vault.PropertyChanged += Vault_PropertyChanged;
        Vaults.Add(vault);
        RefreshVaults();
        AddActivity(vault.Name, $"Bóveda cifrada creada desde {path}");
        await SaveAsync();

        var removeOriginal = CreateDialog("Bóveda verificada", new TextBlock
        {
            Text = "La bóveda se ha creado y verificado correctamente. Para completar la conversión y evitar conservar una copia sin cifrar, elimina ahora la carpeta original. Esta acción no se puede deshacer.",
            TextWrapping = TextWrapping.Wrap
        }, "Eliminar carpeta original", "Conservar por ahora");
        if (await removeOriginal.ShowAsync() != ContentDialogResult.Primary)
        {
            await ShowMessageAsync("Conversión pendiente", "La bóveda ya está protegida, pero la carpeta original sigue existiendo sin cifrar. Elimínala manualmente cuando hayas comprobado el contenido.");
            return true;
        }
        var cleanup = await StageAndDeleteOriginalFolderAsync(path);
        if (cleanup.Success)
        {
            AddActivity(vault.Name, "Carpeta original eliminada tras verificar la conversión");
            await SaveAsync();
        }
        else if (cleanup.StagedPath is null)
        {
            await ShowMessageAsync("Bóveda creada; carpeta original conservada",
                $"No se pudo preparar la carpeta original para eliminarla. No se eliminó ningún archivo. Cierra las aplicaciones que la usen y revisa sus permisos:{Environment.NewLine}{PathForDisplay(path)}");
        }
        else
        {
            await ShowMessageAsync("Bóveda creada; limpieza pendiente",
                $"La carpeta original se movió a una ubicación de limpieza antes de borrar su contenido, pero Windows no terminó la operación. La bóveda cifrada ya fue verificada. Revisa y elimina manualmente la carpeta restante:{Environment.NewLine}{PathForDisplay(cleanup.StagedPath)}");
        }
        return true;
    }

    private static Task<StagedFolderDeletion> StageAndDeleteOriginalFolderAsync(string originalPath) => Task.Run(() =>
    {
        var parent = Directory.GetParent(originalPath)?.FullName
            ?? throw new IOException("No se pudo determinar la carpeta contenedora.");
        var stagedPath = Path.Combine(parent,
            $"{Path.GetFileName(originalPath)}.protectedapp-pending-delete-{Guid.NewGuid():N}");
        try
        {
            // Renaming inside the same parent is atomic.  If this step fails, the
            // source remains untouched; recursive deletion never starts on it.
            Directory.Move(originalPath, stagedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StagedFolderDeletion(false, null);
        }

        try
        {
            Directory.Delete(stagedPath, recursive: true);
            return new StagedFolderDeletion(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StagedFolderDeletion(false, stagedPath);
        }
    });

    private sealed record StagedFolderDeletion(bool Success, string? StagedPath);

    private static string PathForDisplay(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private async void EditFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProtectedFolder folder
            || !await EnsureFolderManagementAvailableAsync($"Autorizar cambios en {folder.Name}")) return;
        var name = new TextBox { Header = "Nombre", Text = folder.Name };
        var unlockMinutes = CreateFolderMinutesBox(folder.UnlockMinutes);
        var passwordMode = new ComboBox { Header = "Contraseña", HorizontalAlignment = HorizontalAlignment.Stretch };
        passwordMode.Items.Add(new ComboBoxItem { Content = "Mantener configuración actual", Tag = "keep" });
        passwordMode.Items.Add(new ComboBoxItem { Content = "Usar contraseña maestra", Tag = "master" });
        passwordMode.Items.Add(new ComboBoxItem { Content = "Definir contraseña propia", Tag = "own" });
        passwordMode.SelectedIndex = 0;
        var password = new PasswordBox { Header = "Nueva contraseña propia", PasswordRevealMode = PasswordRevealMode.Peek, Visibility = Visibility.Collapsed };
        var confirmation = new PasswordBox { Header = "Confirmar contraseña", PasswordRevealMode = PasswordRevealMode.Peek, Visibility = Visibility.Collapsed };
        passwordMode.SelectionChanged += (_, _) =>
        {
            var visibility = (passwordMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "own" ? Visibility.Visible : Visibility.Collapsed;
            password.Visibility = visibility;
            confirmation.Visibility = visibility;
        };
        var error = CreateDialogErrorText();
        var panel = new StackPanel { Width = 420, Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = folder.Path, Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 118, 118, 118)), FontSize = 10, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(name); panel.Children.Add(unlockMinutes); panel.Children.Add(passwordMode); panel.Children.Add(password); panel.Children.Add(confirmation); panel.Children.Add(error);
        var dialog = CreateDialog("Editar carpeta protegida", panel, "Guardar", "Cancelar");
        var valid = false;
        dialog.PrimaryButtonClick += (dialogSender, args) =>
        {
            var mode = (passwordMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = LocalizationService.T("Indica un nombre."); args.Cancel = true; return; }
            if (mode == "own" && (password.Password.Length < PasswordService.MinimumPasswordLength || password.Password != confirmation.Password)) { error.Text = LocalizationService.T("La nueva contraseña debe coincidir y tener al menos 12 caracteres."); args.Cancel = true; return; }
            if (!TryReadFolderMinutes(unlockMinutes, out _, out var minutesError)) { error.Text = LocalizationService.T(minutesError); args.Cancel = true; return; }
            valid = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !valid) return;

        var snapshot = (folder.Name, folder.UnlockMinutes, folder.PasswordHash, folder.PasswordSalt);
        folder.Name = name.Text.Trim();
        TryReadFolderMinutes(unlockMinutes, out var minutes, out _);
        folder.UnlockMinutes = minutes;
        var selectedMode = (passwordMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (selectedMode == "master") { folder.PasswordHash = null; folder.PasswordSalt = null; }
        if (selectedMode == "own")
        {
            var hashed = PasswordService.Hash(password.Password);
            folder.PasswordHash = hashed.Hash;
            folder.PasswordSalt = hashed.Salt;
        }
        var response = await PersistFolderPolicyAsync();
        if (!response.Success)
        {
            (folder.Name, folder.UnlockMinutes, folder.PasswordHash, folder.PasswordSalt) = snapshot;
            await ShowMessageAsync("No se pudieron guardar los cambios", LocalizationService.UserFacingMessage(response.Error, "Guardian rechazó la configuración."));
            return;
        }
        AddActivity(folder.Name, "Protección de carpeta actualizada");
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Autorizar conversión de carpeta")) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFolder? selected;
        try { selected = await picker.PickSingleFolderAsync(); }
        finally { RestoreMainWindowFocus(); }
        if (selected is null) return;
        await ConvertFolderToVaultAsync(selected.Path, selected.Name);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProtectedFolder folder || !folder.IsEnabled) return;
        await _folderAccessGate.WaitAsync();
        try { await UnlockAndOpenFolderAsync(folder); }
        finally { _folderAccessGate.Release(); }
    }

    private async Task ProcessFolderContextActionAsync(string requestedPath, ProtectedFolder? folder)
    {
        if (!Directory.Exists(requestedPath))
        {
            await ShowNoticeWindowAsync("Carpeta no disponible",
                "La carpeta seleccionada ya no existe o no se puede localizar.", true);
            return;
        }
        if (!await VerifyMasterAsync("Autorizar conversión de carpeta")) return;
        ShowMainWindow();
        SelectNavigation("vaults");
        await ConvertFolderToVaultAsync(requestedPath, Path.GetFileName(requestedPath));
    }

    private async Task UnlockAndOpenFolderAsync(ProtectedFolder folder)
    {
        if (folder.UnlockedUntilUtc is not { } until || until <= DateTimeOffset.UtcNow)
        {
            GuardianResponse? response = null;
            var unlock = new UnlockWindow(folder.Name, "Introduce la contraseña para desbloquear temporalmente la carpeta", async entered =>
            {
                response = await _guardianClient.UnlockFolderAsync(folder.Id, entered);
                RecordGuardianAuthenticationActivity(folder.Name, response, "Contraseña incorrecta al intentar desbloquear la carpeta");
                return UnlockAttemptResult.FromGuardian(response);
            }, _state.UseWindowsHello && !string.IsNullOrWhiteSpace(_guardianToken), async () =>
            {
                response = await _guardianClient.UnlockFolderAsync(folder.Id, token: _guardianToken);
                return UnlockAttemptResult.FromGuardian(response);
            });
            if (!await ShowUnlockWindowAsync(unlock)) return;
            folder.UnlockedUntilUtc = response?.UnlockedUntilUtc;
            if (FolderIconService.TryApply(folder)) await SaveAsync();
            AddActivity(folder.Name, $"Carpeta desbloqueada durante {folder.UnlockMinutes} min");
        }
        try { Process.Start(new ProcessStartInfo(folder.Path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            if (_appWindow.IsVisible)
                await ShowMessageAsync("No se pudo abrir la carpeta", LocalizationService.UserFacingError(ex));
            else
                await ShowNoticeWindowAsync(folder.Name, LocalizationService.UserFacingError(ex), true);
        }
    }

    private async void LockFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProtectedFolder folder
            || !await EnsureFolderManagementAvailableAsync($"Autorizar bloqueo de {folder.Name}")) return;
        var response = await LockFolderWithGuardianAsync(folder);
        if (!response.Success)
        {
            await ShowMessageAsync("No se pudo bloquear la carpeta", LocalizationService.UserFacingMessage(response.Error, "Guardian no confirmó el bloqueo."));
            return;
        }
        folder.UnlockedUntilUtc = null;
        FolderIconService.TryApply(folder);
        AddActivity(folder.Name, "Carpeta bloqueada manualmente");
    }

    private async Task<GuardianResponse> LockFolderWithGuardianAsync(ProtectedFolder folder)
    {
        var response = await _guardianClient.LockFolderAsync(folder.Id, _guardianToken ?? string.Empty);
        if (!response.Success
            && response.Error?.Contains("Autorización de administración no válida", StringComparison.OrdinalIgnoreCase) == true)
        {
            _guardianToken = null;
            if (!await VerifyMasterAsync($"Autorizar bloqueo de {folder.Name}")) return response;
            response = await _guardianClient.LockFolderAsync(folder.Id, _guardianToken ?? string.Empty);
        }
        if (response.Success)
        {
            folder.UnlockedUntilUtc = null;
            FolderIconService.TryApply(folder);
            FolderIconService.NotifyIconChanged(folder.Path);
        }
        return response;
    }

    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProtectedFolder folder) return;
        var dialog = CreateDialog("Eliminar protección de carpeta", new TextBlock
        {
            Text = $"Se restaurarán los permisos originales de {folder.Name}. Sus archivos no se eliminarán.",
            TextWrapping = TextWrapping.Wrap
        }, "Restaurar y eliminar", "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !await EnsureFolderManagementAvailableAsync($"Autorizar eliminación de {folder.Name}")) return;
        var index = Folders.IndexOf(folder);
        folder.PropertyChanged -= Folder_PropertyChanged;
        Folders.Remove(folder);
        var response = await PersistFolderPolicyAsync();
        if (!response.Success)
        {
            Folders.Insert(Math.Max(0, index), folder);
            folder.PropertyChanged += Folder_PropertyChanged;
            await ShowMessageAsync("No se pudo restaurar la carpeta", LocalizationService.UserFacingMessage(response.Error, "Guardian no pudo restaurar sus permisos originales."));
            RefreshFolders();
            return;
        }
        FolderIconService.TryRestore(folder);
        AddActivity(folder.Name, "Protección eliminada y permisos originales restaurados");
        RefreshFolders();
    }

    private async void FolderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingControls || sender is not ToggleSwitch toggle
            || toggle.DataContext is not ProtectedFolder folder) return;

        // Toggled can run before a TwoWay x:Bind has propagated IsOn to the model.
        // Treat the control as authoritative so Guardian always receives the state
        // the user can actually see.
        var desired = toggle.IsOn;
        folder.IsEnabled = desired;
        var folderIconApplied = desired && FolderIconService.TryApply(folder);
        if (!await EnsureFolderManagementAvailableAsync(desired ? $"Autorizar protección de {folder.Name}" : $"Autorizar restauración de {folder.Name}"))
        {
            if (folderIconApplied) FolderIconService.TryRestore(folder);
            SetFolderProtectionToggle(toggle, folder, !desired);
            return;
        }
        var response = await PersistFolderPolicyAsync();
        if (!response.Success)
        {
            if (folderIconApplied) FolderIconService.TryRestore(folder);
            SetFolderProtectionToggle(toggle, folder, !desired);
            await ShowMessageAsync("No se pudo cambiar la protección", LocalizationService.UserFacingMessage(response.Error, "Guardian rechazó el cambio."));
            return;
        }
        if (!desired) FolderIconService.TryRestore(folder);
        folder.UnlockedUntilUtc = null;
        AddActivity(folder.Name, desired ? "Protección de carpeta activada" : "Protección desactivada y permisos restaurados");
    }

    private void SetFolderProtectionToggle(ToggleSwitch toggle, ProtectedFolder folder, bool enabled)
    {
        _updatingControls = true;
        try
        {
            folder.IsEnabled = enabled;
            toggle.IsOn = enabled;
        }
        finally { _updatingControls = false; }
    }

    private void Folder_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshFolders();

    private void RefreshFolders()
    {
        // Kept as a no-op during the compatibility transition. Folder state is
        // no longer published to Explorer because folders are converted to
        // encrypted vaults instead of receiving an NTFS access rule.
    }

    private async Task ProcessPendingFolderRequestsAsync()
    {
        if (!_initialized || !await _folderAccessGate.WaitAsync(0)) return;
        try
        {
            while (_pendingFolderRequests.TryDequeue(out var request))
            {
                if (request.UseContextAction)
                {
                    await ProcessFolderContextActionAsync(request.Path, null);
                    continue;
                }
                await ShowNoticeWindowAsync("Acción no disponible",
                    "Las carpetas ya no se desbloquean mediante permisos NTFS. Usa «Cifrar carpeta como bóveda» para convertirla en una bóveda cifrada.", true);
            }
        }
        finally
        {
            _folderAccessGate.Release();
            if (_pendingFolderRequests.Count > 0)
                DispatcherQueue.TryEnqueue(() => _ = ProcessPendingFolderRequestsAsync());
        }
    }
}
