namespace ProtectedApp.Models;

using ProtectedApp.Services;

public enum ActivityEventKind
{
    System,
    Blocked,
    Access,
    Warning,
    Error
}

public sealed class ActivityEntry : System.ComponentModel.INotifyPropertyChanged
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string AppName { get; set; } = string.Empty;
    // Persist the source message separately.  Message is only the current
    // display rendering and must never become the source for another locale.
    public string CanonicalMessage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ActivityEventKind Kind { get; set; } = ActivityEventKind.System;
    public bool IsFailedPassword
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(CanonicalMessage) ? Message : CanonicalMessage;
            return (source.Contains("contraseña", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("password", StringComparison.OrdinalIgnoreCase))
                && (source.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("fallid", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("failed", StringComparison.OrdinalIgnoreCase));
        }
    }
    public string TimeLabel => Timestamp.ToLocalTime().Date == DateTimeOffset.Now.Date
        ? Timestamp.ToLocalTime().ToString("HH:mm:ss")
        : Timestamp.ToLocalTime().ToString("dd/MM HH:mm");
    public string KindLabel => LocalizationService.T(Kind switch
    {
        ActivityEventKind.Blocked => "Bloqueo",
        ActivityEventKind.Access => "Acceso",
        ActivityEventKind.Warning => "Aviso",
        ActivityEventKind.Error => "Error",
        _ => "Sistema"
    });
    public string Glyph => Kind switch
    {
        ActivityEventKind.Blocked => "\uE72E",
        ActivityEventKind.Access => "\uE73E",
        ActivityEventKind.Warning => "\uE7BA",
        ActivityEventKind.Error => "\uEA39",
        _ => "\uE946"
    };

    public void RefreshLocalizedText()
    {
        if (string.IsNullOrWhiteSpace(CanonicalMessage))
            CanonicalMessage = LocalizationService.RecoverSource(Message);
        Message = LocalizationService.T(CanonicalMessage);
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(TimeLabel));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
