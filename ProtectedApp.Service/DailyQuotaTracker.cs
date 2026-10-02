using System.Text.Json;

namespace ProtectedApp.Service;

/// <summary>
/// Accumulates how long each protected application has run today and reports
/// when its configured daily quota is exhausted.
/// </summary>
/// <remarks>
/// Usage is persisted under the policy folder, which only SYSTEM and
/// Administrators may write, so a standard user cannot reset their own quota by
/// editing the file. It is deliberately keyed by local date: a quota is a
/// human-facing "today", and the reset must happen at the user's midnight
/// rather than UTC's.
/// </remarks>
internal sealed class DailyQuotaTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _path;
    private readonly ILogger<DailyQuotaTracker>? _logger;
    // Accumulate() saves once per scan cycle for every running rule, so a
    // persistently unwritable policy folder would otherwise fill the log with
    // the same failure. Report each run of failures once, and again only after
    // a save has succeeded in between.
    private bool _saveFailureReported;
    private QuotaDatabase _database;

    public DailyQuotaTracker(ILogger<DailyQuotaTracker> logger)
        : this(() => DateTimeOffset.Now, GuardianConstants.DailyQuotaPath, logger) { }

    internal DailyQuotaTracker(Func<DateTimeOffset> clock, string path,
        ILogger<DailyQuotaTracker>? logger = null)
    {
        _clock = clock;
        _path = path;
        _logger = logger;
        _database = Load();
    }

    /// <summary>
    /// Adds <paramref name="elapsed"/> to today's total for <paramref name="ruleId"/>
    /// and returns the running total in minutes.
    /// </summary>
    public double Accumulate(string userSid, Guid ruleId, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return GetUsedMinutes(userSid, ruleId);
        lock (_sync)
        {
            var entry = GetOrCreateEntryLocked(userSid, ruleId, Today());
            // A clock jump or a suspended machine can report an implausible
            // interval. Never credit more than a scan cycle's worth at once.
            entry.UsedMinutes += Math.Min(elapsed.TotalMinutes, MaximumCreditedMinutes);
            Save();
            return entry.UsedMinutes;
        }
    }

    public double GetUsedMinutes(string userSid, Guid ruleId)
    {
        lock (_sync)
        {
            var key = Key(userSid, ruleId);
            return _database.Entries.TryGetValue(key, out var entry) && entry.Date == Today()
                ? entry.UsedMinutes
                : 0;
        }
    }

    /// <summary>True when <paramref name="quotaMinutes"/> is set and already consumed.</summary>
    public bool IsExhausted(string userSid, Guid ruleId, int quotaMinutes) =>
        quotaMinutes > 0 && GetUsedMinutes(userSid, ruleId) >= quotaMinutes;

    /// <summary>Clears today's usage, which the master password may always do.</summary>
    public void Reset(string userSid, Guid ruleId)
    {
        lock (_sync)
        {
            if (_database.Entries.Remove(Key(userSid, ruleId))) Save();
        }
    }

    /// <summary>Local midnight after which the quota is available again.</summary>
    public DateTimeOffset NextResetLocal()
    {
        var now = _clock();
        return new DateTimeOffset(now.Date.AddDays(1), now.Offset);
    }

    private const double MaximumCreditedMinutes = 2;

    private string Today() => _clock().ToString("yyyy-MM-dd");
    private static string Key(string userSid, Guid ruleId) => $"{userSid}|{ruleId:N}";

    private QuotaEntry GetOrCreateEntryLocked(string userSid, Guid ruleId, string today)
    {
        var key = Key(userSid, ruleId);
        if (_database.Entries.TryGetValue(key, out var entry))
        {
            if (entry.Date == today) return entry;
            // Yesterday's total is replaced rather than kept: the quota is
            // per-day and history is not a feature anyone asked for.
            entry.Date = today;
            entry.UsedMinutes = 0;
            return entry;
        }
        entry = new QuotaEntry { Date = today, UsedMinutes = 0 };
        _database.Entries[key] = entry;
        return entry;
    }

    private QuotaDatabase Load()
    {
        try
        {
            if (!File.Exists(_path)) return new QuotaDatabase();
            var database = JsonSerializer.Deserialize<QuotaDatabase>(File.ReadAllText(_path), JsonOptions);
            if (database?.Entries is null) return new QuotaDatabase();
            // Drop stale days on load so the file cannot grow without bound.
            var today = Today();
            foreach (var key in database.Entries.Where(pair => pair.Value.Date != today)
                         .Select(pair => pair.Key).ToArray())
                database.Entries.Remove(key);
            return database;
        }
        // A quota is a convenience, never a security boundary: an unreadable
        // file must not stop Guardian from enforcing passwords, so start over.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Starting over silently would hand the user a fresh quota with no
            // trace of why, so the discarded file is always reported.
            _logger?.LogWarning(ex,
                "No se pudo leer el uso diario en {Path}; se parte de un registro vacío y las cuotas de hoy vuelven a cero.",
                _path);
            return new QuotaDatabase();
        }
    }

    private void Save()
    {
        try
        {
            // The policy folder is created and hardened once by Guardian's
            // startup; re-applying its ACL here would also stamp SYSTEM-only
            // permissions onto whatever path a caller passed in.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_database, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            _saveFailureReported = false;
        }
        // Losing a few minutes of accounting is preferable to failing the
        // enforcement pass that called us, but a quota that silently stops
        // persisting looks exactly like a quota that was never configured, so
        // the first failure of each run is recorded.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_saveFailureReported) return;
            _saveFailureReported = true;
            _logger?.LogWarning(ex,
                "No se pudo guardar el uso diario en {Path}; las cuotas se perderán al reiniciar Guardian mientras persista el fallo.",
                _path);
        }
    }

    private sealed class QuotaDatabase
    {
        public Dictionary<string, QuotaEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class QuotaEntry
    {
        public string Date { get; set; } = string.Empty;
        public double UsedMinutes { get; set; }
    }
}
