using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using ProtectedApp.Services;
using ProtectedApp.Shared;

namespace ProtectedApp.Models;

public sealed class ProtectedApplication : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _path = string.Empty;
    private bool _isEnabled = true;
    private string? _passwordHash;
    private string? _passwordSalt;
    private int _unlockGraceMinutes;
    private int _forceCloseAfterMinutes;
    private int _forceCloseAfterInactivityMinutes;
    private string _category = "General";
    private BitmapImage? _iconSource;
    private bool _scheduleEnabled;
    private int _scheduleDays = (int)ProtectedApp.Shared.ScheduleDays.EveryDay;
    private int _scheduleStartMinutes = 9 * 60;
    private int _scheduleEndMinutes = 17 * 60;
    private bool _blockOutsideSchedule;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Path { get => _path; set => SetField(ref _path, value); }
    public string Category { get => string.IsNullOrWhiteSpace(_category) ? "General" : _category; set { if (SetField(ref _category, value)) OnPropertyChanged(nameof(CategoryLabel)); } }
    public bool IsEnabled { get => _isEnabled; set => SetField(ref _isEnabled, value); }
    public string? PasswordHash { get => _passwordHash; set { SetField(ref _passwordHash, value); OnPropertyChanged(nameof(PasswordLabel)); OnPropertyChanged(nameof(AccessLabel)); } }
    public string? PasswordSalt { get => _passwordSalt; set => SetField(ref _passwordSalt, value); }
    public int UnlockGraceMinutes
    {
        get => _unlockGraceMinutes;
        set
        {
            if (!SetField(ref _unlockGraceMinutes, value)) return;
            OnPropertyChanged(nameof(AccessLabel));
        }
    }
    public int ForceCloseAfterMinutes
    {
        get => _forceCloseAfterMinutes;
        set
        {
            if (!SetField(ref _forceCloseAfterMinutes, value)) return;
            OnPropertyChanged(nameof(AccessLabel));
        }
    }
    public int ForceCloseAfterInactivityMinutes
    {
        get => _forceCloseAfterInactivityMinutes;
        set
        {
            if (!SetField(ref _forceCloseAfterInactivityMinutes, value)) return;
            OnPropertyChanged(nameof(AccessLabel));
        }
    }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public int BlockCount { get; set; }
    public bool ScheduleEnabled { get => _scheduleEnabled; set { if (SetField(ref _scheduleEnabled, value)) { OnPropertyChanged(nameof(AccessLabel)); OnPropertyChanged(nameof(ScheduleSummary)); } } }
    public int ScheduleDays { get => _scheduleDays; set { if (SetField(ref _scheduleDays, value)) { OnPropertyChanged(nameof(AccessLabel)); OnPropertyChanged(nameof(ScheduleSummary)); } } }
    public int ScheduleStartMinutes { get => _scheduleStartMinutes; set { if (SetField(ref _scheduleStartMinutes, value)) { OnPropertyChanged(nameof(AccessLabel)); OnPropertyChanged(nameof(ScheduleSummary)); } } }
    public int ScheduleEndMinutes { get => _scheduleEndMinutes; set { if (SetField(ref _scheduleEndMinutes, value)) { OnPropertyChanged(nameof(AccessLabel)); OnPropertyChanged(nameof(ScheduleSummary)); } } }
    public bool BlockOutsideSchedule { get => _blockOutsideSchedule; set { if (SetField(ref _blockOutsideSchedule, value)) { OnPropertyChanged(nameof(AccessLabel)); OnPropertyChanged(nameof(ScheduleSummary)); } } }

    [JsonIgnore]
    public BitmapImage? IconSource { get => _iconSource; set => SetField(ref _iconSource, value); }

    [JsonIgnore]
    public string CategoryLabel => LocalizationService.T(string.IsNullOrWhiteSpace(Category) ? "General" : Category);

    [JsonIgnore]
    public string PasswordLabel => LocalizationService.T(string.IsNullOrWhiteSpace(PasswordHash) ? "Usa la contraseña maestra" : "Contraseña propia");

    [JsonIgnore]
    public string ScheduleSummary
    {
        get
        {
            if (!ScheduleEnabled) return string.Empty;
            var startH = ScheduleStartMinutes / 60;
            var startM = ScheduleStartMinutes % 60;
            var endH = ScheduleEndMinutes / 60;
            var endM = ScheduleEndMinutes % 60;
            var timeStr = $"{startH:D2}:{startM:D2} - {endH:D2}:{endM:D2}";
            var daysStr = FormatDays(ScheduleDays);
            var modeStr = BlockOutsideSchedule
                ? LocalizationService.IsEnglish ? " (locked outside schedule)" : " (bloqueo fuera de horario)"
                : "";
            return $"{daysStr} {timeStr}{modeStr}";
        }
    }

    private static string FormatDays(int days)
    {
        var mask = (ScheduleDays)(days & (int)ProtectedApp.Shared.ScheduleDays.EveryDay);
        if (mask == ProtectedApp.Shared.ScheduleDays.EveryDay) return LocalizationService.T("Todos los días");
        if (mask == (ProtectedApp.Shared.ScheduleDays.Monday | ProtectedApp.Shared.ScheduleDays.Tuesday |
                     ProtectedApp.Shared.ScheduleDays.Wednesday | ProtectedApp.Shared.ScheduleDays.Thursday |
                     ProtectedApp.Shared.ScheduleDays.Friday)) return LocalizationService.T("Lun-Vie");
        if (mask == (ProtectedApp.Shared.ScheduleDays.Saturday | ProtectedApp.Shared.ScheduleDays.Sunday)) return LocalizationService.T("Fin de semana");
        var list = new List<string>();
        if (mask.HasFlag(ProtectedApp.Shared.ScheduleDays.Monday)) list.Add("L");
        if (mask.HasFlag(ProtectedApp.Shared.ScheduleDays.Tuesday)) list.Add("M");
        if (mask.HasFlag(ProtectedApp.Shared.ScheduleDays.Wednesday)) list.Add("X");
        if (mask.HasFlag(ProtectedApp.Shared.ScheduleDays.Thursday)) list.Add("J");
        if (mask.HasFlag(ProtectedApp.Shared.ScheduleDays.Friday)) list.Add("V");
        if (mask.HasFlag(ProtectedApp.Shared.ScheduleDays.Saturday)) list.Add("S");
        if (mask.HasFlag(ProtectedApp.Shared.ScheduleDays.Sunday)) list.Add("D");
        return list.Count > 0 ? string.Join(",", list) : LocalizationService.T("Ninguno");
    }

    [JsonIgnore]
    public string AccessLabel
    {
        get
        {
            var trust = UnlockGraceMinutes > 0
                ? LocalizationService.IsEnglish ? $"grace {FormatMinutes(UnlockGraceMinutes)}" : $"confianza {FormatMinutes(UnlockGraceMinutes)}"
                : LocalizationService.T("hasta cerrar");
            var forcedClose = ForceCloseAfterMinutes > 0
                ? LocalizationService.IsEnglish ? $" · close {FormatMinutes(ForceCloseAfterMinutes)}" : $" · cierre {FormatMinutes(ForceCloseAfterMinutes)}"
                : ForceCloseAfterInactivityMinutes > 0
                    ? LocalizationService.IsEnglish ? $" · inactivity {FormatMinutes(ForceCloseAfterInactivityMinutes)}" : $" · inactividad {FormatMinutes(ForceCloseAfterInactivityMinutes)}"
                    : string.Empty;
            var schedule = ScheduleEnabled
                ? BlockOutsideSchedule
                    ? LocalizationService.IsEnglish ? " · scheduled lock" : " · horario con bloqueo"
                    : LocalizationService.IsEnglish ? " · schedule" : " · horario"
                : string.Empty;
            return $"{PasswordLabel} · {trust}{forcedClose}{schedule}";
        }
    }

    private static string FormatMinutes(int minutes) => minutes == 60
        ? "1 h"
        : minutes % 60 == 0 && minutes >= 120
            ? $"{minutes / 60} h"
            : $"{minutes} min";

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(CategoryLabel));
        OnPropertyChanged(nameof(PasswordLabel));
        OnPropertyChanged(nameof(AccessLabel));
        OnPropertyChanged(nameof(ScheduleSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
