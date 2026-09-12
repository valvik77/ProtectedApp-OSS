namespace ProtectedApp.Models;

using ProtectedApp.Services;

public enum DiagnosticSeverity
{
    Success,
    Warning,
    Error
}

public sealed class DiagnosticResult : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _detail = string.Empty;
    public DiagnosticResult()
    {
    }

    public DiagnosticResult(string name, string detail, DiagnosticSeverity severity, bool canRepair = false)
    {
        Name = name;
        Detail = detail;
        Severity = severity;
        CanRepair = canRepair;
    }

    public string Name
    {
        get => LocalizationService.T(_name);
        set => _name = value;
    }

    public string Detail
    {
        get => LocalizationService.T(_detail);
        set => _detail = value;
    }
    public DiagnosticSeverity Severity { get; set; }
    public bool CanRepair { get; set; }

    public string StatusLabel => LocalizationService.T(Severity switch
    {
        DiagnosticSeverity.Success => "Correcto",
        DiagnosticSeverity.Warning => "Revisar",
        _ => "Error"
    });

    public string Glyph => Severity switch
    {
        DiagnosticSeverity.Success => "\ue668",
        DiagnosticSeverity.Warning => "\uf083",
        _ => "\ue88e"
    };

    public void RefreshLocalizedText()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Detail)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusLabel)));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
