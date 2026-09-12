using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Models;
using ProtectedApp.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private void RefreshStats()
    {
        EmptyActivityText.Visibility = Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshDashboard();
    }

    private void RefreshDashboard()
    {
        if (DashboardAppsCountText is null) return;

        var enabledRules = Applications.Count(application => application.IsEnabled);
        var mountedVaults = Vaults.Count(vault => vault.IsMounted);
        var recentEvents = ActivityStatisticsService.Create(_activityHistory, DateTimeOffset.Now).Events;
        var guardianAvailable = _guardianManaged || GuardianServiceDetector.IsRunning();

        DashboardAppsCountText.Text = enabledRules.ToString("N0");
        DashboardAppsDetailText.Text = LocalizationService.IsEnglish
            ? $"{Applications.Count:N0} protected application{(Applications.Count == 1 ? string.Empty : "s")}"
            : $"{Applications.Count:N0} aplicaciones protegidas";
        DashboardVaultCountText.Text = Vaults.Count.ToString("N0");
        DashboardVaultDetailText.Text = LocalizationService.IsEnglish
            ? $"{mountedVaults:N0} open vault{(mountedVaults == 1 ? string.Empty : "s")}"
            : $"{mountedVaults:N0} bóvedas abiertas";
        DashboardActivityCountText.Text = recentEvents.ToString("N0");
        DashboardGuardianText.Text = LocalizationService.T(guardianAvailable ? "Guardian conectado" : "Guardian requiere atención");
        SidebarStatusText.Text = DashboardGuardianText.Text;
        DashboardDiagnosticsText.Text = LocalizationService.T(Diagnostics.Count == 0 ? "Pendiente" : "Disponible");
        DashboardSystemDetailText.Text = LocalizationService.T(Diagnostics.Count == 0
            ? "La comprobación detallada está disponible en Diagnóstico."
            : "Consulta Diagnóstico para revisar el estado de los componentes.");
    }

    private void AddActivity(string appName, string message)
    {
        _activityHistory.Insert(0, new ActivityEntry
        {
            AppName = appName,
            CanonicalMessage = message,
            Message = LocalizationService.T(message),
            Kind = ClassifyActivity(message)
        });
        while (_activityHistory.Count > 500) _activityHistory.RemoveAt(_activityHistory.Count - 1);
        RefreshVisibleActivity();
        _activitySaveTimer.Stop();
        _activitySaveTimer.Start();
    }

    private void ActivitySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) RefreshVisibleActivity();
    }

    private void ActivityFilterCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton selected) return;
        _activityFilter = selected.Tag?.ToString() ?? "recent";
        ActivityEvents24hFilterButton.IsChecked = ReferenceEquals(selected, ActivityEvents24hFilterButton);
        ActivityBlockedFilterButton.IsChecked = ReferenceEquals(selected, ActivityBlockedFilterButton);
        ActivityFailedPasswordsFilterButton.IsChecked = ReferenceEquals(selected, ActivityFailedPasswordsFilterButton);
        ActivityAlertsFilterButton.IsChecked = ReferenceEquals(selected, ActivityAlertsFilterButton);
        if (_initialized) RefreshVisibleActivity();
    }

    private void RefreshVisibleActivity()
    {
        foreach (var entry in _activityHistory)
            entry.RefreshLocalizedText();
        var query = ActivitySearchBox.Text?.Trim() ?? string.Empty;
        var filter = _activityFilter;
        var since = DateTimeOffset.Now.AddHours(-24);
        var filtered = _activityHistory.Where(entry =>
            (query.Length == 0
                || entry.AppName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || entry.Message.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            && filter switch
            {
                "recent" => entry.Timestamp >= since,
                "blocked" => entry.Kind == ActivityEventKind.Blocked,
                "failed" => entry.IsFailedPassword,
                "error" => entry.Kind is ActivityEventKind.Warning or ActivityEventKind.Error,
                _ => true
            }).ToArray();

        Activity.Clear();
        foreach (var entry in filtered) Activity.Add(entry);
        ExportActivityButton.IsEnabled = Activity.Count > 0;
        ClearActivityButton.IsEnabled = _activityHistory.Count > 0;
        var statistics = ActivityStatisticsService.Create(_activityHistory, DateTimeOffset.Now);
        ActivityEvents24hText.Text = statistics.Events.ToString("N0");
        ActivityBlocked24hText.Text = statistics.Blocked.ToString("N0");
        ActivityFailedPasswords24hText.Text = statistics.FailedPasswords.ToString("N0");
        ActivityAlerts24hText.Text = statistics.Alerts.ToString("N0");
        // These labels are hosted inside a templated filter control. Set them
        // during every refresh instead of relying on visual-tree traversal,
        // which can run before the template is materialized at startup.
        ActivityEvents24hLabel.Text = LocalizationService.T("EVENTOS · 24 H");
        ActivityBlockedLabel.Text = LocalizationService.T("BLOQUEOS");
        ActivityFailedPasswordsLabel.Text = LocalizationService.T("CONTRASEÑAS FALLIDAS");
        ActivityAlertsLabel.Text = LocalizationService.T("AVISOS Y ERRORES");
        ExportActivityLabel.Text = LocalizationService.T("Exportar…");
        ClearActivityLabel.Text = LocalizationService.T("Limpiar");
        UpdateActivityFilterCardAppearance();
        ActivityCountText.Text = filter == "all" && query.Length == 0
            ? LocalizationService.IsEnglish
                ? $"{_activityHistory.Count} events"
                : $"{_activityHistory.Count} eventos"
            : LocalizationService.IsEnglish
                ? $"{filtered.Length} of {_activityHistory.Count}"
                : $"{filtered.Length} de {_activityHistory.Count}";
        EmptyActivityText.Text = _activityHistory.Count == 0
            ? LocalizationService.T("Aún no hay actividad")
            : LocalizationService.T("No hay eventos que coincidan con el filtro");
        RefreshStats();
    }

    private static bool IsFailedPasswordActivity(ActivityEntry entry)
    {
        var source = string.IsNullOrWhiteSpace(entry.CanonicalMessage) ? entry.Message : entry.CanonicalMessage;
        return (source.Contains("contraseña", StringComparison.OrdinalIgnoreCase)
                || source.Contains("password", StringComparison.OrdinalIgnoreCase))
            && (source.Contains("incorrecta", StringComparison.OrdinalIgnoreCase)
                || source.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
                || source.Contains("fallid", StringComparison.OrdinalIgnoreCase)
                || source.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateActivityFilterCardAppearance()
    {
        var selected = new SolidColorBrush(Microsoft.UI.Colors.White);
        SetActivityFilterCardAppearance(ActivityEvents24hFilterButton, ActivityEvents24hLabel,
            ActivityEvents24hText, (Brush)Application.Current.Resources["PrimaryTextBrush"], selected);
        SetActivityFilterCardAppearance(ActivityBlockedFilterButton, ActivityBlockedLabel,
            ActivityBlocked24hText, (Brush)Application.Current.Resources["AccentIndigoBrush"], selected);
        SetActivityFilterCardAppearance(ActivityFailedPasswordsFilterButton, ActivityFailedPasswordsLabel,
            ActivityFailedPasswords24hText, (Brush)Application.Current.Resources["WarningBrush"], selected);
        SetActivityFilterCardAppearance(ActivityAlertsFilterButton, ActivityAlertsLabel,
            ActivityAlerts24hText, (Brush)Application.Current.Resources["DangerBrush"], selected);
    }

    private static void SetActivityFilterCardAppearance(ToggleButton button, TextBlock label,
        TextBlock value, Brush normalValue, Brush selected)
    {
        var isSelected = button.IsChecked == true;
        button.Foreground = isSelected ? selected : (Brush)Application.Current.Resources["PrimaryTextBrush"];
        label.Foreground = isSelected ? selected : (Brush)Application.Current.Resources["SubtleTextBrush"];
        value.Foreground = isSelected ? selected : normalValue;
    }

    private async void ExportActivityButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = Activity.ToList();
        if (entries.Count == 0)
        {
            await ShowMessageAsync("No hay eventos", "No hay actividad visible para exportar con los filtros actuales.");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"ProtectedApp-Actividad-{DateTime.Now:yyyy-MM-dd}"
        };
        picker.FileTypeChoices.Add("CSV compatible con Excel", new List<string> { ".csv" });
        picker.FileTypeChoices.Add("JSON estructurado", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file;
        try { file = await picker.PickSaveFileAsync(); }
        finally { RestoreMainWindowFocus(); }
        if (file is null) return;

        try
        {
            var bytes = Path.GetExtension(file.Path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? ActivityExportService.CreateJson(entries)
                : ActivityExportService.CreateCsv(entries);
            await File.WriteAllBytesAsync(file.Path, bytes);
            AddActivity("ProtectedApp", $"Registro de actividad exportado con {entries.Count} eventos");
            await ShowMessageAsync("Actividad exportada",
                $"Se han exportado {entries.Count} eventos visibles. La búsqueda y el filtro actuales se han respetado.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddActivity("ProtectedApp", "No se pudo exportar el registro de actividad.");
            await ShowMessageAsync("No se pudo exportar", LocalizationService.UserFacingError(ex));
        }
    }

    private async void ClearActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activityHistory.Count == 0) return;
        var dialog = CreateDialog(
            "Limpiar historial",
            new TextBlock
            {
                Text = $"Se eliminarán permanentemente los {_activityHistory.Count} eventos guardados en este equipo.",
                TextWrapping = TextWrapping.Wrap
            },
            "Limpiar",
            "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _activityHistory.Clear();
        RefreshVisibleActivity();
        await PersistActivityAsync();
    }

    private async void ActivitySaveTimer_Tick(object? sender, object e)
    {
        _activitySaveTimer.Stop();
        await PersistActivityAsync();
    }

    private async Task PersistActivityAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            _state.Applications = Applications.ToList();
            _state.ActivityHistory = _activityHistory.ToList();
            await _store.SaveAsync(_state);
        }
        finally { _saveGate.Release(); }
    }

    private static ActivityEventKind ClassifyActivity(string message)
    {
        if ((message.Contains("contraseña", StringComparison.CurrentCultureIgnoreCase)
                && message.Contains("incorrecta", StringComparison.CurrentCultureIgnoreCase))
            || message.Contains("sincronización de política pendiente", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("intentos fallidos", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("manipulación", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("forzad", StringComparison.CurrentCultureIgnoreCase))
            return ActivityEventKind.Warning;
        if (message.Contains("no se pudo", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("rechaz", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("error", StringComparison.CurrentCultureIgnoreCase))
            return ActivityEventKind.Error;
        if (message.Contains("intercept", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("bloque", StringComparison.CurrentCultureIgnoreCase))
            return ActivityEventKind.Blocked;
        if (message.Contains("autoriz", StringComparison.CurrentCultureIgnoreCase)
            || message.Contains("iniciad", StringComparison.CurrentCultureIgnoreCase))
            return ActivityEventKind.Access;
        return ActivityEventKind.System;
    }
}
