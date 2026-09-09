using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProtectedApp.Services;

public static class TruncatedTextToolTip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TruncatedTextToolTip), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBlock textBlock || args.NewValue is not true) return;

        void UpdateToolTip() => ToolTipService.SetToolTip(textBlock, textBlock.Text);
        UpdateToolTip();
        textBlock.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateToolTip());
    }
}
