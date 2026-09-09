namespace ProtectedApp.Services;

public sealed class LocalAuthenticationThrottle
{
    private readonly Dictionary<string, FailureState> _failures = new(StringComparer.OrdinalIgnoreCase);

    public LocalThrottleResult Verify(string scope, bool passwordAccepted, string rejectionMessage)
    {
        var now = DateTimeOffset.UtcNow;
        if (_failures.TryGetValue(scope, out var current) && current.RetryAfterUtc > now)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((current.RetryAfterUtc - now).TotalSeconds));
            return new LocalThrottleResult(
                new UnlockAttemptResult(false, "Demasiados intentos. Espera antes de volver a intentarlo.",
                    seconds, current.Count),
                PasswordRejected: false, LockoutStarted: false, LockoutEnded: false);
        }

        var lockoutEnded = current?.LockoutActive == true;
        if (passwordAccepted)
        {
            _failures.Remove(scope);
            return new LocalThrottleResult(UnlockAttemptResult.Accepted, false, false, lockoutEnded);
        }

        var count = current is null || now - current.LastFailureUtc > TimeSpan.FromMinutes(15)
            ? 1
            : Math.Min(current.Count + 1, 20);
        var delay = count switch
        {
            <= 2 => 0,
            3 => 5,
            4 => 15,
            5 => 30,
            6 => 60,
            _ => 300
        };
        _failures[scope] = new FailureState(count, now,
            delay > 0 ? now.AddSeconds(delay) : now, delay > 0);
        var countText = count < 3 ? $" Intentos fallidos: {count} de 3." : $" Intentos fallidos: {count}.";
        return new LocalThrottleResult(
            new UnlockAttemptResult(false, rejectionMessage.TrimEnd() + countText, delay, count),
            PasswordRejected: true, LockoutStarted: delay > 0, LockoutEnded: lockoutEnded);
    }

    private sealed record FailureState(int Count, DateTimeOffset LastFailureUtc,
        DateTimeOffset RetryAfterUtc, bool LockoutActive);
}

public sealed record LocalThrottleResult(UnlockAttemptResult Attempt, bool PasswordRejected,
    bool LockoutStarted, bool LockoutEnded);
