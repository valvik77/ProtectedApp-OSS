using Xunit;

namespace ProtectedApp.Vault.Tests;

/// <summary>
/// The close warning lets the user pick how long to extend this one session.
/// These cover which durations it offers; the clamping of a requested value is
/// covered by the Guardian suite, which owns the enforcer.
/// </summary>
public sealed class CloseWarningExtensionTests
{
    private const int PolicyLimitMinutes = 10_080;

    [Fact]
    public void OffersTheConfiguredIntervalAlongsideTheCommonDurations()
    {
        Assert.Equal([5, 15, 30, 45, 60], MainWindow.BuildExtensionChoices(45));
    }

    [Fact]
    public void DoesNotRepeatTheConfiguredIntervalWhenItAlreadyMatchesAChoice()
    {
        Assert.Equal([5, 15, 30, 60], MainWindow.BuildExtensionChoices(30));
    }

    [Fact]
    public void KeepsTheChoicesInAscendingOrderWhenTheIntervalIsTheLongest()
    {
        // The selector shows them in this order, so a configured interval longer
        // than every default must not end up in the middle of the list.
        Assert.Equal([5, 15, 30, 60, 240], MainWindow.BuildExtensionChoices(240));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void IgnoresAnIntervalThatIsNotConfigured(int configuredMinutes)
    {
        Assert.Equal([5, 15, 30, 60], MainWindow.BuildExtensionChoices(configuredMinutes));
    }

    [Fact]
    public void NeverOffersMoreThanThePolicyAllows()
    {
        Assert.Equal([5, 15, 30, 60], MainWindow.BuildExtensionChoices(PolicyLimitMinutes + 1));
        Assert.Contains(PolicyLimitMinutes, MainWindow.BuildExtensionChoices(PolicyLimitMinutes));
    }
}
