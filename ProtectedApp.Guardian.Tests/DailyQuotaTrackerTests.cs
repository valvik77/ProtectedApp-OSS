using ProtectedApp.Service;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public sealed class DailyQuotaTrackerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "ProtectedApp.Quota.Tests", Guid.NewGuid().ToString("N"), "usage.json");
    private DateTimeOffset _now = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2));
    private readonly Guid _rule = Guid.NewGuid();
    private const string Sid = "S-1-5-21-test";

    private DailyQuotaTracker Create() => new(() => _now, _path);

    [Fact]
    public void AccumulatesUseAndReportsExhaustionAtTheQuota()
    {
        var tracker = Create();

        Assert.False(tracker.IsExhausted(Sid, _rule, 60));
        tracker.Accumulate(Sid, _rule, TimeSpan.FromMinutes(1));
        _now = _now.AddMinutes(1);
        Assert.Equal(1, tracker.GetUsedMinutes(Sid, _rule), 3);
        Assert.False(tracker.IsExhausted(Sid, _rule, 2));

        tracker.Accumulate(Sid, _rule, TimeSpan.FromMinutes(1));
        Assert.True(tracker.IsExhausted(Sid, _rule, 2));
    }

    [Fact]
    public void AQuotaOfZeroIsNeverExhausted()
    {
        var tracker = Create();
        tracker.Accumulate(Sid, _rule, TimeSpan.FromMinutes(2));

        Assert.False(tracker.IsExhausted(Sid, _rule, 0));
    }

    [Fact]
    public void UsageResetsOnTheNextLocalDay()
    {
        var tracker = Create();
        tracker.Accumulate(Sid, _rule, TimeSpan.FromMinutes(2));
        Assert.True(tracker.IsExhausted(Sid, _rule, 2));

        _now = _now.AddDays(1);

        Assert.Equal(0, tracker.GetUsedMinutes(Sid, _rule), 3);
        Assert.False(tracker.IsExhausted(Sid, _rule, 2));
    }

    [Fact]
    public void UsageSurvivesARestartWithinTheSameDay()
    {
        Create().Accumulate(Sid, _rule, TimeSpan.FromMinutes(2));

        // A service restart must not hand the user a fresh quota.
        Assert.True(Create().IsExhausted(Sid, _rule, 2));
    }

    [Fact]
    public void AnImplausibleIntervalCannotBurnTheWholeQuota()
    {
        var tracker = Create();

        // A suspended machine or a clock jump can report hours between passes;
        // crediting them would close the application the moment it resumes.
        tracker.Accumulate(Sid, _rule, TimeSpan.FromHours(5));

        Assert.True(tracker.GetUsedMinutes(Sid, _rule) <= 2);
    }

    [Fact]
    public void ResetClearsTodaysUsage()
    {
        var tracker = Create();
        tracker.Accumulate(Sid, _rule, TimeSpan.FromMinutes(2));
        Assert.True(tracker.IsExhausted(Sid, _rule, 2));

        tracker.Reset(Sid, _rule);

        Assert.Equal(0, tracker.GetUsedMinutes(Sid, _rule), 3);
    }

    [Fact]
    public void UsageIsTrackedPerRuleAndPerUser()
    {
        var tracker = Create();
        var other = Guid.NewGuid();
        tracker.Accumulate(Sid, _rule, TimeSpan.FromMinutes(2));

        Assert.Equal(0, tracker.GetUsedMinutes(Sid, other), 3);
        Assert.Equal(0, tracker.GetUsedMinutes("S-1-5-21-other", _rule), 3);
    }

    [Fact]
    public void NextResetIsTheFollowingLocalMidnight()
    {
        var reset = Create().NextResetLocal();

        Assert.Equal(new DateTime(2026, 10, 2), reset.Date);
        Assert.Equal(TimeSpan.Zero, reset.TimeOfDay);
    }

    [Fact]
    public void AnUnreadableUsageFileDoesNotBlockEnforcement()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        // A quota is a convenience; a damaged file must never stop Guardian.
        var tracker = Create();

        Assert.Equal(0, tracker.GetUsedMinutes(Sid, _rule), 3);
        Assert.False(tracker.IsExhausted(Sid, _rule, 60));
    }

    public void Dispose()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
    }
}

/// <summary>
/// A rule may choose never to force an application closed, so an unanswered
/// save dialog cannot cost the user their work.
/// </summary>
public sealed class UnresponsiveCloseTests
{
    private static readonly DateTimeOffset Requested = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothModesWaitOutTheGracePeriod(bool forceWhenUnresponsive)
    {
        Assert.Equal(GuardianEnforcer.UnresponsiveCloseAction.KeepWaiting,
            GuardianEnforcer.DecideUnresponsiveClose(forceWhenUnresponsive, Requested,
                Requested.AddSeconds(29)));
    }

    [Fact]
    public void ForcingTerminatesOnceTheGracePeriodExpires()
    {
        Assert.Equal(GuardianEnforcer.UnresponsiveCloseAction.Terminate,
            GuardianEnforcer.DecideUnresponsiveClose(true, Requested, Requested.AddSeconds(30)));
    }

    [Fact]
    public void NotForcingNeverTerminates()
    {
        // However long the application holds its dialog, the process survives.
        foreach (var minutes in new[] { 1, 5, 60, 60 * 24 })
            Assert.NotEqual(GuardianEnforcer.UnresponsiveCloseAction.Terminate,
                GuardianEnforcer.DecideUnresponsiveClose(false, Requested, Requested.AddMinutes(minutes)));
    }

    [Fact]
    public void NotForcingRepeatsTheRequestOnTheRetryInterval()
    {
        Assert.Equal(GuardianEnforcer.UnresponsiveCloseAction.KeepWaiting,
            GuardianEnforcer.DecideUnresponsiveClose(false, Requested, Requested.AddMinutes(4)));
        Assert.Equal(GuardianEnforcer.UnresponsiveCloseAction.RequestAgain,
            GuardianEnforcer.DecideUnresponsiveClose(false, Requested, Requested.AddMinutes(5)));
    }

    [Fact]
    public void AClockGoingBackwardsDoesNotTerminateAnything()
    {
        Assert.Equal(GuardianEnforcer.UnresponsiveCloseAction.KeepWaiting,
            GuardianEnforcer.DecideUnresponsiveClose(true, Requested, Requested.AddMinutes(-5)));
    }
}
