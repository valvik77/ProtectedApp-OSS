using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private void ApplySavedLanguagePreference()
    {
        LocalizationService.SetLanguage(_state.LanguagePreference);
        _tray.RefreshLocalizedText();
        LocalizationService.ApplyTo(Root);
        TranslateLanguageOptions();
        RefreshLocalizedBoundValues();
        if (TpmProtectionStatusText is not null) RefreshTpmProtectionStatus();
        SelectNavigation(_selectedNavigation);
        // Some flyouts and templated controls are materialized after startup.
        // Run once more after layout so the selected language also reaches
        // their generated visual trees.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            LocalizationService.ApplyTo(Root);
            RefreshLocalizedBoundValues();
            if (TpmProtectionStatusText is not null) RefreshTpmProtectionStatus();
        });
    }

    // Bindings calculate several labels from application state. They are not
    // literal XAML text, so notify them explicitly after a language switch.
    private void RefreshLocalizedBoundValues()
    {
        foreach (var application in Applications) application.RefreshLocalizedText();
        foreach (var vault in Vaults) vault.RefreshLocalizedText();
        foreach (var entry in Activity) entry.RefreshLocalizedText();
        foreach (var diagnostic in Diagnostics) diagnostic.RefreshLocalizedText();
        if (_initialized)
        {
            RefreshCategoryChips();
            RefreshVisibleApps();
            RefreshVisibleActivity();
        }
    }

    private void TranslateLanguageOptions()
    {
        if (LanguageBox is null) return;
        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Content is string content) item.Content = LocalizationService.T(content);
        }
    }

    private void SelectLanguagePreference(string? preference)
    {
        var normalized = LocalizationService.NormalizeLanguage(preference);
        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                return;
            }
        }
    }

    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        var preference = LocalizationService.NormalizeLanguage(item.Tag as string);
        _state.LanguagePreference = preference == "system" ? null : preference;
        ApplySavedLanguagePreference();
        if (_initialized) await SaveAsync(synchronizeGuardian: false);
    }
}
