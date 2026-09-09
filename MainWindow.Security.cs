using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_guardianManaged)
        {
            await ShowMessageAsync("Guardian no disponible", "No se puede aplicar el bloqueo inmediato mientras el servicio Guardian no esté activo. Repara el servicio desde Configuración.");
            return;
        }
        var dialog = CreateDialog(
            "Bloquear ahora",
            new TextBlock
            {
                Text = "Se revocarán los periodos de confianza. Las aplicaciones protegidas recibirán primero una solicitud de cierre normal y tendrán hasta 5 segundos para cerrarse; las que sigan abiertas se cerrarán forzosamente. Se bloquearán las carpetas y se guardarán y cerrarán las bóvedas.",
                TextWrapping = TextWrapping.Wrap
            },
            "Bloquear ahora",
            "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var lockedFolders = 0;
        var gracefullyClosed = 0;
        var forciblyTerminated = 0;
        if (_guardianManaged)
        {
            if (_guardianToken is null && !await VerifyMasterAsync("Autorizar bloqueo inmediato")) return;
            var response = await _guardianClient.LockAllAsync(_guardianToken ?? string.Empty);
            if (!response.Success
                && response.Error?.Contains("Autorización de administración no válida", StringComparison.OrdinalIgnoreCase) == true)
            {
                _guardianToken = null;
                if (!await VerifyMasterAsync("Autorizar bloqueo inmediato")) return;
                response = await _guardianClient.LockAllAsync(_guardianToken ?? string.Empty);
            }
            if (!response.Success)
            {
                await ShowMessageAsync("No se pudo bloquear", LocalizationService.UserFacingMessage(response.Error, "Guardian no confirmó la revocación de accesos."));
                return;
            }
            lockedFolders = response.AffectedFolderCount;
            gracefullyClosed = response.GracefulCloseCount;
            forciblyTerminated = response.ForcedTerminationCount;
            foreach (var folder in Folders)
            {
                folder.UnlockedUntilUtc = null;
                if (folder.IsEnabled) FolderIconService.TryApply(folder);
            }
            _monitor.RevokeAllAuthorizations();
        }

        var lockedVaults = await LockAllVaultsAsync(showErrors: true);
        if (lockedVaults < 0) return;
        AddActivity("ProtectedApp",
            $"Bloqueo inmediato aplicado; {gracefullyClosed} aplicaciones cerradas normalmente, {forciblyTerminated} cierres forzados, {lockedFolders} carpetas y {lockedVaults} bóvedas bloqueadas");
        await SaveAsync();
        HideToTray(showNotification: false, forceLock: true);
    }

    private async void TravelModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_guardianManaged)
        {
            await ShowMessageAsync("Guardian no disponible", "El bloqueo inmediato requiere que Guardian esté activo. Repara el servicio desde Configuración.");
            return;
        }

        var dialog = CreateDialog(
            "Bloqueo inmediato",
            new TextBlock
            {
                Text = "Las aplicaciones protegidas recibirán primero una solicitud de cierre normal y tendrán hasta 5 segundos para cerrarse; las que sigan abiertas se cerrarán forzosamente. Después se bloquearán las carpetas, se guardarán y desmontarán las bóvedas abiertas y se bloqueará Windows.",
                TextWrapping = TextWrapping.Wrap
            },
            "Bloquear ahora",
            "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (_guardianToken is null && !await VerifyMasterAsync("Autorizar bloqueo inmediato")) return;
        var response = await _guardianClient.LockAllAsync(_guardianToken ?? string.Empty);
        if (!response.Success
            && response.Error?.Contains("Autorización de administración no válida", StringComparison.OrdinalIgnoreCase) == true)
        {
            _guardianToken = null;
            if (!await VerifyMasterAsync("Autorizar bloqueo inmediato")) return;
            response = await _guardianClient.LockAllAsync(_guardianToken ?? string.Empty);
        }
        if (!response.Success)
        {
            await ShowMessageAsync("No se pudo aplicar el bloqueo inmediato", LocalizationService.UserFacingMessage(response.Error, "Guardian no confirmó el bloqueo de accesos."));
            return;
        }

        foreach (var folder in Folders)
        {
            folder.UnlockedUntilUtc = null;
            if (folder.IsEnabled) FolderIconService.TryApply(folder);
        }
        _monitor.RevokeAllAuthorizations();
        var lockedVaults = await LockAllVaultsAsync(showErrors: true);
        if (lockedVaults < 0) return;

        AddActivity("Bloqueo inmediato",
            $"Aplicado; {response.GracefulCloseCount} aplicaciones cerradas normalmente, {response.ForcedTerminationCount} cierres forzados, {response.AffectedFolderCount} carpetas y {lockedVaults} bóvedas bloqueadas");
        await SaveAsync();
        HideToTray(showNotification: false, forceLock: true);
        LockWorkStation();
    }
}
