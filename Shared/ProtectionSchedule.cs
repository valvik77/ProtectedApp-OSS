namespace ProtectedApp.Shared;

[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
    EveryDay = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday
}

public enum ScheduleDisposition
{
    Protect,
    Allow,
    Block
}

public static class ProtectionSchedule
{
    public static ScheduleDisposition GetDisposition(bool scheduleEnabled, int days,
        int startMinutes, int endMinutes, bool blockOutsideSchedule, DateTimeOffset localNow)
    {
        if (!scheduleEnabled || IsWithinSchedule(days, startMinutes, endMinutes, localNow))
            return ScheduleDisposition.Protect;
        return blockOutsideSchedule ? ScheduleDisposition.Block : ScheduleDisposition.Allow;
    }

    public static bool IsWithinSchedule(int days, int startMinutes, int endMinutes,
        DateTimeOffset localNow)
    {
        var selectedDays = (ScheduleDays)(days & (int)ScheduleDays.EveryDay);
        if (selectedDays == ScheduleDays.None) return false;
        startMinutes = Math.Clamp(startMinutes, 0, 1_439);
        endMinutes = Math.Clamp(endMinutes, 0, 1_439);
        var currentMinutes = localNow.Hour * 60 + localNow.Minute;
        var today = ToMask(localNow.DayOfWeek);

        if (startMinutes == endMinutes) return selectedDays.HasFlag(today);
        if (startMinutes < endMinutes)
            return selectedDays.HasFlag(today)
                && currentMinutes >= startMinutes
                && currentMinutes < endMinutes;

        if (currentMinutes >= startMinutes) return selectedDays.HasFlag(today);
        var previousDay = ToMask(localNow.AddDays(-1).DayOfWeek);
        return currentMinutes < endMinutes && selectedDays.HasFlag(previousDay);
    }

    public static ScheduleDays ToMask(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ScheduleDays.Monday,
        DayOfWeek.Tuesday => ScheduleDays.Tuesday,
        DayOfWeek.Wednesday => ScheduleDays.Wednesday,
        DayOfWeek.Thursday => ScheduleDays.Thursday,
        DayOfWeek.Friday => ScheduleDays.Friday,
        DayOfWeek.Saturday => ScheduleDays.Saturday,
        _ => ScheduleDays.Sunday
    };
}
