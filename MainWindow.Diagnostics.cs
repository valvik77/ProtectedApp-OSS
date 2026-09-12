using Microsoft.UI.Xaml;
using ProtectedApp.Models;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void RunDiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        await RunDiagnosticsAsync();

    private async Task RunDiagnosticsAsync()
    {
        RunDiagnosticsButton.IsEnabled = false;
        RunDiagnosticsButton.Content = LocalizationService.T("Comprobando…");
        DiagnosticsSummaryText.Text = LocalizationService.T("Comprobando la protección");
        DiagnosticsSummaryDetail.Text = LocalizationService.T("Consultando Guardian y los componentes instalados…");
        try
        {
            var results = await _diagnosticsService.RunAsync(Applications.ToArray(), Folders.ToArray(), Vaults.ToArray(),
                _guardianToken);
            Diagnostics.Clear();
            foreach (var result in results) Diagnostics.Add(result);
            EmptyDiagnosticsText.Visibility = Diagnostics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var errors = results.Count(result => result.Severity == DiagnosticSeverity.Error);
            var warnings = results.Count(result => result.Severity == DiagnosticSeverity.Warning);
            RepairDiagnosticsButton.Visibility = results.Any(result => result.CanRepair && result.Severity != DiagnosticSeverity.Success)
                ? Visibility.Visible
                : Visibility.Collapsed;
            DiagnosticsSummaryIcon.Glyph = errors > 0 ? "\ue88e" : warnings > 0 ? "\uf083" : "\ue668";
            DiagnosticsSummaryText.Text = errors > 0
                ? LocalizationService.IsEnglish ? $"{errors} errors found" : $"Se encontraron {errors} errores"
                : warnings > 0
                    ? LocalizationService.IsEnglish ? $"Protection is healthy with {warnings} warnings" : $"Protección correcta con {warnings} avisos"
                    : LocalizationService.T("Todas las comprobaciones son correctas");
            DiagnosticsSummaryDetail.Text = LocalizationService.IsEnglish
                ? $"{results.Count} checks · {DateTime.Now:HH:mm:ss}"
                : $"{results.Count} comprobaciones · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            DiagnosticsSummaryIcon.Glyph = "\ue88e";
            DiagnosticsSummaryText.Text = LocalizationService.T("No se pudo completar el diagnóstico");
            DiagnosticsSummaryDetail.Text = LocalizationService.UserFacingError(ex);
        }
        finally
        {
            RunDiagnosticsButton.Content = LocalizationService.T("Comprobar de nuevo");
            RunDiagnosticsButton.IsEnabled = true;
        }
    }

    private async void RepairDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Autorizar reparación de ProtectedApp")) return;
        RepairDiagnosticsButton.IsEnabled = false;
        RunDiagnosticsButton.IsEnabled = false;
        DiagnosticsSummaryText.Text = LocalizationService.T("Reparando componentes de protección");
        DiagnosticsSummaryDetail.Text = LocalizationService.T("Windows solicitará permiso de administrador para reinstalar las capas protegidas.");
        try
        {
            if (!await SetGuardianInstalledAsync(true)) return;
            await RunDiagnosticsAsync();
            var remaining = Diagnostics.Count(result => result.CanRepair && result.Severity != DiagnosticSeverity.Success);
            await ShowMessageAsync(
                remaining == 0 ? "Reparación completada" : "Reparación incompleta",
                remaining == 0
                    ? "Guardian, Gate, la política y la tarea SYSTEM se han comprobado correctamente."
                    : $"Quedan {remaining} componentes que requieren revisión.");
        }
        finally
        {
            RepairDiagnosticsButton.IsEnabled = true;
            RunDiagnosticsButton.IsEnabled = true;
        }
    }
}
