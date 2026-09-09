using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using ProtectedApp.Models;

namespace ProtectedApp.Converters;

public sealed class ActivityKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var resource = value is ActivityEventKind kind
            ? kind switch
            {
                ActivityEventKind.Access => "SuccessBrush",
                ActivityEventKind.Blocked or ActivityEventKind.Warning => "WarningBrush",
                ActivityEventKind.Error => "DangerBrush",
                _ => "AccentIndigoBrush"
            }
            : "AccentIndigoBrush";
        return Application.Current.Resources[resource] as Brush
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
