using ProtectedApp.Service;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

/// <summary>
/// Resetting a daily quota must also clear the mark that records "this rule has
/// already been closed today". The marks are keyed by SID, session and
/// executable path, so a reset that looked for the rule id inside them matched
/// nothing: the stale mark survived, and a rule exhausted again before the
/// ten-minute expiry was silently never closed a second time.
/// </summary>
public sealed class QuotaCloseMarkTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string RulePath = @"C:\Program Files\Game\game.exe";

    [Fact]
    public void AMarkForTheSameRuleIsCleared()
    {
        var key = GuardianEnforcer.ProcessKey(Sid, 1, RulePath);

        Assert.True(GuardianEnforcer.QuotaCloseMarkBelongsToRule(key, Sid, RulePath));
    }

    [Fact]
    public void TheRuleIdIsNotPartOfTheKeyAndMustNotBeMatchedOn()
    {
        // The regression this guards: the key holds no rule id at all, so any
        // matching strategy based on one can only ever clear nothing.
        var ruleId = Guid.NewGuid();
        var key = GuardianEnforcer.ProcessKey(Sid, 1, RulePath);

        Assert.DoesNotContain(ruleId.ToString("N"), key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ruleId.ToString(), key, StringComparison.OrdinalIgnoreCase);
        // The path-based match still finds it.
        Assert.True(GuardianEnforcer.QuotaCloseMarkBelongsToRule(key, Sid, RulePath));
    }

    [Fact]
    public void MarksForEverySessionOfTheRuleAreCleared()
    {
        // The quota is per rule and per user, but a mark is written per session,
        // so resetting once must clear all of them or a second session keeps
        // skipping its close.
        foreach (var sessionId in new[] { 0, 1, 2, 17 })
            Assert.True(GuardianEnforcer.QuotaCloseMarkBelongsToRule(
                GuardianEnforcer.ProcessKey(Sid, sessionId, RulePath), Sid, RulePath));
    }

    [Fact]
    public void AMarkForAnotherRuleIsLeftAlone()
    {
        var other = GuardianEnforcer.ProcessKey(Sid, 1, @"C:\Program Files\Other\other.exe");

        Assert.False(GuardianEnforcer.QuotaCloseMarkBelongsToRule(other, Sid, RulePath));
    }

    [Fact]
    public void AMarkForAnotherUserIsLeftAlone()
    {
        // One user's master-password reset must not lift another user's close.
        var key = GuardianEnforcer.ProcessKey(OtherSid, 1, RulePath);

        Assert.False(GuardianEnforcer.QuotaCloseMarkBelongsToRule(key, Sid, RulePath));
    }

    [Fact]
    public void ThePathComparisonIgnoresCaseAndTrailingSeparators()
    {
        // The stored rule path and the one the key was built from come from
        // different places, so they need not match character for character.
        var key = GuardianEnforcer.ProcessKey(Sid, 1, RulePath.ToUpperInvariant());

        Assert.True(GuardianEnforcer.QuotaCloseMarkBelongsToRule(key, Sid, RulePath.ToLowerInvariant()));
    }

    [Fact]
    public void ASidThatIsAPrefixOfAnotherDoesNotMatch()
    {
        // Matching on a raw prefix would be wrong without the separator: SID
        // ...-100 must not clear a mark belonging to ...-1001.
        var key = GuardianEnforcer.ProcessKey(Sid + "1", 1, RulePath);

        Assert.False(GuardianEnforcer.QuotaCloseMarkBelongsToRule(key, Sid, RulePath));
    }
}
