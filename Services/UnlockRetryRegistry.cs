using System.Collections.Concurrent;
using ProtectedApp.Models;

namespace ProtectedApp.Services;

/// <summary>
/// Keeps the visible password retry delay while an unlock window is closed and
/// reopened in the same ProtectedApp process. Guardian remains the authority
/// for the actual lockout; this registry prevents the UI from appearing to
/// reset it between windows.
/// </summary>
internal static class UnlockRetryRegistry
{
    private static readonly ConcurrentDictionary<string, RetryState> Entries =
        new(StringComparer.Ordinal);

    internal static RetryState? GetActive(string scope, DateTimeOffset now)
    {
        if (!Entries.TryGetValue(scope, out var state)) return null;
        if (state.RetryUntilUtc > now) return state;
        Entries.TryRemove(new KeyValuePair<string, RetryState>(scope, state));
        return null;
    }

    internal static void Record(string scope, UnlockAttemptResult result, DateTimeOffset now)
    {
        if (result.Success)
        {
            Entries.TryRemove(scope, out _);
            return;
        }
        if (result.RetryAfterSeconds <= 0) return;

        var message = string.IsNullOrWhiteSpace(result.Error)
            ? "Demasiados intentos."
            : result.Error.Trim();
        var proposed = new RetryState(now.AddSeconds(result.RetryAfterSeconds), message);
        Entries.AddOrUpdate(scope, proposed, (_, existing) =>
            existing.RetryUntilUtc >= proposed.RetryUntilUtc ? existing : proposed);
    }

    internal sealed record RetryState(DateTimeOffset RetryUntilUtc, string Message);
}
