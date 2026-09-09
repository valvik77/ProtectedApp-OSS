using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("Protección administrada", "ProtectedApp aplica la protección exclusivamente mediante Guardian. No se puede pausar desde la interfaz.");
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag) SelectNavigation(tag);
    }

    private void SettingsSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string section) return;
        SelectSettingsSection(section);
    }

    private void SelectSettingsSection(string section)
    {
        SettingsGeneralSection.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSecuritySection.Visibility = section == "security" ? Visibility.Visible : Visibility.Collapsed;
        SettingsMaintenanceSection.Visibility = section == "maintenance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsInformationSection.Visibility = section == "information" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, selected) in new[]
        {
            (SettingsGeneralButton, section == "general"),
            (SettingsSecurityButton, section == "security"),
            (SettingsMaintenanceButton, section == "maintenance"),
            (SettingsInformationButton, section == "information")
        })
        {
            button.Background = (SolidColorBrush)Application.Current.Resources[selected ? "PrimaryActionBrush" : "ControlSurfaceBrush"];
            button.BorderBrush = (SolidColorBrush)Application.Current.Resources[selected ? "AccentIndigoBrush" : "CardStrokeBrush"];
            button.Foreground = (SolidColorBrush)Application.Current.Resources[selected ? "PrimaryActionTextBrush" : "PrimaryTextBrush"];
            button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void SelectNavigation(string tag)
    {
        _selectedNavigation = tag;
        AppsView.Visibility = tag == "apps" ? Visibility.Visible : Visibility.Collapsed;
        VaultsView.Visibility = tag == "vaults" ? Visibility.Visible : Visibility.Collapsed;
        ActivityView.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsView.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        LocalizationService.ApplyTo(tag switch
        {
            "vaults" => VaultsView,
            "activity" => ActivityView,
            "diagnostics" => DiagnosticsView,
            "settings" => SettingsView,
            "about" => AboutView,
            _ => AppsView
        });
        var (title, subtitle) = tag switch
        {
            "activity" => ("Actividad", "Consulta el historial de seguridad del equipo"),
            "vaults" => ("Bóvedas cifradas", "Protege archivos dentro de contenedores con contraseña"),
            "diagnostics" => ("Diagnóstico", "Comprueba que todas las capas de protección funcionan"),
            "settings" => ("Configuración", "Ajusta la protección y las credenciales"),
            "about" => ("Acerca de", "Información y componentes de ProtectedApp"),
            _ => ("Aplicaciones protegidas", "Controla qué programas necesitan autorización")
        };
        PageTitle.Text = LocalizationService.T(title);
        PageSubtitle.Text = LocalizationService.T(subtitle);
        foreach (var (button, selected) in new[] { (AppsNavButton, tag == "apps"), (VaultsNavButton, tag == "vaults"), (ActivityNavButton, tag == "activity"), (DiagnosticsNavButton, tag == "diagnostics"), (SettingsNavButton, tag == "settings"), (AboutNavButton, tag == "about") })
        {
            var light = Root.ActualTheme == ElementTheme.Light;
            button.Background = new SolidColorBrush(selected
                ? light ? Windows.UI.Color.FromArgb(255, 232, 232, 232) : Windows.UI.Color.FromArgb(16, 189, 147, 249)
                : Windows.UI.Color.FromArgb(0, 0, 0, 0));
            button.Foreground = new SolidColorBrush(selected
                ? light ? Windows.UI.Color.FromArgb(255, 31, 31, 31) : Windows.UI.Color.FromArgb(255, 248, 248, 242)
                : light ? Windows.UI.Color.FromArgb(255, 97, 97, 97) : Windows.UI.Color.FromArgb(255, 98, 114, 164));
            button.BorderBrush = new SolidColorBrush(selected
                ? light ? Windows.UI.Color.FromArgb(255, 0, 95, 184) : Windows.UI.Color.FromArgb(180, 96, 205, 255)
                : Windows.UI.Color.FromArgb(0, 0, 0, 0));
            button.BorderThickness = selected ? new Thickness(2, 0, 0, 0) : new Thickness(0);
        }
        if (tag == "settings")
        {
            SelectSettingsSection("general");
            RefreshGuardianStatus();
        }
        if (tag == "about") RefreshAboutInformation();
        if (tag == "vaults" && _initialized) _ = RefreshVaultRecoveryItemsAsync();
    }

    private void RefreshAboutInformation()
    {
        var executable = Environment.ProcessPath;
        var version = string.IsNullOrWhiteSpace(executable)
            ? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            : System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).ProductVersion;
        var unavailableVersion = LocalizationService.IsEnglish ? "unknown" : "desconocida";
        AboutVersionText.Text = $"{LocalizationService.T("Versión")} {version ?? unavailableVersion}";
    }

    private async void OpenLegalDocument_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string document) return;
        var fileName = document switch
        {
            "LICENSE" => "LICENSE",
            "THIRD-PARTY-NOTICES" => LocalizationService.IsEnglish ? "THIRD-PARTY-NOTICES-en.txt" : "THIRD-PARTY-NOTICES.txt",
            _ => document
        };
        var path = Path.Combine(AppContext.BaseDirectory, "Legal", fileName);
        if (!File.Exists(path))
        {
            await ShowMessageAsync("Documento no disponible", "Reinstala ProtectedApp para recuperar la documentación legal.");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("No se pudo abrir el documento", LocalizationService.UserFacingError(ex));
        }
    }
}
