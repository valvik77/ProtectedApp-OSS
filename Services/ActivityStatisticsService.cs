using ProtectedApp.Models;

namespace ProtectedApp.Services;

public sealed record ActivityStatistics(int Events, int Blocked, int FailedPasswords, int Alerts);

public static class ActivityStatisticsService
{
    public static ActivityStatistics Create(IEnumerable<ActivityEntry> entries, DateTimeOffset now)
    {
        var since = now.AddHours(-24);
        var recent = entries.Where(entry => entry.Timestamp >= since && entry.Timestamp <= now).ToArray();
        return new ActivityStatistics(
            recent.Length,
            recent.Count(entry => entry.Kind == ActivityEventKind.Blocked),
            recent.Count(entry => entry.IsFailedPassword),
            recent.Count(entry => entry.Kind is ActivityEventKind.Warning or ActivityEventKind.Error));
    }
}
