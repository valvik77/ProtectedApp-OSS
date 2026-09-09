using ProtectedApp.Models;
using ProtectedApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private void VaultActionsFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        LocalizationService.TranslateFlyout(menu);
    }

    private async void AddVaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Autorizar nueva bóveda")) return;
        var name = new TextBox { Header = "Nombre", PlaceholderText = "Documentos privados", MaxLength = 120 };
        var description = new TextBox { Header = "Descripción", PlaceholderText = "Opcional", MaxLength = 300 };
        var password = new PasswordBox { Header = "Contraseña", PlaceholderText = "Mínimo 12 caracteres", PasswordRevealMode = PasswordRevealMode.Peek };
        var confirmation = new PasswordBox { Header = "Confirmar contraseña", PasswordRevealMode = PasswordRevealMode.Peek };
        var minutes = new NumberBox
        {
            Header = "Bloquear automáticamente después de (minutos)",
            Minimum = 1,
            Maximum = 10_080,
            Value = 30,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var inactivityMinutes = new NumberBox
        {
            Header = "Desmontar tras inactividad (0 para desactivar)",
            Minimum = 0,
            Maximum = 10_080,
            Value = 0,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var error = new TextBlock
        {
            Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 85, 85), Windows.UI.Color.FromArgb(255, 248, 81, 73)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 9, Width = 440 };
        panel.Children.Add(name);
        panel.Children.Add(description);
        panel.Children.Add(password);
        panel.Children.Add(confirmation);
        panel.Children.Add(minutes);
        panel.Children.Add(inactivityMinutes);
        panel.Children.Add(error);
        var dialog = CreateDialog("Nueva bóveda cifrada", panel, "Continuar", "Cancelar");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = LocalizationService.T("Introduce un nombre."); args.Cancel = true; return; }
            if (password.Password.Length < PasswordService.MinimumPasswordLength) { error.Text = LocalizationService.T("La contraseña debe tener al menos 12 caracteres."); args.Cancel = true; return; }
            if (password.Password != confirmation.Password) { error.Text = LocalizationService.T("Las contraseñas no coinciden."); args.Cancel = true; return; }
            if (double.IsNaN(minutes.Value) || minutes.Value < 1 || minutes.Value > 10_080)
            { error.Text = LocalizationService.T("El tiempo debe estar entre 1 y 10.080 minutos."); args.Cancel = true; }
            if (double.IsNaN(inactivityMinutes.Value) || inactivityMinutes.Value < 0 || inactivityMinutes.Value > 10_080)
            { error.Text = LocalizationService.T("La inactividad debe estar entre 0 y 10.080 minutos."); args.Cancel = true; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SanitizeFileName(name.Text.Trim())
        };
        picker.FileTypeChoices.Add("Bóveda cifrada de ProtectedApp", new List<string> { ".pavault" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file;
        try { file = await picker.PickSaveFileAsync(); }
        finally { RestoreMainWindowFocus(); }
        if (file is null) return;
        if (Vaults.Any(candidate => PathsEqual(candidate.VaultFilePath ?? string.Empty, file.Path)))
        {
            await ShowMessageAsync("Bóveda duplicada", "Ese contenedor ya aparece en la lista.");
            return;
        }

        var vault = new VaultContainer
        {
            Name = name.Text.Trim(),
            Description = description.Text.Trim(),
            AutoLockMinutes = (int)minutes.Value,
            InactivityAutoLockMinutes = (int)inactivityMinutes.Value,
            VaultFilePath = file.Path
        };
        if (!await _vaultService.SaveVaultAsync(vault, file.Path, password.Password))
        {
            var detail = string.IsNullOrWhiteSpace(_vaultService.LastError)
                ? "No se pudo escribir y verificar el contenedor cifrado."
                : $"No se pudo escribir el contenedor cifrado:{Environment.NewLine}{_vaultService.LastError}";
            await ShowMessageAsync("No se pudo crear", detail);
            return;
        }
        vault.PropertyChanged += Vault_PropertyChanged;
        Vaults.Add(vault);
        RefreshVaults();
        AddActivity(vault.Name, "Bóveda cifrada creada");
        await SaveAsync();
    }

    private async void ImportVaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Autorizar importación de bóveda")) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pavault");
        picker.FileTypeFilter.Add(".bak");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file;
        try { file = await picker.PickSingleFileAsync(); }
        finally { RestoreMainWindowFocus(); }
        if (file is null) return;
        var selectedBackup = file.Path.EndsWith(".pavault.bak", StringComparison.OrdinalIgnoreCase);
        if (Path.GetExtension(file.Path).Equals(".bak", StringComparison.OrdinalIgnoreCase) && !selectedBackup)
        {
            await ShowMessageAsync("Copia no compatible", "Selecciona un archivo cuyo nombre termine en .pavault.bak.");
            return;
        }
        var primaryPath = selectedBackup ? file.Path[..^4] : file.Path;
        var backupPath = primaryPath + ".bak";
        if (Vaults.Any(candidate => PathsEqual(candidate.VaultFilePath ?? string.Empty, primaryPath)))
        {
            await ShowMessageAsync("Bóveda duplicada", "Ese contenedor ya aparece en la lista.");
            return;
        }

        VaultContainer? loaded = null;
        VaultContainer? recoverableBackup = null;
        string? recoverableBackupPassword = null;
        var unlock = new UnlockWindow("Importar bóveda", "Introduce la contraseña del contenedor", async candidatePassword =>
        {
            loaded = null;
            recoverableBackup = null;
            recoverableBackupPassword = null;
            if (!selectedBackup)
                loaded = await _vaultService.LoadVaultAsync(primaryPath, candidatePassword);
            if (loaded is null && File.Exists(backupPath))
            {
                recoverableBackup = await _vaultService.LoadVaultAsync(backupPath, candidatePassword);
                if (recoverableBackup is not null)
                {
                    recoverableBackup.VaultFilePath = primaryPath;
                    recoverableBackupPassword = candidatePassword;
                }
            }
            var throttle = _localAuthenticationThrottle.Verify($"vault-import:{primaryPath}",
                loaded is not null || recoverableBackup is not null,
                "Contraseña incorrecta o contenedor no válido.");
            RecordLocalAuthenticationActivity(Path.GetFileName(primaryPath), throttle,
                "Contraseña incorrecta al importar la bóveda");
            return throttle.Attempt;
        });
        if (!await ShowUnlockWindowAsync(unlock)) return;
        if (loaded is null && recoverableBackup is not null && recoverableBackupPassword is not null)
        {
            var confirmation = CreateDialog("Recuperar antes de importar",
                new TextBlock
                {
                    Text = "El contenedor principal no es válido, pero su copia cifrada anterior sí. ProtectedApp puede restaurarla y conservar el archivo dañado antes de importar la bóveda.",
                    TextWrapping = TextWrapping.Wrap
                },
                "Restaurar e importar", "Cancelar");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await _vaultService.RestoreVaultBackupAsync(recoverableBackup, recoverableBackupPassword);
            if (!result.Success)
            {
                await ShowMessageAsync("No se pudo recuperar", result.Error ?? "La copia anterior se ha conservado.");
                return;
            }
            loaded = recoverableBackup;
            AddActivity(loaded.Name, result.PreservedPrimaryPath is null
                ? "Bóveda restaurada desde la copia anterior durante la importación"
                : $"Bóveda restaurada e importada; el contenedor dañado se conservó en {result.PreservedPrimaryPath}");
        }
        if (loaded is null) return;
        loaded.VaultFilePath = primaryPath;
        loaded.PropertyChanged += Vault_PropertyChanged;
        Vaults.Add(loaded);
        RefreshVaults();
        await RefreshVaultRecoveryItemsAsync();
        AddActivity(loaded.Name, "Bóveda cifrada importada");
        await SaveAsync();
    }

    private async Task ProcessPendingVaultRequestsAsync()
    {
        if (!_initialized || !await _vaultOperationGate.WaitAsync(0)) return;
        try
        {
            while (_pendingVaultRequests.TryDequeue(out var request))
            {
                var requestedPath = request.Path;
                if (!File.Exists(requestedPath))
                {
                    await ShowNoticeWindowAsync("Bóveda no encontrada", "El archivo .pavault fue movido o eliminado.", true);
                    continue;
                }

                var vault = Vaults.FirstOrDefault(candidate => candidate.VaultFilePath is not null
                    && PathsEqual(candidate.VaultFilePath, requestedPath));
                var importIdentityFromContainer = vault is null;
                if (vault is null)
                {
                    vault = new VaultContainer
                    {
                        Name = Path.GetFileNameWithoutExtension(requestedPath),
                        VaultFilePath = requestedPath
                    };
                }

                if (request.UseContextAction && vault.IsMounted)
                {
                    if (!await LockVaultAsync(vault, automatic: false))
                        await ShowNoticeWindowAsync(vault.Name,
                            _vaultService.LastError ?? "No se pudo guardar y desmontar la bóveda.", true);
                    continue;
                }

                await OpenVaultAsync(vault, gateHeld: true, directActivation: true,
                    importIdentityFromContainer: importIdentityFromContainer);
                if (importIdentityFromContainer && vault.IsMounted)
                {
                    vault.PropertyChanged += Vault_PropertyChanged;
                    Vaults.Add(vault);
                    RefreshVaults();
                    AddActivity(vault.Name, "Bóveda añadida mediante doble clic");
                    await SaveAsync();
                }
            }
        }
        finally
        {
            _vaultOperationGate.Release();
            if (_pendingVaultRequests.Count > 0)
                DispatcherQueue.TryEnqueue(() => _ = ProcessPendingVaultRequestsAsync());
        }
    }

    private async void OpenVault_Click(object sender, RoutedEventArgs e)
    {
        if (GetVaultFromSender(sender) is not { } vault) return;
        await OpenVaultAsync(vault);
    }

    private static VaultContainer? GetVaultFromSender(object sender) =>
        (sender as FrameworkElement)?.DataContext as VaultContainer
        ?? (sender as MenuFlyoutItem)?.CommandParameter as VaultContainer;

    private async Task RefreshVaultRecoveryItemsAsync()
    {
        try
        {
            var vaults = Vaults.ToArray();
            var items = await Task.Run(() => _vaultService.FindPendingRecoveryWork(vaults));
            VaultRecoveryItems.Clear();
            foreach (var item in items) VaultRecoveryItems.Add(item);
            VaultRecoveryPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            VaultRecoveryCountText.Text = items.Count == 1
                ? "1 trabajo conservado"
                : $"{items.Count} trabajos conservados";
            if (items.Count > 0 && !_state.VaultRecoveryWarningPending)
            {
                var listedPaths = string.Join(Environment.NewLine, items.Take(5)
                    .Select(item => $"• {item.DisplayName}: {item.WorkingDirectory}"));
                var remainder = items.Count > 5
                    ? $"{Environment.NewLine}• y {items.Count - 5} trabajo(s) más"
                    : string.Empty;
                _state.VaultRecoveryWarningPending = true;
                _state.VaultRecoveryWarningMessage =
                    $"ProtectedApp ha detectado {items.Count} carpeta(s) de trabajo conservadas. " +
                    $"Revísalas para guardar sus cambios o descartarlas de forma segura.{Environment.NewLine}{Environment.NewLine}{listedPaths}{remainder}";
                AddActivity("ProtectedApp", $"Detectados {items.Count} trabajo(s) de bóveda pendientes de recuperación");
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception)
        {
            VaultRecoveryItems.Clear();
            VaultRecoveryPanel.Visibility = Visibility.Collapsed;
            AddActivity("ProtectedApp", "No se pudo revisar la recuperación de bóvedas.");
        }
    }

    private async void RefreshVaultRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshVaultRecoveryItemsAsync();
    }

    private async void RecoverVaultWork_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultRecoveryItem item || item.Vault is null) return;
        await _vaultOperationGate.WaitAsync();
        try
        {
            var check = await Task.Run(() => _vaultService.ValidateRecovery(item));
            if (!check.Success)
            {
                AddActivity(item.DisplayName, $"Recuperación detenida por la comprobación previa: {check.Message}");
                await ShowMessageAsync("No se puede recuperar todavía", check.Message);
                await RefreshVaultRecoveryItemsAsync();
                return;
            }

            string? verifiedPassword = null;
            var unlock = new UnlockWindow(item.Vault.Name,
                $"Introduce la contraseña para recuperar el trabajo pendiente.{Environment.NewLine}{check.Message}",
                async candidatePassword =>
                {
                    var valid = await _vaultService.VerifyVaultPasswordAsync(item.Vault.VaultFilePath!, candidatePassword);
                    var throttle = _localAuthenticationThrottle.Verify($"vault-recovery:{item.VaultId:N}", valid,
                        "Contraseña incorrecta.");
                    RecordLocalAuthenticationActivity(item.Vault.Name, throttle,
                        "Contraseña incorrecta al recuperar la bóveda");
                    if (throttle.Attempt.Success) verifiedPassword = candidatePassword;
                    return throttle.Attempt;
                });
            if (!await ShowUnlockWindowAsync(unlock) || verifiedPassword is null) return;

            var recovered = await _vaultService.RecoverWorkingDirectoryAsync(item, verifiedPassword);
            if (recovered)
            {
                AddActivity(item.Vault.Name, "Trabajo pendiente recuperado, cifrado y eliminado de la carpeta temporal");
                await RefreshVaultRecoveryItemsAsync();
                RefreshVaults();
                ClearVaultRecoveryWarningIfResolved();
                await SaveAsync();
                await ShowMessageAsync("Recuperación completada",
                    $"Los archivos se han guardado en:{Environment.NewLine}{item.Vault.VaultFilePath}{Environment.NewLine}{Environment.NewLine}La carpeta de trabajo se eliminó después de verificar el contenedor cifrado.");
                return;
            }

            var error = string.IsNullOrWhiteSpace(_vaultService.LastError)
                ? "No se pudo completar el cifrado. Los archivos se han conservado."
                : _vaultService.LastError;
            _state.VaultRecoveryWarningPending = true;
            _state.VaultRecoveryWarningMessage =
                $"No se pudo recuperar {item.Vault.Name}. La carpeta de trabajo continúa en:{Environment.NewLine}{item.WorkingDirectory}{Environment.NewLine}{Environment.NewLine}{error}";
            AddActivity(item.Vault.Name, $"No se pudo recuperar el trabajo pendiente: {error}");
            await SaveAsync();
            await RefreshVaultRecoveryItemsAsync();
            await ShowMessageAsync("La recuperación no se completó",
                $"{error}{Environment.NewLine}{Environment.NewLine}Los archivos permanecen en:{Environment.NewLine}{item.WorkingDirectory}");
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async void OpenVaultRecoveryFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultRecoveryItem item) return;
        if (!Directory.Exists(item.WorkingDirectory) || !TryOpenDirectory(item.WorkingDirectory))
        {
            await RefreshVaultRecoveryItemsAsync();
            await ShowMessageAsync("No se pudo abrir", "La carpeta de trabajo ya no existe o Windows no permitió abrirla.");
            return;
        }
        AddActivity(item.DisplayName, "Carpeta de recuperación abierta para revisión manual");
    }

    private async void DiscardVaultRecovery_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultRecoveryItem item) return;
        var confirmation = new TextBox
        {
            Header = LocalizationService.T("Escribe DESCARTAR para confirmar"),
            PlaceholderText = LocalizationService.T("DESCARTAR"),
            MaxLength = 9
        };
        var warning = new TextBlock
        {
            Text = "Esta acción elimina permanentemente la carpeta de trabajo sin incorporar sus cambios al contenedor cifrado.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 184, 108), Windows.UI.Color.FromArgb(255, 137, 85, 3))
        };
        var error = new TextBlock
        {
            Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 85, 85), Windows.UI.Color.FromArgb(255, 248, 81, 73)),
            FontSize = 11
        };
        var panel = new StackPanel { Spacing = 9, Width = 430 };
        panel.Children.Add(warning);
        panel.Children.Add(new TextBlock
        {
            Text = item.WorkingDirectory,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            FontSize = 10
        });
        panel.Children.Add(confirmation);
        panel.Children.Add(error);
        var dialog = CreateDialog("Descartar trabajo recuperable", panel, "Continuar", "Cancelar");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var confirmationWord = LocalizationService.IsEnglish ? "DISCARD" : "DESCARTAR";
            if (confirmation.Text.Trim().Equals(confirmationWord, StringComparison.OrdinalIgnoreCase)) return;
            error.Text = LocalizationService.T("Escribe DESCARTAR para habilitar la eliminación.");
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !await VerifyMasterAsync("Autorizar descarte de recuperación")) return;

        await _vaultOperationGate.WaitAsync();
        try
        {
            if (!_vaultService.DiscardRecoveryWork(item))
            {
                var detail = _vaultService.LastError ?? "Windows no permitió eliminar la carpeta.";
                AddActivity(item.DisplayName, $"No se pudo descartar el trabajo recuperable: {detail}");
                await ShowMessageAsync("No se pudo descartar", detail);
                return;
            }
            AddActivity(item.DisplayName, "Trabajo recuperable descartado con autenticación maestra y confirmación explícita");
            await RefreshVaultRecoveryItemsAsync();
            RefreshVaults();
            ClearVaultRecoveryWarningIfResolved();
            await SaveAsync();
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async void VaultTimer_Tick(object? sender, object e)
    {
        if (!_initialized || !await _vaultOperationGate.WaitAsync(0)) return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var vault in Vaults.Where(candidate => candidate.IsMounted).ToArray())
            {
                var expiredByDuration = vault.SessionExpiresUtc is { } expires && expires <= now;
                var expiredByInactivity = vault.InactivityAutoLockMinutes > 0
                    && now - vault.LastAccessUtc >= TimeSpan.FromMinutes(vault.InactivityAutoLockMinutes);
                if (!expiredByDuration && !expiredByInactivity) continue;

                var reason = expiredByInactivity ? "por inactividad" : "al finalizar el tiempo configurado";
                if (!await LockVaultAsync(vault, automatic: true, automaticReason: reason))
                {
                    vault.SessionExpiresUtc = null;
                    // Evita reintentos continuos si Windows mantiene la unidad
                    // ocupada. La siguiente actividad o el siguiente periodo
                    // completo volverá a intentarlo.
                    if (expiredByInactivity) vault.LastAccessUtc = now;
                    AddActivity(vault.Name, vault.IsReadOnlyMounted
                        ? $"No se pudo desmontar automáticamente la unidad virtual {reason}"
                        : $"No se pudo aplicar el bloqueo automático {reason}; la carpeta de trabajo se conservó");
                }
            }
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async Task<bool> LockVaultAsync(VaultContainer vault, bool automatic, string? automaticReason = null)
    {
        if (!vault.IsMounted) return true;
        var wasReadOnly = vault.IsReadOnlyMounted;
        vault.IsClosing = true;
        try
        {
            if (!await _vaultService.UnmountVaultAsync(vault)) return false;
            RefreshVaults();
            await RefreshVaultRecoveryItemsAsync();
            ClearVaultRecoveryWarningIfResolved();
            var autoSuffix = string.IsNullOrWhiteSpace(automaticReason) ? string.Empty : $" {automaticReason}";
            AddActivity(vault.Name, wasReadOnly
                ? automatic ? $"Consulta segura cerrada automáticamente{autoSuffix}" : "Consulta segura cerrada y bóveda protegida"
                : automatic ? $"Bóveda bloqueada automáticamente{autoSuffix}" : "Cambios guardados y bóveda protegida");
            await SaveAsync();
            return true;
        }
        finally
        {
            vault.IsClosing = false;
        }
    }

    private async void LockVault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultContainer vault || !vault.IsMounted) return;
        await _vaultOperationGate.WaitAsync();
        try
        {
            if (!await ConfirmPendingVaultChangesAsync(vault)) return;
            if (!await LockVaultAsync(vault, automatic: false))
                await ShowMessageAsync("No se pudo bloquear", vault.IsReadOnlyMounted
                    ? "No se pudo cerrar la sesión de consulta segura."
                    : "La bóveda sigue abierta para editar para evitar perder cambios.");
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async Task<bool> ConfirmPendingVaultChangesAsync(VaultContainer vault)
    {
        if (vault.IsReadOnlyMounted || !_vaultService.HasPendingVirtualChanges(vault)) return true;
        var dialog = CreateDialog("Cambios pendientes",
            new TextBlock
            {
                Text = $"{vault.Name} contiene cambios que todavía no se han consolidado en el archivo cifrado. Al desmontarla se guardarán de forma segura antes de cerrar la unidad.",
                TextWrapping = TextWrapping.Wrap
            },
            "Guardar y desmontar", "Cancelar");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> RestoreDetectedVaultBackupAsync(VaultContainer vault, string password,
        VaultBackupValidation validation)
    {
        var message = validation.PrimaryValid
            ? "La copia anterior es válida, pero también lo es el contenedor actual. ¿Quieres volver deliberadamente a la versión anterior?"
            : "El contenedor principal no se pudo validar con esta contraseña, pero su copia anterior sí. ¿Quieres restaurarla ahora?";
        var confirmation = CreateDialog("Recuperación disponible",
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            "Restaurar y abrir", "Cancelar");
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return false;

        var result = await _vaultService.RestoreVaultBackupAsync(vault, password);
        RefreshVaults();
        if (!result.Success)
        {
            if (vault.BackupNeedsAttention)
            {
                _state.VaultRecoveryWarningPending = true;
                _state.VaultRecoveryWarningMessage =
                    $"No se pudo restaurar la copia anterior de {vault.Name}: {result.Error}";
            }
            AddActivity(vault.Name, $"Falló la recuperación automática de la copia anterior: {result.Error}");
            await SaveAsync();
            await ShowMessageAsync("No se pudo recuperar", result.Error ?? "La copia anterior se ha conservado.");
            return false;
        }

        ClearVaultRecoveryWarningIfResolved();
        AddActivity(vault.Name, result.PreservedPrimaryPath is null
            ? "Copia cifrada anterior restaurada automáticamente al abrir la bóveda"
            : $"Copia anterior restaurada al abrir; el contenedor sustituido se conservó en {result.PreservedPrimaryPath}");
        await SaveAsync();
        return true;
    }

    private async void RemoveVault_Click(object sender, RoutedEventArgs e)
    {
        if (GetVaultFromSender(sender) is not { } vault) return;
        if (vault.IsMounted)
        {
            await ShowMessageAsync("Bóveda abierta", "Guarda y bloquea la bóveda antes de quitarla.");
            return;
        }
        await RefreshVaultRecoveryItemsAsync();
        if (VaultRecoveryItems.Any(item => item.VaultId == vault.Id && !item.IsTemporaryOpening))
        {
            await ShowMessageAsync("Recuperación pendiente",
                "Recupera o descarta primero la carpeta de trabajo conservada. Quitar ahora la bóveda impediría guardarla desde ProtectedApp.");
            return;
        }
        var dialog = CreateDialog("Gestionar bóveda",
            $"Puedes quitar {vault.Name} de la lista sin borrar el archivo cifrado, o eliminar definitivamente el contenedor.",
            "Quitar de la lista", "Cancelar");
        dialog.SecondaryButtonText = "Eliminar permanentemente";
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            await DeleteVaultPermanentlyFromUiAsync(vault);
            return;
        }
        if (choice != ContentDialogResult.Primary || !await VerifyMasterAsync($"Autorizar retirada de {vault.Name}")) return;
        vault.PropertyChanged -= Vault_PropertyChanged;
        Vaults.Remove(vault);
        RefreshVaults();
        AddActivity(vault.Name, "Bóveda retirada de la lista; el contenedor cifrado se conservó");
        await SaveAsync();
    }

    private async Task ProcessPendingVaultUnmountRequestsAsync()
    {
        if (!_initialized || !await _vaultOperationGate.WaitAsync(0)) return;
        try
        {
            while (_pendingVaultUnmountDrives.TryDequeue(out var mountPath))
            {
                var vault = Vaults.FirstOrDefault(candidate => candidate.IsMounted
                    && !string.IsNullOrWhiteSpace(candidate.MountPath)
                    && PathsEqual(candidate.MountPath, mountPath));
                // The registry command is deliberately harmless for another
                // drive if Explorer ever ignores its AppliesTo condition.
                if (vault is null) continue;

                if (!await LockVaultAsync(vault, automatic: false))
                    await ShowNoticeWindowAsync("No se pudo desmontar la bóveda",
                        _vaultService.LastError ?? "La unidad virtual sigue abierta para evitar perder cambios.", true);
            }
        }
        finally
        {
            _vaultOperationGate.Release();
            if (_pendingVaultUnmountDrives.Count > 0)
                DispatcherQueue.TryEnqueue(() => _ = ProcessPendingVaultUnmountRequestsAsync());
        }
    }

    private async void CleanupMissingVaultReferences_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Gestionar referencias de bóvedas")) return;
        await RefreshVaultRecoveryItemsAsync();
        var missing = Vaults.Where(vault => !vault.IsMounted
            && (string.IsNullOrWhiteSpace(vault.VaultFilePath)
                || (!File.Exists(vault.VaultFilePath) && !File.Exists(vault.VaultFilePath + ".bak")))
            && !VaultRecoveryItems.Any(item => item.VaultId == vault.Id && !item.IsTemporaryOpening))
            .ToArray();
        if (missing.Length == 0)
        {
            await ShowMessageAsync("No hay referencias ausentes",
                "Todas las bóvedas de la lista tienen su contenedor principal, una copia recuperable o trabajo pendiente que requiere atención.");
            return;
        }

        var names = string.Join(Environment.NewLine, missing.Take(6)
            .Select(vault => $"• {vault.Name}: {vault.VaultFilePath ?? "sin ubicación"}"));
        var more = missing.Length > 6 ? $"{Environment.NewLine}• y {missing.Length - 6} más" : string.Empty;
        var dialog = CreateDialog("Quitar referencias ausentes",
            $"Se quitarán de la lista {missing.Length} referencia(s) cuyo archivo cifrado y copia de recuperación ya no existen. No se eliminará ningún archivo.{Environment.NewLine}{Environment.NewLine}{names}{more}",
            "Quitar referencias", "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var vault in missing)
        {
            vault.PropertyChanged -= Vault_PropertyChanged;
            Vaults.Remove(vault);
        }
        RefreshVaults();
        AddActivity("ProtectedApp", $"Eliminadas {missing.Length} referencia(s) de bóvedas sin contenedor disponible");
        await SaveAsync();
    }

    private async Task DeleteVaultPermanentlyFromUiAsync(VaultContainer vault)
    {
        var confirmation = new TextBox { Header = "Escribe ELIMINAR para confirmar", MaxLength = 8 };
        var keepCopies = new CheckBox
        {
            Content = "Conservar las copias cifradas existentes (.bak y programadas)",
            IsChecked = true
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"Se eliminará permanentemente el contenedor de {vault.Name}. Esta acción no se puede deshacer.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(keepCopies);
        content.Children.Add(confirmation);
        var dialog = CreateDialog("Eliminar bóveda permanentemente", content, "Eliminar permanentemente", "Cancelar");
        dialog.IsPrimaryButtonEnabled = false;
        confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            confirmation.Text.Equals("ELIMINAR", StringComparison.Ordinal);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            !await VerifyMasterAsync($"Autorizar eliminación permanente de {vault.Name}")) return;

        await _vaultOperationGate.WaitAsync();
        try
        {
            await RefreshVaultRecoveryItemsAsync();
            if (vault.IsMounted || VaultRecoveryItems.Any(item => item.VaultId == vault.Id && !item.IsTemporaryOpening))
            {
                await ShowMessageAsync("No se puede eliminar", "Guarda y bloquea la bóveda y resuelve cualquier recuperación pendiente antes de eliminarla.");
                return;
            }
            var deleteCopies = keepCopies.IsChecked != true;
            var result = _vaultService.DeleteVaultPermanently(vault, deleteCopies: deleteCopies,
                scheduledBackupRoot: _state.VaultBackupDirectory);
            if (!result.PrimaryDeleted)
            {
                await ShowMessageAsync("No se pudo eliminar", result.Error ?? "Windows rechazó la eliminación del contenedor.");
                return;
            }
            vault.PropertyChanged -= Vault_PropertyChanged;
            Vaults.Remove(vault);
            RefreshVaults();
            AddActivity(vault.Name, deleteCopies
                ? $"Bóveda eliminada permanentemente junto con {result.DeletedCopies} copia(s) cifrada(s)"
                : "Bóveda eliminada permanentemente; las copias cifradas se conservaron");
            await SaveAsync();
            if (result.FailedCopies > 0)
                await ShowMessageAsync("Eliminación parcial", result.Error ?? "El contenedor se eliminó, pero alguna copia se conservó.");
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async Task UnmountVaultFromTrayAsync(Guid vaultId)
    {
        var vault = Vaults.FirstOrDefault(candidate => candidate.Id == vaultId && candidate.IsMounted);
        if (vault is null) return;
        await _vaultOperationGate.WaitAsync();
        try
        {
            if (!await LockVaultAsync(vault, automatic: false))
                _tray.ShowBalloon("No se pudo desmontar la bóveda",
                    _vaultService.LastError ?? "La unidad virtual continúa disponible para evitar perder cambios.");
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async Task OpenVaultFromTrayAsync(Guid vaultId)
    {
        var vault = Vaults.FirstOrDefault(candidate => candidate.Id == vaultId);
        if (vault is null) return;
        await OpenVaultAsync(vault);
    }

    private async Task DeleteVaultBackupFromUiAsync(VaultContainer vault, VaultBackupInfo info)
    {
        var dialog = CreateDialog("Eliminar copia anterior",
            new TextBlock
            {
                Text = $"Se eliminará permanentemente esta copia cifrada, sin modificar el contenedor principal:{Environment.NewLine}{info.BackupPath}",
                TextWrapping = TextWrapping.Wrap
            },
            "Eliminar copia", "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !await VerifyMasterAsync("Autorizar eliminación de la copia anterior")) return;

        await _vaultOperationGate.WaitAsync();
        try
        {
            if (!_vaultService.DeleteVaultBackup(vault))
            {
                await ShowMessageAsync("No se pudo eliminar", LocalizationService.UserFacingMessage(_vaultService.LastError, "Windows rechazó la eliminación."));
                return;
            }
            RefreshVaults();
            ClearVaultRecoveryWarningIfResolved();
            AddActivity(vault.Name, "Copia cifrada anterior eliminada con autorización maestra");
            await SaveAsync();
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async void EditVault_Click(object sender, RoutedEventArgs e)
    {
        if (GetVaultFromSender(sender) is not { } vault ||
            string.IsNullOrWhiteSpace(vault.VaultFilePath)) return;
        if (vault.IsMounted)
        {
            await ShowMessageAsync("Bóveda abierta", "Guarda y bloquea la bóveda antes de editarla o cambiar su contraseña.");
            return;
        }

        string? currentPassword = null;
        var unlock = new UnlockWindow(vault.Name, "Introduce la contraseña actual para editar la bóveda", async candidatePassword =>
        {
            var valid = await _vaultService.VerifyVaultPasswordAsync(vault.VaultFilePath, candidatePassword);
            var throttle = _localAuthenticationThrottle.Verify($"vault-edit:{vault.Id:N}", valid,
                "Contraseña incorrecta.");
            RecordLocalAuthenticationActivity(vault.Name, throttle,
                "Contraseña incorrecta al editar la bóveda");
            if (throttle.Attempt.Success) currentPassword = candidatePassword;
            return throttle.Attempt;
        });
        if (!await ShowUnlockWindowAsync(unlock) || currentPassword is null) return;

        var name = new TextBox { Header = "Nombre", Text = vault.Name, MaxLength = 120 };
        var description = new TextBox { Header = "Descripción", Text = vault.Description, MaxLength = 300 };
        var minutes = new NumberBox
        {
            Header = "Bloquear automáticamente después de (minutos)",
            Minimum = 1,
            Maximum = 10_080,
            Value = vault.AutoLockMinutes,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var inactivityMinutes = new NumberBox
        {
            Header = "Desmontar tras inactividad (0 para desactivar)",
            Minimum = 0,
            Maximum = 10_080,
            Value = vault.InactivityAutoLockMinutes,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var newPassword = new PasswordBox { Header = "Nueva contraseña (opcional)", PlaceholderText = "Déjalo vacío para conservarla", PasswordRevealMode = PasswordRevealMode.Peek };
        var confirmation = new PasswordBox { Header = "Confirmar nueva contraseña", PasswordRevealMode = PasswordRevealMode.Peek };
        var error = new TextBlock
        {
            Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 85, 85), Windows.UI.Color.FromArgb(255, 248, 81, 73)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 9, Width = 440 };
        panel.Children.Add(name);
        panel.Children.Add(description);
        panel.Children.Add(minutes);
        panel.Children.Add(inactivityMinutes);
        panel.Children.Add(newPassword);
        panel.Children.Add(confirmation);
        panel.Children.Add(error);
        var dialog = CreateDialog("Editar bóveda", panel, "Guardar", "Cancelar");
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.IsEnglish
                ? "Changing the password re-encrypts all data. Existing backups still use their old passwords; review them if a password was compromised."
                : "Cambiar la contraseña vuelve a cifrar todos los datos. Las copias anteriores conservan sus contraseñas; revísalas si una contraseña se ha filtrado.",
            TextWrapping = TextWrapping.Wrap
        });
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = LocalizationService.T("Introduce un nombre."); args.Cancel = true; return; }
            if (double.IsNaN(minutes.Value) || minutes.Value < 1 || minutes.Value > 10_080)
            { error.Text = LocalizationService.T("El tiempo debe estar entre 1 y 10.080 minutos."); args.Cancel = true; return; }
            if (double.IsNaN(inactivityMinutes.Value) || inactivityMinutes.Value < 0 || inactivityMinutes.Value > 10_080)
            { error.Text = LocalizationService.T("La inactividad debe estar entre 0 y 10.080 minutos."); args.Cancel = true; return; }
            if (newPassword.Password.Length is > 0 and < PasswordService.MinimumPasswordLength)
            { error.Text = LocalizationService.T("La nueva contraseña debe tener al menos 12 caracteres."); args.Cancel = true; return; }
            if (newPassword.Password != confirmation.Password)
            { error.Text = LocalizationService.T("Las nuevas contraseñas no coinciden."); args.Cancel = true; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var originalName = vault.Name;
        var originalDescription = vault.Description;
        var originalMinutes = vault.AutoLockMinutes;
        var originalInactivityMinutes = vault.InactivityAutoLockMinutes;
        vault.Name = name.Text.Trim();
        vault.Description = description.Text.Trim();
        vault.AutoLockMinutes = (int)minutes.Value;
        vault.InactivityAutoLockMinutes = (int)inactivityMinutes.Value;
        if (!await _vaultService.SaveVaultAsync(vault, vault.VaultFilePath, currentPassword))
        {
            vault.Name = originalName;
            vault.Description = originalDescription;
            vault.AutoLockMinutes = originalMinutes;
            vault.InactivityAutoLockMinutes = originalInactivityMinutes;
            await ShowMessageAsync("No se pudo guardar", "El contenedor no se modificó y conserva su contraseña actual.");
            return;
        }
        if (!string.IsNullOrEmpty(newPassword.Password) &&
            !await _vaultService.ChangeVaultPasswordAsync(vault.VaultFilePath, currentPassword, newPassword.Password))
        {
            AddActivity(vault.Name, "Configuración actualizada, pero no se pudo cambiar la contraseña de la bóveda");
            await SaveAsync();
            await ShowMessageAsync("No se pudo cambiar la contraseña", "Los demás cambios se guardaron, pero la contraseña anterior sigue siendo válida.");
            return;
        }
        RefreshVaults();
        AddActivity(vault.Name, string.IsNullOrEmpty(newPassword.Password)
            ? "Configuración de la bóveda actualizada"
            : "Configuración y contraseña de la bóveda actualizadas");
        await SaveAsync();
    }

    private Border CreateVaultBackupDetail(string title, string path, bool exists, bool structurallyValid,
        long sizeBytes, DateTime? modifiedUtc)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
        });
        var state = !exists
            ? "No existe"
            : $"{VaultRecoveryItem.FormatBytes(sizeBytes)} · {(structurallyValid ? "estructura reconocida" : "estructura dañada")} · {(modifiedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "fecha desconocida")}";
        panel.Children.Add(new TextBlock
        {
            Text = state,
            FontSize = 9,
            Foreground = structurallyValid
                ? (Brush)Application.Current.Resources["MutedTextBrush"]
                : ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 184, 108), Windows.UI.Color.FromArgb(255, 137, 85, 3))
        });
        return new Border
        {
            Background = (Brush)Application.Current.Resources["ControlSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = panel
        };
    }

    private async void ManageVaultBackup_Click(object sender, RoutedEventArgs e)
    {
        if (GetVaultFromSender(sender) is not { } vault) return;
        if (vault.IsMounted)
        {
            await ShowMessageAsync("Bóveda abierta", "Guarda y bloquea la bóveda antes de gestionar su copia anterior.");
            return;
        }
        var info = _vaultService.InspectVaultBackup(vault);
        if (!info.BackupExists)
        {
            RefreshVaults();
            await ShowMessageAsync("Sin copia anterior", "No existe una copia cifrada anterior para esta bóveda.");
            return;
        }

        var details = new StackPanel { Spacing = 8, Width = 460 };
        details.Children.Add(new TextBlock { Text = info.StatusLabel, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        details.Children.Add(CreateVaultBackupDetail("Contenedor actual", info.PrimaryPath, info.PrimaryExists, info.PrimaryEnvelopeValid, info.PrimarySizeBytes, info.PrimaryModifiedUtc));
        details.Children.Add(CreateVaultBackupDetail("Copia anterior", info.BackupPath, info.BackupExists, info.BackupEnvelopeValid, info.BackupSizeBytes, info.BackupModifiedUtc));
        details.Children.Add(new TextBlock
        {
            Text = "La comprobación estructural no descifra archivos. La contraseña verificará la autenticidad antes de restaurar.",
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"], FontSize = 10, TextWrapping = TextWrapping.Wrap
        });
        var choice = CreateDialog("Copia cifrada anterior", details, "Validar y restaurar", "Cancelar");
        choice.SecondaryButtonText = "Eliminar copia";
        var selected = await choice.ShowAsync();
        if (selected == ContentDialogResult.Secondary) { await DeleteVaultBackupFromUiAsync(vault, info); return; }
        if (selected != ContentDialogResult.Primary) return;

        await _vaultOperationGate.WaitAsync();
        try
        {
            if (vault.IsMounted) return;
            string? verifiedPassword = null;
            VaultBackupValidation? validation = null;
            var unlock = new UnlockWindow(vault.Name, "Introduce la contraseña de la bóveda para validar la copia anterior", async candidatePassword =>
            {
                validation = await _vaultService.ValidateVaultBackupAsync(vault, candidatePassword);
                var throttle = _localAuthenticationThrottle.Verify($"vault-backup:{vault.Id:N}", validation.BackupValid, "Contraseña incorrecta o copia no válida.");
                RecordLocalAuthenticationActivity(vault.Name, throttle, "Contraseña incorrecta al validar la copia anterior de la bóveda");
                if (throttle.Attempt.Success) verifiedPassword = candidatePassword;
                return throttle.Attempt;
            });
            if (!await ShowUnlockWindowAsync(unlock) || verifiedPassword is null || validation is null) return;

            if (validation.PrimaryValid) vault.BackupNeedsAttention = false;
            else
            {
                MarkVaultBackupAttention(vault, "El contenedor principal no superó la autenticación, pero su copia cifrada anterior sí");
                await SaveAsync();
            }
            var confirmationText = validation.PrimaryValid
                ? "El contenedor actual también es válido. Restaurar volverá a la versión anterior y puede descartar cambios posteriores. El actual se conservará con la marca 'replaced'."
                : "La copia anterior es válida. Sustituirá al contenedor ausente o dañado; el archivo dañado se conservará con la marca 'corrupt'.";
            var confirmation = CreateDialog("Confirmar restauración", new TextBlock { Text = confirmationText, TextWrapping = TextWrapping.Wrap }, "Restaurar copia", "Cancelar");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await _vaultService.RestoreVaultBackupAsync(vault, verifiedPassword);
            RefreshVaults();
            if (!result.Success)
            {
                if (vault.BackupNeedsAttention)
                {
                    _state.VaultRecoveryWarningPending = true;
                    _state.VaultRecoveryWarningMessage = $"No se pudo restaurar la copia anterior de {vault.Name}: {result.Error}";
                }
                AddActivity(vault.Name, $"No se pudo restaurar la copia cifrada anterior: {result.Error}");
                await SaveAsync();
                await ShowMessageAsync("No se pudo restaurar", result.Error ?? "La copia se conservó sin cambios.");
                return;
            }
            ClearVaultRecoveryWarningIfResolved();
            AddActivity(vault.Name, result.PreservedPrimaryPath is null ? "Contenedor principal recuperado desde la copia cifrada anterior" : $"Contenedor recuperado desde la copia anterior; el archivo sustituido se conservó en {result.PreservedPrimaryPath}");
            await SaveAsync();
            await ShowMessageAsync("Bóveda recuperada", result.PreservedPrimaryPath is null ? "La copia se verificó y se restauró como contenedor principal." : $"La copia se verificó y se restauró correctamente. El contenedor sustituido se conserva en:{Environment.NewLine}{result.PreservedPrimaryPath}");
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async void ManageScheduledVaultBackups_Click(object sender, RoutedEventArgs e)
    {
        if (GetVaultFromSender(sender) is not { } vault) return;
        if (vault.IsMounted || string.IsNullOrWhiteSpace(_state.VaultBackupDirectory))
        {
            await ShowMessageAsync("Copias no disponibles", "Guarda y bloquea la bóveda y configura un destino de copias antes de continuar.");
            return;
        }

        var versions = _vaultService.ListScheduledBackups(vault, _state.VaultBackupDirectory);
        if (versions.Count == 0)
        {
            RefreshVaultBackupStatus();
            await ShowMessageAsync("Sin versiones", "No se encontró ninguna copia programada para esta bóveda.");
            return;
        }

        var selector = new ComboBox { Header = "Versiones disponibles", Width = 460 };
        foreach (var version in versions)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Tag = version,
                Content = $"{version.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {VaultRecoveryItem.FormatBytes(version.SizeBytes)} · {(version.EnvelopeValid ? "estructura válida" : "estructura dañada") }"
            });
        }
        selector.SelectedIndex = 0;
        var selectedPath = new TextBlock
        {
            Text = versions[0].Path,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
        };
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is ComboBoxItem { Tag: VaultScheduledBackupVersion version }) selectedPath.Text = version.Path;
        };
        var panel = new StackPanel { Spacing = 8, Width = 480 };
        panel.Children.Add(new TextBlock { Text = "Selecciona una copia completa de esta bóveda.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(selector);
        panel.Children.Add(selectedPath);
        panel.Children.Add(new TextBlock
        {
            Text = "Comprobar valida la contraseña y autenticidad sin modificar la bóveda. Tras una comprobación correcta podrás decidir si restaurarla.",
            FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
        });
        var dialog = CreateDialog("Historial de copias programadas", panel, "Comprobar versión", "Cancelar");
        dialog.SecondaryButtonText = "Eliminar versión";
        var choice = await dialog.ShowAsync();
        if (selector.SelectedItem is not ComboBoxItem { Tag: VaultScheduledBackupVersion selected }) return;

        if (choice == ContentDialogResult.Secondary)
        {
            if (!await VerifyMasterAsync("Eliminar versión de copia")) return;
            var confirmation = CreateDialog("Eliminar versión", $"Se eliminará la copia del {selected.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}. Esta acción no se puede deshacer.", "Eliminar", "Cancelar");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            if (!_vaultService.DeleteScheduledBackup(vault, _state.VaultBackupDirectory, selected.Path))
            {
                await ShowMessageAsync("No se pudo eliminar", "La versión se conservó. Comprueba que el archivo no esté abierto ni protegido.");
                return;
            }
            RefreshVaultBackupStatus();
            AddActivity(vault.Name, "Versión de copia programada eliminada");
            await SaveAsync();
            return;
        }
        if (choice != ContentDialogResult.Primary) return;

        await _vaultOperationGate.WaitAsync();
        try
        {
            if (vault.IsMounted) return;
            string? password = null;
            VaultBackupValidation? validation = null;
            var unlock = new UnlockWindow(vault.Name, "Introduce la contraseña de la bóveda para validar la versión seleccionada", async candidate =>
            {
                validation = await _vaultService.ValidateScheduledBackupAsync(vault, selected.Path, candidate);
                var throttle = _localAuthenticationThrottle.Verify($"vault-scheduled-backup:{vault.Id:N}", validation.BackupValid,
                    "Contraseña incorrecta o versión no válida.");
                RecordLocalAuthenticationActivity(vault.Name, throttle, "Contraseña incorrecta al validar una versión de copia programada");
                if (throttle.Attempt.Success) password = candidate;
                return throttle.Attempt;
            });
            if (!await ShowUnlockWindowAsync(unlock) || password is null || validation is null) return;

            AddActivity(vault.Name, $"Versión programada comprobada correctamente ({selected.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm})");
            await SaveAsync();
            var verificationText = validation.PrimaryValid
                ? "La versión seleccionada y el contenedor actual son válidos. No se ha modificado ningún archivo. Si restauras, volverás a esa fecha y el contenedor actual se conservará con la marca 'replaced'."
                : "La versión seleccionada es válida y el contenedor actual no pudo validarse. No se ha modificado ningún archivo. Si restauras, el archivo actual se conservará con la marca 'corrupt'.";
            var confirmation = CreateDialog("Versión comprobada", verificationText, "Restaurar ahora", "Cerrar");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await _vaultService.RestoreScheduledBackupAsync(vault, selected.Path, password);
            RefreshVaults();
            if (!result.Success)
            {
                AddActivity(vault.Name, $"No se pudo restaurar una versión programada: {result.Error}");
                await SaveAsync();
                await ShowMessageAsync("No se pudo restaurar", result.Error ?? "La versión se conservó sin cambios.");
                return;
            }
            AddActivity(vault.Name, $"Bóveda restaurada desde la copia programada del {selected.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
            await SaveAsync();
            await ShowMessageAsync("Versión restaurada", result.PreservedPrimaryPath is null
                ? "La versión se verificó y se restauró correctamente."
                : $"La versión se restauró correctamente. El contenedor sustituido se conserva en:{Environment.NewLine}{result.PreservedPrimaryPath}");
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async void VerifyVaultIntegrity_Click(object sender, RoutedEventArgs e)
    {
        if (GetVaultFromSender(sender) is not { } vault) return;
        if (vault.IsMounted)
        {
            await ShowMessageAsync("Bóveda abierta", "Guarda y bloquea la bóveda antes de comprobar su integridad.");
            return;
        }

        await _vaultOperationGate.WaitAsync();
        try
        {
            if (vault.IsMounted) return;
            VaultIntegrityValidation? integrity = null;
            string? verifiedPassword = null;
            var unlock = new UnlockWindow(vault.Name,
                "Introduce la contraseña para comprobar todos los bloques cifrados", async candidatePassword =>
                {
                    integrity = await _vaultService.VerifyVaultIntegrityAsync(vault, candidatePassword);
                    var throttle = _localAuthenticationThrottle.Verify($"vault-integrity:{vault.Id:N}",
                        integrity.PasswordVerified, "Contraseña incorrecta o cabecera no válida.");
                    RecordLocalAuthenticationActivity(vault.Name, throttle,
                        "Contraseña incorrecta al comprobar la integridad de la bóveda");
                    if (throttle.Attempt.Success) verifiedPassword = candidatePassword;
                    return throttle.Attempt;
                });
            if (!await ShowUnlockWindowAsync(unlock) || integrity is null || verifiedPassword is null) return;

            if (integrity.IsValid)
            {
                vault.BackupNeedsAttention = false;
                AddActivity(vault.Name, LocalizationService.IsEnglish
                    ? $"Integrity checked: {integrity.FileCount:N0} files and {integrity.ChunkCount:N0} authenticated blocks"
                    : $"Integridad comprobada: {integrity.FileCount:N0} archivos y {integrity.ChunkCount:N0} bloques autenticados");
                await SaveAsync();
                await ShowMessageAsync("Integridad correcta",
                    LocalizationService.IsEnglish
                        ? $"Integrity checked: {integrity.FileCount:N0} files and {integrity.ChunkCount:N0} authenticated blocks.{Environment.NewLine}{Environment.NewLine}Verified data: {VaultRecoveryItem.FormatBytes(integrity.VerifiedBytes)}."
                        : $"{integrity.Message}{Environment.NewLine}{Environment.NewLine}Datos verificados: {VaultRecoveryItem.FormatBytes(integrity.VerifiedBytes)}.");
                return;
            }

            AddActivity(vault.Name, "La comprobación de integridad detectó bloques no válidos");
            var backup = await _vaultService.ValidateVaultBackupAsync(vault, verifiedPassword);
            if (!backup.BackupValid)
            {
                await SaveAsync();
                await ShowMessageAsync("Integridad no confirmada",
                    $"{integrity.Message}{Environment.NewLine}{Environment.NewLine}No hay una copia cifrada anterior válida para recuperarla automáticamente.");
                return;
            }

            MarkVaultBackupAttention(vault,
                "El contenedor principal no superó la comprobación completa de integridad, pero su copia anterior es válida");
            await SaveAsync();
            var confirmation = CreateDialog("Recuperación disponible", new TextBlock
            {
                Text = "La copia cifrada anterior se ha autenticado correctamente. Restaurarla sustituirá el contenedor actual; este se conservará con la marca 'corrupt' para no perder evidencia.",
                TextWrapping = TextWrapping.Wrap
            }, "Restaurar copia", "Cancelar");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            var restored = await _vaultService.RestoreVaultBackupAsync(vault, verifiedPassword);
            RefreshVaults();
            if (!restored.Success)
            {
                _state.VaultRecoveryWarningPending = true;
                _state.VaultRecoveryWarningMessage =
                    $"No se pudo restaurar la copia anterior de {vault.Name}: {restored.Error}";
                AddActivity(vault.Name, $"Falló la recuperación después de comprobar la integridad: {restored.Error}");
                await SaveAsync();
                await ShowMessageAsync("No se pudo recuperar", restored.Error ?? "La copia anterior se ha conservado.");
                return;
            }

            ClearVaultRecoveryWarningIfResolved();
            AddActivity(vault.Name, restored.PreservedPrimaryPath is null
                ? "Bóveda recuperada desde la copia anterior tras comprobar la integridad"
                : $"Bóveda recuperada tras comprobar la integridad; el contenedor sustituido se conserva en {restored.PreservedPrimaryPath}");
            await SaveAsync();
            await ShowMessageAsync("Bóveda recuperada", restored.PreservedPrimaryPath is null
                ? "La copia anterior se restauró correctamente como contenedor principal."
                : $"La copia anterior se restauró correctamente. El contenedor sustituido se conserva en:{Environment.NewLine}{restored.PreservedPrimaryPath}");
        }
        finally { _vaultOperationGate.Release(); }
    }

    private async void ShowVaultActivity_Click(object sender, RoutedEventArgs e)
    {
        if (GetVaultFromSender(sender) is not { } vault) return;
        var entries = _activityHistory
            .Where(entry => string.Equals(entry.AppName, vault.Name, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(entry => entry.Timestamp)
            .Take(100)
            .ToArray();
        var content = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 540,
            Height = 280,
            Text = entries.Length == 0
                ? "Todavía no hay eventos registrados para esta bóveda."
                : string.Join(Environment.NewLine + Environment.NewLine, entries.Select(entry =>
                    $"{entry.Timestamp.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {entry.KindLabel}{Environment.NewLine}{entry.Message}"))
        };
        ScrollViewer.SetVerticalScrollBarVisibility(content, ScrollBarVisibility.Auto);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = entries.Length == 0
                ? vault.Name
                : LocalizationService.IsEnglish
                    ? $"{vault.Name} · {entries.Length} most recent event(s)"
                    : $"{vault.Name} · {entries.Length} evento(s) más recientes",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(content);
        await CreateDialog("Historial de bóveda", panel, "Cerrar", null).ShowAsync();
    }

    private static bool TryOpenDirectory(string path)
    {
        try { return Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) is not null; }
        catch { return false; }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Bóveda" : value;
    }

    private async Task<int> LockAllVaultsAsync(bool showErrors)
    {
        await _vaultOperationGate.WaitAsync();
        try
        {
            var locked = 0;
            foreach (var vault in Vaults.Where(candidate => candidate.IsMounted).ToArray())
            {
                if (!await LockVaultAsync(vault, automatic: false))
                {
                    if (showErrors)
                        await ShowMessageAsync("No se pudieron cerrar todas las bóvedas", vault.IsReadOnlyMounted
                            ? $"No se pudo desmontar la unidad virtual de {vault.Name}: {_vaultService.LastError}"
                            : $"Los cambios de {vault.Name} no se han eliminado. La carpeta de trabajo permanece en:{Environment.NewLine}{vault.MountPath}");
                    return -1;
                }
                locked++;
            }
            return locked;
        }
        finally { _vaultOperationGate.Release(); }
    }

    private void Vault_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshVaults();

    private void RefreshVaults()
    {
        NewVaultLabel.Text = LocalizationService.T("Nueva bóveda");
        EmptyVaultsState.Visibility = Vaults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VaultShellStateStore.Save(Vaults);
        _tray.SetMountedVaults(Vaults.Where(vault => vault.IsMounted)
            .Select(vault => (vault.Id, vault.Name)));
        var recentIds = _state.RecentVaultIds ??= [];
        recentIds.RemoveAll(id => !Vaults.Any(vault => vault.Id == id));
        _tray.SetRecentVaults(recentIds
            .Select(id => Vaults.FirstOrDefault(vault => vault.Id == id))
            .Where(vault => vault is not null)
            .Select(vault => (vault!.Id, vault.Name)));
        if (_refreshingVaultBackupState) return;
        _refreshingVaultBackupState = true;
        try
        {
            foreach (var vault in Vaults)
            {
                var info = _vaultService.InspectVaultBackup(vault);
                vault.HasRecoveryBackup = info.BackupExists;
                vault.BackupCanRestore = info.CanAttemptRestore;
                vault.HasPendingJournal = !vault.IsMounted && _vaultService.HasPendingJournal(vault);
                vault.BackupNeedsAttention = info.BackupExists &&
                    (vault.BackupNeedsAttention || !info.PrimaryExists || !info.PrimaryEnvelopeValid);
            }
            var urgent = Vaults.Where(vault => vault.BackupNeedsAttention).ToArray();
            if (urgent.Length > 0 && !_state.VaultRecoveryWarningPending)
            {
                var recoverable = urgent.Count(vault => vault.BackupCanRestore);
                _state.VaultRecoveryWarningPending = true;
                _state.VaultRecoveryWarningMessage = recoverable == urgent.Length
                    ? urgent.Length == 1
                        ? $"El contenedor principal de {urgent[0].Name} está ausente o dañado, pero existe una copia cifrada anterior que puede recuperarse."
                        : $"Hay {urgent.Length} bóvedas cuyo contenedor principal está ausente o dañado. ProtectedApp ha encontrado copias cifradas anteriores para recuperarlas."
                    : $"Hay {urgent.Length} bóveda(s) con el contenedor principal ausente o dañado; {recoverable} tienen una copia con estructura recuperable y las demás requieren revisión manual.";
                AddActivity("ProtectedApp", $"Detectadas {urgent.Length} copia(s) de bóveda que requieren recuperación");
            }
        }
        finally { _refreshingVaultBackupState = false; }
    }

    private void MarkVaultAsRecent(VaultContainer vault)
    {
        var recentIds = _state.RecentVaultIds ??= [];
        recentIds.RemoveAll(id => id == vault.Id);
        recentIds.Insert(0, vault.Id);
        if (recentIds.Count > 8) recentIds.RemoveRange(8, recentIds.Count - 8);
    }

    private void ClearVaultRecoveryWarningIfResolved()
    {
        if (VaultRecoveryItems.Count != 0 || Vaults.Any(vault => vault.BackupNeedsAttention)) return;
        _state.VaultRecoveryWarningPending = false;
        _state.VaultRecoveryWarningMessage = null;
    }

    private void MarkVaultBackupAttention(VaultContainer vault, string reason)
    {
        var newlyDetected = !vault.BackupNeedsAttention;
        vault.BackupNeedsAttention = true;
        _state.VaultRecoveryWarningPending = true;
        _state.VaultRecoveryWarningMessage =
            $"{reason} para {vault.Name}. La copia puede restaurarse desde la sección de bóvedas.";
        if (newlyDetected) AddActivity(vault.Name, reason);
    }

    private async Task OpenVaultAsync(VaultContainer vault, bool gateHeld = false, bool directActivation = false,
        bool importIdentityFromContainer = false)
    {
        if (!gateHeld) await _vaultOperationGate.WaitAsync();
        try
        {
            if (vault.IsMounted && !string.IsNullOrWhiteSpace(vault.MountPath) && Directory.Exists(vault.MountPath))
            {
                vault.LastAccessUtc = DateTime.UtcNow;
                vault.SessionExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, vault.AutoLockMinutes));
                MarkVaultAsRecent(vault);
                RefreshVaults();
                await SaveAsync();
                if (!TryOpenDirectory(vault.MountPath))
                    await ShowMessageAsync("No se pudo abrir el Explorador", vault.IsReadOnlyMounted
                        ? "La bóveda continúa disponible en su unidad de consulta."
                        : "La bóveda continúa abierta para editar.");
                return;
            }
            if (string.IsNullOrWhiteSpace(vault.VaultFilePath))
            {
                await ShowMessageAsync("No se encuentra la bóveda", "El archivo cifrado fue movido o eliminado. Quítalo de la lista y vuelve a importarlo desde su nueva ubicación.");
                return;
            }
            var primaryExists = File.Exists(vault.VaultFilePath);
            var backupInfo = _vaultService.InspectVaultBackup(vault);
            var hasDuplicatePathReferences = Vaults.Count(candidate => candidate.VaultFilePath is not null
                && PathsEqual(candidate.VaultFilePath, vault.VaultFilePath)) > 1;
            var repairedDuplicatePathReferences = false;
            if (!primaryExists && !backupInfo.BackupExists)
            {
                await ShowMessageAsync("No se encuentra la bóveda", "El archivo cifrado fue movido o eliminado y no existe una copia anterior recuperable.");
                return;
            }

            // El doble clic debe ofrecer el comportamiento habitual de una bóveda: abrirla para editar.
            // La consulta sin escritura continúa disponible desde el panel principal.
            var useReadOnlyVirtual = directActivation && _state.OpenVaultsReadOnlyByDefault
                && primaryExists && VaultFormatV3.IsFormat(vault.VaultFilePath);
            if (!directActivation && primaryExists && VaultFormatV3.IsFormat(vault.VaultFilePath))
            {
                var modePanel = new StackPanel { Spacing = 8 };
                modePanel.Children.Add(new TextBlock
                {
                    Text = "Elige cómo quieres abrir tus archivos. Consultar permite verlos sin crear una copia completa en el disco. Editar permite modificar y guardar cambios al bloquear la bóveda.",
                    TextWrapping = TextWrapping.Wrap
                });
                modePanel.Children.Add(new TextBlock
                {
                    Text = "Consultar es recomendable cuando no necesitas modificar archivos.",
                    Foreground = Application.Current.Resources["MutedTextBrush"] as Brush,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
                var modeDialog = CreateDialog("Abrir bóveda", modePanel, "Ver archivos", "Cancelar");
                modeDialog.SecondaryButtonText = "Editar archivos";
                var modeResult = await modeDialog.ShowAsync();
                if (modeResult == ContentDialogResult.None) return;
                useReadOnlyVirtual = modeResult == ContentDialogResult.Primary;
            }

            string? mountPath = null;
            string? recoverableBackupPassword = null;
            VaultBackupValidation? recoverableBackup = null;
            var unlock = new UnlockWindow(vault.Name, "Introduce la contraseña para abrir la bóveda", async candidatePassword =>
            {
                recoverableBackupPassword = null;
                recoverableBackup = null;
                VaultContainer? imported = null;
                var valid = false;
                if (primaryExists)
                {
                    if (importIdentityFromContainer || hasDuplicatePathReferences)
                    {
                        imported = await _vaultService.LoadVaultAsync(vault.VaultFilePath, candidatePassword);
                        valid = imported is not null;
                    }
                    else
                    {
                        valid = await _vaultService.VerifyVaultPasswordAsync(vault.VaultFilePath, candidatePassword);
                    }
                }
                if (valid && imported is not null)
                {
                    // An Explorer activation has no local record yet. The same
                    // reconciliation also repairs duplicate local references
                    // left by an older activation attempt. The authenticated
                    // encrypted manifest is authoritative for this identity.
                    vault.Id = imported.Id;
                    vault.Name = imported.Name;
                    vault.Description = imported.Description;
                    vault.AutoLockMinutes = imported.AutoLockMinutes;
                    vault.CreatedUtc = imported.CreatedUtc;
                    vault.ModifiedUtc = imported.ModifiedUtc;
                    repairedDuplicatePathReferences = hasDuplicatePathReferences;
                }
                if (valid) vault.BackupNeedsAttention = false;
                if (!valid && backupInfo.BackupExists)
                {
                    recoverableBackup = await _vaultService.ValidateVaultBackupAsync(vault, candidatePassword);
                    if (recoverableBackup.BackupValid)
                    {
                        recoverableBackupPassword = candidatePassword;
                        var accepted = _localAuthenticationThrottle.Verify($"vault:{vault.Id:N}", true,
                            "Contraseña incorrecta.");
                        RecordLocalAuthenticationActivity(vault.Name, accepted,
                            "Contraseña incorrecta al abrir la bóveda");
                        return accepted.Attempt;
                    }
                }
                var throttle = _localAuthenticationThrottle.Verify($"vault:{vault.Id:N}", valid,
                    "Contraseña incorrecta.");
                RecordLocalAuthenticationActivity(vault.Name, throttle,
                    "Contraseña incorrecta al abrir la bóveda");
                if (!throttle.Attempt.Success) return throttle.Attempt;
                mountPath = useReadOnlyVirtual
                    ? await _vaultService.MountReadOnlyVaultAsync(vault, candidatePassword)
                    : VaultFormatV3.IsFormat(vault.VaultFilePath)
                        ? await _vaultService.MountReadWriteVaultAsync(vault, candidatePassword)
                        : await _vaultService.MountVaultAsync(vault, candidatePassword);
                return mountPath is null
                    ? new UnlockAttemptResult(false, _vaultService.LastError
                        ?? (useReadOnlyVirtual
                            ? "No se pudo crear la unidad virtual de solo lectura."
                            : "No se pudo abrir la bóveda para editar."))
                    : UnlockAttemptResult.Accepted;
            });
            if (!await ShowUnlockWindowAsync(unlock)) return;
            if (recoverableBackupPassword is not null && recoverableBackup is not null)
            {
                if (!recoverableBackup.PrimaryValid)
                {
                    MarkVaultBackupAttention(vault,
                        "El contenedor principal no superó la autenticación, pero su copia cifrada anterior sí");
                    await SaveAsync();
                }
                if (!await RestoreDetectedVaultBackupAsync(vault, recoverableBackupPassword, recoverableBackup)) return;
                if (useReadOnlyVirtual && !VaultFormatV3.IsFormat(vault.VaultFilePath))
                    useReadOnlyVirtual = false;
                mountPath = useReadOnlyVirtual
                    ? await _vaultService.MountReadOnlyVaultAsync(vault, recoverableBackupPassword)
                    : VaultFormatV3.IsFormat(vault.VaultFilePath)
                        ? await _vaultService.MountReadWriteVaultAsync(vault, recoverableBackupPassword)
                        : await _vaultService.MountVaultAsync(vault, recoverableBackupPassword);
                if (mountPath is null)
                {
                    await ShowMessageAsync("No se pudo abrir", LocalizationService.UserFacingMessage(_vaultService.LastError, "La copia se restauró, pero no se pudo abrir la bóveda."));
                    return;
                }
            }
            if (mountPath is null) return;
            if (repairedDuplicatePathReferences)
            {
                foreach (var duplicate in Vaults.Where(candidate => !ReferenceEquals(candidate, vault)
                    && candidate.VaultFilePath is not null
                    && PathsEqual(candidate.VaultFilePath, vault.VaultFilePath)).ToArray())
                {
                    duplicate.PropertyChanged -= Vault_PropertyChanged;
                    Vaults.Remove(duplicate);
                }
                AddActivity(vault.Name, "Referencias duplicadas de la bóveda corregidas tras validar el contenedor");
            }
            var journalRecovered = _vaultService.LastMountUsedJournal;
            MarkVaultAsRecent(vault);
            RefreshVaults();
            await RefreshVaultRecoveryItemsAsync();
            AddActivity(vault.Name, journalRecovered
                ? "Cambios recuperados de una sesión interrumpida; guarda y bloquea la bóveda para consolidarlos"
                : useReadOnlyVirtual
                    ? $"Consulta segura abierta durante {vault.AutoLockMinutes} min"
                    : "Bóveda abierta para editar; guarda y bloquea antes de cerrar");
            await SaveAsync();
            if (!TryOpenDirectory(mountPath))
                await ShowMessageAsync("No se pudo abrir el Explorador", $"La bóveda está abierta en:{Environment.NewLine}{mountPath}");
        }
        finally
        {
            if (!gateHeld) _vaultOperationGate.Release();
        }
    }

    private void Tray_SecurityEventReceived(VaultSecurityTrigger trigger)
    {
        DispatcherQueue.TryEnqueue(() => _ = HandleVaultSecurityEventAsync(trigger));
    }

    private async Task HandleVaultSecurityEventAsync(VaultSecurityTrigger trigger)
    {
        await _vaultSecurityEventGate.WaitAsync();
        try
        {
            if (!_initialized) return;

            // A Windows security boundary also revokes access to the management panel.
            _sessionUnlocked = false;
            var openVaults = Vaults.Where(vault => vault.IsMounted).ToArray();
            if (openVaults.Length == 0) return;

            var reason = WindowsSecurityMessageRouter.GetActivityLabel(trigger);
            var locked = await LockAllVaultsAsync(showErrors: false);
            if (locked >= 0)
            {
                RefreshVaults();
                ClearVaultRecoveryWarningIfResolved();
                AddActivity("ProtectedApp",
                    $"{locked} bóveda(s) guardadas y bloqueadas automáticamente por {reason}");
            }
            else
            {
                var remaining = Vaults.Where(vault => vault.IsMounted).ToArray();
                var paths = string.Join(Environment.NewLine, remaining
                    .Select(vault => $"• {vault.Name}: {vault.MountPath}")
                    .Where(line => !string.IsNullOrWhiteSpace(line)));
                _state.VaultRecoveryWarningPending = true;
                _state.VaultRecoveryWarningMessage =
                    $"Windows inició {reason}, pero no se pudo guardar y bloquear al menos una bóveda. " +
                    $"ProtectedApp conservó sus carpetas de trabajo para no perder cambios.{Environment.NewLine}{Environment.NewLine}{paths}";
                foreach (var vault in remaining)
                    AddActivity(vault.Name,
                        $"No se pudo guardar y bloquear la bóveda durante {reason}; se conservó su carpeta de trabajo");
            }
            await SaveAsync();
        }
        catch (Exception)
        {
            _state.VaultRecoveryWarningPending = true;
            _state.VaultRecoveryWarningMessage =
                "ProtectedApp encontró un error al proteger las bóvedas tras un evento de Windows. Sus carpetas de trabajo se conservaron para no perder cambios.";
            AddActivity("ProtectedApp", "Error al proteger las bóvedas ante un evento de Windows.");
            try { await SaveAsync(); } catch { }
        }
        finally { _vaultSecurityEventGate.Release(); }
    }

    private async Task ShowPendingVaultRecoveryWarningAsync()
    {
        if (!_sessionUnlocked || !_state.VaultRecoveryWarningPending || !_appWindow.IsVisible) return;

        await _dialogGate.WaitAsync();
        try
        {
            if (!_sessionUnlocked || !_state.VaultRecoveryWarningPending) return;
            var message = string.IsNullOrWhiteSpace(_state.VaultRecoveryWarningMessage)
                ? "Una bóveda no pudo cerrarse de forma segura. Revisa sus archivos de trabajo antes de continuar."
                : _state.VaultRecoveryWarningMessage;
            var dialog = CreateDialog(
                "Bóveda pendiente de recuperación",
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                "Revisar bóvedas",
                null);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            _state.VaultRecoveryWarningPending = false;
            _state.VaultRecoveryWarningMessage = null;
            AddActivity("ProtectedApp", "Aviso de recuperación de bóveda revisado por el usuario");
            await SaveAsync();
            SelectNavigation("vaults");
            await RefreshVaultRecoveryItemsAsync();
        }
        finally { _dialogGate.Release(); }
    }
}
