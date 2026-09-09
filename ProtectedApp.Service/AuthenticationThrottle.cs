using System.Text.Json;

namespace ProtectedApp.Service;

internal sealed class AuthenticationThrottle
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;
    private ThrottleDatabase _database;

    public AuthenticationThrottle() : this(() => DateTimeOffset.UtcNow) { }

    internal AuthenticationThrottle(Func<DateTimeOffset> clock)
    {
        _clock = clock;
        _database = Load();
    }

    public ThrottleDecision Check(string key)
    {
        lock (_sync)
        {
            var now = _clock();
            Cleanup(now);
            if (!_database.Entries.TryGetValue(key, out var state)) return ThrottleDecision.Allowed;
            if (state.RetryAfterUtc > now)
                return new ThrottleDecision(true, SecondsRemaining(state.RetryAfterUtc, now),
                    state.FailureCount, LockoutStarted: false, LockoutEnded: false);
            if (!state.LockoutActive) return new ThrottleDecision(false, 0, state.FailureCount, false, false);

            state.LockoutActive = false;
            SaveLocked();
            return new ThrottleDecision(false, 0, state.FailureCount, false, LockoutEnded: true);
        }
    }

    public ThrottleDecision RegisterFailure(string key)
    {
        lock (_sync)
        {
            var now = _clock();
            Cleanup(now);
            if (!_database.Entries.TryGetValue(key, out var state)
                || now - state.LastFailureUtc > TimeSpan.FromMinutes(15))
            {
                state = new ThrottleEntry();
                _database.Entries[key] = state;
            }

            state.FailureCount = Math.Min(state.FailureCount + 1, 20);
            state.LastFailureUtc = now;
            var delay = DelayForFailure(state.FailureCount);
            state.RetryAfterUtc = delay > 0 ? now.AddSeconds(delay) : now;
            state.LockoutActive = delay > 0;
            SaveLocked();
            return new ThrottleDecision(delay > 0, delay, state.FailureCount,
                LockoutStarted: delay > 0, LockoutEnded: false);
        }
    }

    public void Clear(string key)
    {
        lock (_sync)
        {
            if (!_database.Entries.Remove(key)) return;
            SaveLocked();
        }
    }

    private void Cleanup(DateTimeOffset now)
    {
        var expired = _database.Entries
            .Where(pair => now - pair.Value.LastFailureUtc > TimeSpan.FromHours(24))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired) _database.Entries.Remove(key);
    }

    private static int DelayForFailure(int failureCount) => failureCount switch
    {
        <= 2 => 0,
        3 => 5,
        4 => 15,
        5 => 30,
        6 => 60,
        _ => 300
    };

    private static int SecondsRemaining(DateTimeOffset retryAfterUtc, DateTimeOffset now) =>
        Math.Max(1, (int)Math.Ceiling((retryAfterUtc - now).TotalSeconds));

    private ThrottleDatabase Load()
    {
        try
        {
            if (!File.Exists(GuardianConstants.ThrottlePath)) return new ThrottleDatabase();
            return JsonSerializer.Deserialize<ThrottleDatabase>(
                File.ReadAllText(GuardianConstants.ThrottlePath), JsonOptions) ?? new ThrottleDatabase();
        }
        // A transient lock or access failure must preserve throttling state.
        // Starting with an empty database would let a restart erase an active
        // progressive lockout.
        catch (JsonException) { return new ThrottleDatabase(); }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GuardianConstants.ThrottlePath)!);
        var temp = GuardianConstants.ThrottlePath + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(temp, JsonSerializer.Serialize(_database, JsonOptions));
        File.Move(temp, GuardianConstants.ThrottlePath, true);
    }

    private sealed class ThrottleDatabase
    {
        public Dictionary<string, ThrottleEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ThrottleEntry
    {
        public int FailureCount { get; set; }
        public DateTimeOffset LastFailureUtc { get; set; }
        public DateTimeOffset RetryAfterUtc { get; set; }
        public bool LockoutActive { get; set; }
    }
}

internal sealed record ThrottleDecision(bool IsLimited, int RetryAfterSeconds, int FailureCount,
    bool LockoutStarted, bool LockoutEnded)
{
    public static ThrottleDecision Allowed { get; } = new(false, 0, 0, false, false);
}
