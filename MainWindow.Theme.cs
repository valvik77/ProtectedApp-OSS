using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private void ApplySavedThemePreference()
    {
        Root.RequestedTheme = ToElementTheme(_state.ThemePreference);
        SyncThemeToggle();
    }

    private void SyncThemeToggle()
    {
        if (ThemeLightButton is null) return;
        var preference = NormalizeThemePreference(_state.ThemePreference);
        foreach (var (button, selected) in new[]
        {
            (ThemeLightButton, preference == "light"),
            (ThemeSystemButton, preference == "system"),
            (ThemeDarkButton, preference == "dark")
        })
        {
            button.Background = (SolidColorBrush)Application.Current.Resources[selected ? "PrimaryActionBrush" : "ControlSurfaceBrush"];
            button.BorderBrush = (SolidColorBrush)Application.Current.Resources[selected ? "AccentIndigoBrush" : "ControlSurfaceBrush"];
            button.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            button.Foreground = (SolidColorBrush)Application.Current.Resources[selected ? "PrimaryActionTextBrush" : "MutedTextBrush"];
        }
    }

    private async void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var preference = (sender as FrameworkElement)?.Tag as string;
        _state.ThemePreference = NormalizeThemePreference(preference) switch
        {
            "system" => null,
            var selected => selected
        };
        Root.RequestedTheme = ToElementTheme(_state.ThemePreference);
        SyncThemeToggle();
        if (_initialized) await SaveAsync(synchronizeGuardian: false);
    }

    private static ElementTheme ToElementTheme(string? preference) => NormalizeThemePreference(preference) switch
    {
        "dark" => ElementTheme.Dark,
        "light" => ElementTheme.Light,
        _ => ElementTheme.Default
    };

    private static string NormalizeThemePreference(string? preference) =>
        preference?.Equals("dark", StringComparison.OrdinalIgnoreCase) == true ? "dark"
        : preference?.Equals("light", StringComparison.OrdinalIgnoreCase) == true ? "light"
        : "system";
}
