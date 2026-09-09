using Microsoft.UI.Xaml.Media.Imaging;

namespace ProtectedApp.Models;

public sealed class InstalledApplication
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public byte[]? IconPng { get; init; }
    public BitmapImage? IconSource { get; set; }
    public string Detail => string.IsNullOrWhiteSpace(Publisher) ? Path : $"{Publisher}  ·  {Path}";
}
