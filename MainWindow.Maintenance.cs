using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var uninstaller = Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (uninstaller is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true });
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return;
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("No se pudo iniciar la desinstalación", LocalizationService.UserFacingError(ex));
                return;
            }
        }

        await ShowMessageAsync(
            "Desinstalador no disponible",
            "Esta copia se ejecuta desde una carpeta publicada y no fue instalada con Setup.exe. Instálala primero o retira manualmente el servicio desde Configuración.");
    }

    private async void ManualUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        ManualUpdateButton.IsEnabled = false;
        Windows.Storage.StorageFile? selectedFile;
        try { selectedFile = await picker.PickSingleFileAsync(); }
        catch (Exception ex)
        {
            ManualUpdateButton.IsEnabled = true;
            await ShowMessageAsync("No se pudo seleccionar el instalador", LocalizationService.UserFacingError(ex));
            return;
        }
        finally { RestoreMainWindowFocus(); }
        if (selectedFile is null)
        {
            ManualUpdateButton.IsEnabled = true;
            return;
        }

        try
        {
            UpdateStatusText.Text = "Verificando firma, identidad y versión…";
            var validation = await ManualUpdateService.ValidatePackageAsync(selectedFile.Path);
            if (!validation.Success || validation.Package is null)
            {
                UpdateStatusText.Text = $"Versión instalada {ManualUpdateService.GetDisplayVersion(ManualUpdateService.GetInstalledVersion())}";
                await ShowMessageAsync("Actualización rechazada",
                    validation.Error ?? "El instalador no ha superado la validación de seguridad.");
                return;
            }

            var package = validation.Package;
            var details = new StackPanel { Spacing = 8, MaxWidth = 520 };
            details.Children.Add(new TextBlock
            {
                Text = $"ProtectedApp {ManualUpdateService.GetDisplayVersion(package.InstalledVersion)}  →  {ManualUpdateService.GetDisplayVersion(package.PackageVersion)}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            details.Children.Add(new TextBlock
            {
                Text = $"Firmante verificado: {package.SignerName}",
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
            });
            details.Children.Add(new TextBlock
            {
                Text = "SHA-256",
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["SubtleTextBrush"]
            });
            details.Children.Add(new TextBlock
            {
                Text = package.Sha256,
                FontFamily = (FontFamily)Application.Current.Resources["PrototypeMonoFont"],
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            details.Children.Add(new TextBlock
            {
                Text = "Las bóvedas abiertas se guardarán y cerrarán antes de iniciar Setup. Windows solicitará permiso de administrador.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"]
            });

            var confirmation = CreateDialog(
                "Actualización verificada",
                details,
                "Instalar actualización",
                "Cancelar");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            if (!await VerifyMasterAsync("Autorizar actualización de ProtectedApp")) return;

            var lockedVaults = await LockAllVaultsAsync(showErrors: true);
            if (lockedVaults < 0) return;

            AddActivity("ProtectedApp",
                $"Actualización manual autorizada: {ManualUpdateService.GetDisplayVersion(package.InstalledVersion)} → {ManualUpdateService.GetDisplayVersion(package.PackageVersion)}; paquete {package.Sha256}");
            await SaveAsync();
            var stagedValidation = await ManualUpdateService.StageValidatedPackageAsync(package);
            if (!stagedValidation.Success || stagedValidation.Package is null)
            {
                UpdateStatusText.Text = $"Versión instalada {ManualUpdateService.GetDisplayVersion(ManualUpdateService.GetInstalledVersion())}";
                await ShowMessageAsync("Actualización rechazada", stagedValidation.Error
                    ?? "No se pudo preparar una copia segura del instalador.");
                return;
            }
            var stagedPackage = stagedValidation.Package;
            // Keep the exact validated executable locked against writes and
            // deletion until ShellExecute has opened it, including the UAC wait.
            using var installerLock = new FileStream(stagedPackage.FilePath, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var finalValidation = await ManualUpdateService.ValidatePackageAsync(stagedPackage.FilePath,
                stagedPackage.SignerThumbprint, stagedPackage.InstalledVersion);
            if (!finalValidation.Success || finalValidation.Package?.Sha256 != stagedPackage.Sha256)
                throw new IOException("El instalador cambió antes de su ejecución.");
            UpdateStatusText.Text = $"Iniciando actualización {ManualUpdateService.GetDisplayVersion(package.PackageVersion)}…";

            var startInfo = new ProcessStartInfo(stagedPackage.FilePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(stagedPackage.FilePath) ?? AppContext.BaseDirectory
            };
            if (Process.Start(startInfo) is null)
                await ShowMessageAsync("No se pudo actualizar", "Windows no pudo iniciar el instalador seleccionado.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            UpdateStatusText.Text = $"Versión instalada {ManualUpdateService.GetDisplayVersion(ManualUpdateService.GetInstalledVersion())}";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Versión instalada {ManualUpdateService.GetDisplayVersion(ManualUpdateService.GetInstalledVersion())}";
            await ShowMessageAsync("No se pudo iniciar la actualización", LocalizationService.UserFacingError(ex));
        }
        finally
        {
            ManualUpdateButton.IsEnabled = true;
        }
    }
}
