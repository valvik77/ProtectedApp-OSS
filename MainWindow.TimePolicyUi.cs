using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Shared;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private sealed record TimePolicyEditor(StackPanel Panel, ComboBox TrustBox, TextBox TrustCustom,
        TextBlock TrustMaximumText, ComboBox CloseMode, ComboBox CloseBox, TextBox CloseCustom, TextBlock CloseMaximumText);
    private sealed record ScheduleEditor(StackPanel Panel, ComboBox Mode, ToggleButton[] DayButtons,
        TimePicker Start, TimePicker End);

    private static TimePolicyEditor CreateTimePolicyEditor(int unlockMinutes, int forceCloseMinutes,
        int forceCloseAfterInactivityMinutes)
    {
        var trustBox = CreateMinutePresetBox("Reaperturas sin contraseña", "Solo mientras continúe abierta", unlockMinutes, out var trustCustom, out var trustCustomHost, out var trustMaximumText);
        var usesInactivity = forceCloseAfterInactivityMinutes > 0;
        var closeMinutes = usesInactivity ? forceCloseAfterInactivityMinutes : forceCloseMinutes;
        var closeMode = new ComboBox { Header = "Cierre automático", HorizontalAlignment = HorizontalAlignment.Stretch };
        closeMode.Items.Add(new ComboBoxItem { Content = "Desactivado", Tag = "off" });
        closeMode.Items.Add(new ComboBoxItem { Content = "Tras tiempo de uso", Tag = "elapsed" });
        closeMode.Items.Add(new ComboBoxItem { Content = "Tras inactividad", Tag = "inactive" });
        closeMode.SelectedIndex = closeMinutes <= 0 ? 0 : usesInactivity ? 2 : 1;
        var closeBox = CreateMinutePresetBox("Tiempo", "Selecciona un modo", closeMinutes, out var closeCustom, out var closeCustomHost, out var closeMaximumText);
        var description = new TextBlock { Text = "El cierre por inactividad cuenta desde la última interacción de teclado o ratón mientras la aplicación está en primer plano. Solo puede usarse un modo de cierre a la vez.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Application.Current.Resources["MutedTextBrush"] as Brush };
        var columns = new Grid { ColumnSpacing = 10 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var trustColumn = new StackPanel { Spacing = 6 }; trustColumn.Children.Add(trustBox); trustColumn.Children.Add(trustCustomHost);
        var closeColumn = new StackPanel { Spacing = 6 }; closeColumn.Children.Add(closeMode); closeColumn.Children.Add(closeBox); closeColumn.Children.Add(closeCustomHost);
        columns.Children.Add(trustColumn); Grid.SetColumn(closeColumn, 1); columns.Children.Add(closeColumn);
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(columns); panel.Children.Add(description);
        var editor = new TimePolicyEditor(panel, trustBox, trustCustom, trustMaximumText, closeMode, closeBox, closeCustom, closeMaximumText);
        closeMode.SelectionChanged += (_, _) => UpdateTimePolicyConstraints(editor); closeBox.SelectionChanged += (_, _) => UpdateTimePolicyConstraints(editor); closeCustom.TextChanged += (_, _) => UpdateTimePolicyConstraints(editor); UpdateTimePolicyConstraints(editor);
        return editor;
    }

    private static ScheduleEditor CreateScheduleEditor(bool enabled, int days, int startMinutes,
        int endMinutes, bool blockOutside)
    {
        var mode = new ComboBox { Header = "Horario", HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add(new ComboBoxItem { Content = "Protección permanente", Tag = "always" });
        mode.Items.Add(new ComboBoxItem { Content = "Proteger solo durante el horario", Tag = "protect" });
        mode.Items.Add(new ComboBoxItem { Content = "Proteger durante el horario y bloquear fuera", Tag = "block" });
        mode.SelectedIndex = !enabled ? 0 : blockOutside ? 2 : 1;

        var dayButtons = new[]
        {
            CreateScheduleDayButton("L", "Lunes", ScheduleDays.Monday, days),
            CreateScheduleDayButton("M", "Martes", ScheduleDays.Tuesday, days),
            CreateScheduleDayButton("X", "Miércoles", ScheduleDays.Wednesday, days),
            CreateScheduleDayButton("J", "Jueves", ScheduleDays.Thursday, days),
            CreateScheduleDayButton("V", "Viernes", ScheduleDays.Friday, days),
            CreateScheduleDayButton("S", "Sábado", ScheduleDays.Saturday, days),
            CreateScheduleDayButton("D", "Domingo", ScheduleDays.Sunday, days)
        };
        var daysPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (var button in dayButtons) daysPanel.Children.Add(button);

        var start = new TimePicker { Header = "Desde", ClockIdentifier = "24HourClock", Time = TimeSpan.FromMinutes(Math.Clamp(startMinutes, 0, 1_439)), MinuteIncrement = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        var end = new TimePicker { Header = "Hasta", ClockIdentifier = "24HourClock", Time = TimeSpan.FromMinutes(Math.Clamp(endMinutes, 0, 1_439)), MinuteIncrement = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        var timeGrid = new Grid { ColumnSpacing = 10 };
        timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeGrid.Children.Add(start); Grid.SetColumn(end, 1); timeGrid.Children.Add(end);

        var details = new StackPanel { Spacing = 7 };
        details.Children.Add(new TextBlock { Text = "Días activos", FontSize = 11, Foreground = Application.Current.Resources["MutedTextBrush"] as Brush });
        details.Children.Add(daysPanel); details.Children.Add(timeGrid);
        details.Children.Add(new TextBlock { Text = "Si ambas horas coinciden, el horario cubre las 24 horas de los días elegidos. Los intervalos nocturnos pueden terminar al día siguiente.", FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = Application.Current.Resources["MutedTextBrush"] as Brush });
        var panel = new StackPanel { Spacing = 7 }; panel.Children.Add(mode); panel.Children.Add(details);
        mode.SelectionChanged += (_, _) => details.Visibility = mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        details.Visibility = mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        return new ScheduleEditor(panel, mode, dayButtons, start, end);
    }

    private static ToggleButton CreateScheduleDayButton(string text, string accessibleName,
        ScheduleDays day, int selectedDays)
    {
        var button = new ToggleButton { Content = text, Tag = (int)day, IsChecked = (selectedDays & (int)day) != 0, Width = 38, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(7) };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private static bool TryReadSchedule(ScheduleEditor editor, out bool enabled, out int days,
        out int startMinutes, out int endMinutes, out bool blockOutside, out string error)
    {
        enabled = editor.Mode.SelectedIndex > 0;
        blockOutside = editor.Mode.SelectedIndex == 2;
        days = editor.DayButtons.Where(button => button.IsChecked == true).Sum(button => (int)button.Tag);
        startMinutes = Math.Clamp((int)editor.Start.Time.TotalMinutes, 0, 1_439);
        endMinutes = Math.Clamp((int)editor.End.Time.TotalMinutes, 0, 1_439);
        if (enabled && days == 0) { error = "Selecciona al menos un día para el horario."; return false; }
        error = string.Empty;
        return true;
    }

    private ScrollViewer CreateDialogScroller(UIElement content)
    {
        var height = Root.XamlRoot?.Size.Height ?? 720;
        return new ScrollViewer
        {
            Content = content, MaxHeight = Math.Clamp(height - 220, 120, 620),
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static ComboBox CreateMinutePresetBox(string header, string disabledLabel, int selectedMinutes,
        out TextBox customValue, out Grid customHost, out TextBlock maximumText)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Items.Add(new ComboBoxItem { Content = disabledLabel, Tag = "0" });
        box.Items.Add(new ComboBoxItem { Content = "5 minutos", Tag = "5" });
        box.Items.Add(new ComboBoxItem { Content = "15 minutos", Tag = "15" });
        box.Items.Add(new ComboBoxItem { Content = "1 hora", Tag = "60" });
        box.Items.Add(new ComboBoxItem { Content = "Tiempo personalizado…", Tag = "custom" });
        var presetIndex = selectedMinutes switch { 5 => 1, 15 => 2, 60 => 3, > 0 => 4, _ => 0 };
        box.SelectedIndex = presetIndex;
        customValue = new TextBox { Header = "Minutos personalizados", Text = (selectedMinutes > 0 && presetIndex == 4 ? selectedMinutes : 30).ToString(), MaxLength = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        var custom = customValue;
        custom.Tag = 10_080;
        var upButton = CreateMinuteStepButton("\uE70E", "Aumentar un minuto");
        var downButton = CreateMinuteStepButton("\uE70D", "Reducir un minuto");
        upButton.Click += (_, _) => AdjustCustomMinutes(custom, 1);
        downButton.Click += (_, _) => AdjustCustomMinutes(custom, -1);
        var stepButtons = new StackPanel { Spacing = 2, Width = 24, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
        stepButtons.Children.Add(upButton); stepButtons.Children.Add(downButton);
        maximumText = new TextBlock { Text = "Máximo: 10.080 min", FontSize = 9, Margin = new Thickness(1, 2, 0, 0), Foreground = Application.Current.Resources["MutedTextBrush"] as Brush };
        customHost = new Grid { ColumnSpacing = 5, Visibility = presetIndex == 4 ? Visibility.Visible : Visibility.Collapsed };
        customHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customHost.Children.Add(custom);
        Grid.SetColumn(stepButtons, 1); customHost.Children.Add(stepButtons);
        Grid.SetRow(maximumText, 1); Grid.SetColumnSpan(maximumText, 2); customHost.Children.Add(maximumText);
        var host = customHost;
        box.SelectionChanged += (_, _) => host.Visibility = IsCustomTime(box) ? Visibility.Visible : Visibility.Collapsed;
        return box;
    }

    private static Button CreateMinuteStepButton(string glyph, string accessibleName)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 7 }, Width = 24, Height = 15, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private static void AdjustCustomMinutes(TextBox input, int delta)
    {
        var maximum = input.Tag is int configuredMaximum ? configuredMaximum : 10_080;
        var current = int.TryParse(input.Text.Trim(), out var parsed) ? parsed : 1;
        input.Text = Math.Clamp(current + delta, 1, maximum).ToString();
        input.Select(input.Text.Length, 0);
    }

    private static bool TryReadTimePolicy(TimePolicyEditor editor, out int unlockMinutes,
        out int forceCloseMinutes, out int forceCloseAfterInactivityMinutes, out string error)
    {
        if (!TryReadMinutes(editor.TrustBox, editor.TrustCustom, out unlockMinutes))
        {
            forceCloseMinutes = forceCloseAfterInactivityMinutes = 0;
            error = "Indica un número entero de minutos para el periodo de confianza (entre 1 y 10.080).";
            return false;
        }
        forceCloseMinutes = 0;
        forceCloseAfterInactivityMinutes = 0;
        var mode = (editor.CloseMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var closeMinutes = 0;
        if (mode != "off" && !TryReadMinutes(editor.CloseBox, editor.CloseCustom, out closeMinutes))
        {
            error = "Indica un número entero de minutos para el cierre automático (entre 1 y 10.080).";
            return false;
        }
        else if (mode == "elapsed") forceCloseMinutes = closeMinutes;
        else if (mode == "inactive") forceCloseAfterInactivityMinutes = closeMinutes;
        var closeLimit = Math.Max(forceCloseMinutes, forceCloseAfterInactivityMinutes);
        if (closeLimit > 0 && unlockMinutes > closeLimit)
        {
            error = "El periodo de confianza no puede superar el tiempo de cierre automático.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static void UpdateTimePolicyConstraints(TimePolicyEditor editor)
    {
        var mode = (editor.CloseMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var closeMinutes = 0;
        var hasCloseLimit = mode != "off" && TryReadMinutes(editor.CloseBox, editor.CloseCustom, out closeMinutes) && closeMinutes > 0;
        editor.CloseBox.IsEnabled = mode != "off";
        editor.CloseCustom.IsEnabled = mode != "off";
        var maximum = hasCloseLimit ? closeMinutes : 10_080;
        editor.TrustCustom.Tag = maximum;
        editor.TrustCustom.Header = LocalizationService.T("Minutos personalizados");
        editor.TrustMaximumText.Text = LocalizationService.IsEnglish
            ? $"Maximum: {maximum:N0} min"
            : $"Máximo: {maximum:N0} min";
        foreach (var item in editor.TrustBox.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag?.ToString();
            item.IsEnabled = !int.TryParse(tag, out var preset) || preset == 0 || !hasCloseLimit || preset <= closeMinutes;
        }
        if (hasCloseLimit && TryReadMinutes(editor.TrustBox, editor.TrustCustom, out var trustMinutes) && trustMinutes > closeMinutes)
            editor.TrustBox.SelectedIndex = 0;
    }

    private static bool TryReadMinutes(ComboBox box, TextBox custom, out int minutes)
    {
        var tag = (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (tag != "custom") return int.TryParse(tag, out minutes);
        if (!int.TryParse(custom.Text.Trim(), out minutes) || minutes < 1 || minutes > 10_080)
        {
            minutes = 0;
            return false;
        }
        return true;
    }

    private static bool IsCustomTime(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "custom";
}
