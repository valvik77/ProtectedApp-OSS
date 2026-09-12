using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private Border CreateEditorSection(string title, string glyph, params UIElement[] fields)
    {
        var content = new StackPanel { Spacing = 12 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(new FontIcon
        {
            Glyph = glyph switch { "\uE71D" => "\ue5c3", "\uE72E" => "\ue899", "\uEDA2" => "\ue617", _ => "\uefd6" },
            FontFamily = (FontFamily)Application.Current.Resources["PrototypeIconFont"], FontSize = 20,
            Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 143, 205, 255), Windows.UI.Color.FromArgb(255, 0, 95, 184))
        });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(heading);
        foreach (var field in fields) content.Children.Add(field);
        return new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
            Background = ThemeBrush(Windows.UI.Color.FromArgb(255, 23, 28, 36), Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = ThemeBrush(Windows.UI.Color.FromArgb(255, 62, 72, 81), Windows.UI.Color.FromArgb(255, 229, 229, 229)),
            BorderThickness = new Thickness(1), Child = content
        };
    }

    private ContentDialog CreateDialog(string title, object content, string primary, string? close)
    {
        if (content is Panel panel) content = CreateDialogScroller(panel);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Style = Application.Current.Resources["ProtectedContentDialogStyle"] as Style,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            DefaultButton = ContentDialogButton.Primary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(close)) dialog.CloseButtonText = close;
        LocalizationService.ApplyTo(dialog);
        ApplyDialogPalette(dialog);
        dialog.Loaded += Dialog_Loaded;
        TrackDialogActivity(dialog);
        return dialog;
    }

    private void TrackDialogActivity(ContentDialog dialog)
    {
        dialog.Opened += (_, _) =>
        {
            LocalizationService.LanguageChanged += RefreshDialogLanguage;
            _openDialogCount++;
            RecordManagementActivity();
        };
        dialog.Closed += (_, _) =>
        {
            LocalizationService.LanguageChanged -= RefreshDialogLanguage;
            if (_openDialogCount > 0) _openDialogCount--;
            RecordManagementActivity();
        };
        void RefreshDialogLanguage() => dialog.DispatcherQueue.TryEnqueue(() => LocalizationService.ApplyTo(dialog));
    }

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentDialog dialog) return;
        // ContentDialog action labels are native properties, not descendants
        // of the visual tree, so TranslateTo cannot reach them by itself.
        LocalizationService.ApplyTo(dialog);
        ApplyDialogPalette(dialog);
        ApplyRoundedDialogButtons(dialog, dialog);
        dialog.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            ApplyRoundedDialogButtons(dialog, dialog));
    }

    private void ApplyDialogPalette(ContentDialog dialog)
    {
        var light = Root.ActualTheme == ElementTheme.Light;
        var background = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 248, 248, 248) : Windows.UI.Color.FromArgb(255, 27, 32, 40));
        var foreground = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 59, 59, 59) : Windows.UI.Color.FromArgb(255, 222, 226, 238));
        var border = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 229, 229, 229) : Windows.UI.Color.FromArgb(255, 62, 72, 81));
        var inputBorder = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 206, 206, 206) : Windows.UI.Color.FromArgb(255, 62, 72, 81));
        var surface = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 15, 20, 28));
        var hover = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 242, 242, 242) : Windows.UI.Color.FromArgb(255, 37, 42, 51));
        var selected = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 232, 232, 232) : Windows.UI.Color.FromArgb(255, 48, 53, 62));
        var selectedHover = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 220, 220, 220) : Windows.UI.Color.FromArgb(255, 143, 205, 255));
        var focus = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 0, 95, 184) : Windows.UI.Color.FromArgb(255, 143, 205, 255));
        var indicator = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 0, 95, 184) : Windows.UI.Color.FromArgb(255, 143, 205, 255));
        var placeholder = new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 118, 118, 118) : Windows.UI.Color.FromArgb(255, 143, 205, 255));

        dialog.RequestedTheme = Root.ActualTheme;
        dialog.Background = background;
        dialog.Foreground = foreground;
        dialog.BorderBrush = border;
        dialog.BorderThickness = new Thickness(1);
        dialog.Style = Application.Current.Resources["ProtectedContentDialogStyle"] as Style;
        dialog.CornerRadius = new CornerRadius(14);
        dialog.DefaultButton = !string.IsNullOrWhiteSpace(dialog.PrimaryButtonText) ? ContentDialogButton.Primary
            : !string.IsNullOrWhiteSpace(dialog.SecondaryButtonText) ? ContentDialogButton.Secondary
            : !string.IsNullOrWhiteSpace(dialog.CloseButtonText) ? ContentDialogButton.Close : ContentDialogButton.None;
        dialog.PrimaryButtonStyle = Application.Current.Resources["RoundedPrimaryButtonStyle"] as Style;
        dialog.SecondaryButtonStyle = Application.Current.Resources["RoundedDialogButtonStyle"] as Style;
        dialog.CloseButtonStyle = Application.Current.Resources["RoundedDialogButtonStyle"] as Style;

        dialog.Resources["ContentDialogBackground"] = background;
        dialog.Resources["ContentDialogTopOverlay"] = background;
        dialog.Resources["ContentDialogForeground"] = foreground;
        dialog.Resources["ContentDialogBorderBrush"] = border;
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = border;
        dialog.Resources["TextControlBackground"] = surface;
        dialog.Resources["TextControlBackgroundPointerOver"] = hover;
        dialog.Resources["TextControlBackgroundFocused"] = hover;
        dialog.Resources["TextControlForeground"] = foreground;
        dialog.Resources["TextControlForegroundPointerOver"] = foreground;
        dialog.Resources["TextControlForegroundFocused"] = foreground;
        dialog.Resources["TextControlPlaceholderForeground"] = placeholder;
        dialog.Resources["TextControlPlaceholderForegroundPointerOver"] = placeholder;
        dialog.Resources["TextControlPlaceholderForegroundFocused"] = placeholder;
        dialog.Resources["TextControlHeaderForeground"] = foreground;
        dialog.Resources["TextControlButtonForeground"] = placeholder;
        dialog.Resources["TextControlButtonForegroundPointerOver"] = foreground;
        dialog.Resources["TextControlButtonForegroundPressed"] = focus;
        dialog.Resources["TextControlBorderBrush"] = inputBorder;
        dialog.Resources["TextControlBorderBrushPointerOver"] = focus;
        dialog.Resources["TextControlBorderBrushFocused"] = focus;
        dialog.Resources["TextControlSelectionHighlightColor"] = focus;
        dialog.Resources["ListViewItemBackground"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        dialog.Resources["ListViewItemBackgroundPointerOver"] = hover;
        dialog.Resources["ListViewItemBackgroundPressed"] = selectedHover;
        dialog.Resources["ListViewItemBackgroundSelected"] = selected;
        dialog.Resources["ListViewItemBackgroundSelectedPointerOver"] = selectedHover;
        dialog.Resources["ListViewItemBackgroundSelectedPressed"] = selected;
        dialog.Resources["ListViewItemSelectionIndicatorBrush"] = indicator;
        dialog.Resources["ListViewItemSelectionIndicatorPointerOverBrush"] = indicator;
        dialog.Resources["ListViewItemSelectionIndicatorPressedBrush"] = indicator;
    }

    private static void ApplyRoundedDialogButtons(DependencyObject node, ContentDialog dialog)
    {
        var primaryStyle = Application.Current.Resources["RoundedPrimaryButtonStyle"] as Style;
        var secondaryStyle = Application.Current.Resources["RoundedDialogButtonStyle"] as Style;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is Button button)
            {
                var label = button.Content switch
                {
                    string text => text,
                    TextBlock textBlock => textBlock.Text,
                    _ => AutomationProperties.GetName(button)
                };
                var isPrimary = string.Equals(label, dialog.PrimaryButtonText, StringComparison.CurrentCulture);
                var isDialogAction = isPrimary
                    || string.Equals(label, dialog.SecondaryButtonText, StringComparison.CurrentCulture)
                    || string.Equals(label, dialog.CloseButtonText, StringComparison.CurrentCulture);
                if (isDialogAction)
                {
                    button.Style = isPrimary ? primaryStyle : secondaryStyle;
                    button.CornerRadius = new CornerRadius(9);
                }
            }
            ApplyRoundedDialogButtons(child, dialog);
        }
    }
}
