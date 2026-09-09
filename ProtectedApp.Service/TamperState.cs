using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProtectedApp.Service;

internal static class TamperState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object AuditSync = new();
    // This is intentionally short: it represents a responsive, initialized
    // interactive UI, not merely a process that happens to be present.
    private static readonly TimeSpan AgentHeartbeatLifetime = TimeSpan.FromSeconds(35);

    public static string BeginServiceLease(ILogger logger)
    {
        Directory.CreateDirectory(GuardianConstants.StateFolder);
        var bootUtc = CurrentBootUtc();
        try
        {
            if (!IsMaintenanceActive() && File.Exists(GuardianConstants.LeasePath))
            {
                var previous = JsonSerializer.Deserialize<ServiceLease>(File.ReadAllText(GuardianConstants.LeasePath));
                if (previous is not null
                    && Math.Abs((previous.BootUtc - bootUtc).TotalMinutes) < 2
                    && DateTimeOffset.UtcNow - previous.StartedUtc >= TimeSpan.Zero
                    && DateTimeOffset.UtcNow - previous.StartedUtc <= TimeSpan.FromMinutes(2))
                    SignalTamper("El proceso del servicio Guardian fue terminado inesperadamente.", logger);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo comprobar la señal de vida anterior."); }

        var lease = new ServiceLease(Guid.NewGuid().ToString("N"), Environment.ProcessId, bootUtc, DateTimeOffset.UtcNow);
        WriteJsonAtomically(GuardianConstants.LeasePath, lease);
        return lease.Id;
    }

    public static void EndServiceLease(string leaseId)
    {
        try
        {
            if (!File.Exists(GuardianConstants.LeasePath)) return;
            var current = JsonSerializer.Deserialize<ServiceLease>(File.ReadAllText(GuardianConstants.LeasePath));
            if (current?.Id == leaseId) File.Delete(GuardianConstants.LeasePath);
        }
        catch { }
    }

    public static void MarkAgentHeartbeat(uint sessionId)
    {
        try
        {
            Directory.CreateDirectory(GuardianConstants.StateFolder);
            WriteJsonAtomically(GuardianConstants.AgentLeasePath,
                new AgentLease(sessionId, CurrentBootUtc(), DateTimeOffset.UtcNow));
        }
        catch { }
    }

    public static bool IsAgentHeartbeatFresh(uint sessionId)
    {
        try
        {
            if (!File.Exists(GuardianConstants.AgentLeasePath)) return false;
            var lease = JsonSerializer.Deserialize<AgentLease>(File.ReadAllText(GuardianConstants.AgentLeasePath));
            return lease is not null
                && lease.SessionId == sessionId
                && Math.Abs((lease.BootUtc - CurrentBootUtc()).TotalMinutes) < 2
                // Windows Fast Startup can preserve the kernel boot time over
                // several user logons. A lease from an earlier sign-in must
                // never turn a normal startup into a tamper incident.
                && DateTimeOffset.UtcNow - lease.ObservedUtc >= TimeSpan.Zero
                && DateTimeOffset.UtcNow - lease.ObservedUtc <= AgentHeartbeatLifetime;
        }
        catch { return false; }
    }

    // Kept for callers compiled against earlier service revisions.
    public static bool WasAgentObservedThisBoot(uint sessionId) => IsAgentHeartbeatFresh(sessionId);

    public static void ClearAgentObservation()
    {
        try { File.Delete(GuardianConstants.AgentLeasePath); }
        catch { }
    }

    public static bool IsMaintenanceActive()
    {
        try
        {
            if (!File.Exists(GuardianConstants.MaintenancePath)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(GuardianConstants.MaintenancePath) <= TimeSpan.FromMinutes(10))
                return true;
            File.Delete(GuardianConstants.MaintenancePath);
        }
        // If maintenance state cannot be read or cleaned, do not suppress a
        // tamper signal. Maintenance is an exception to protection, so it must
        // be positively established rather than inferred from an I/O failure.
        catch { return false; }
        return false;
    }

    public static void SignalTamper(string reason, ILogger? logger = null, TamperEventCode code = TamperEventCode.TamperDetected)
    {
        if (IsMaintenanceActive()) return;
        Directory.CreateDirectory(GuardianConstants.StateFolder);
        try { AppendAuditEntry(reason, code); }
        catch (Exception ex) { logger?.LogWarning(ex, "No se pudo añadir el evento a la auditoría protegida."); }
        using var writeMutex = new Mutex(false, @"Global\ProtectedAppGuardian.TamperQueue");
        var lockTaken = false;
        try
        {
            try { lockTaken = writeMutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { lockTaken = true; }
            var signals = ReadSignals();
            signals.Add(new TamperSignal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, reason));
            if (signals.Count > 50) signals.RemoveRange(0, signals.Count - 50);
            WriteJsonAtomically(GuardianConstants.TamperPath, signals);
        }
        catch (Exception ex) { logger?.LogWarning(ex, "No se pudo guardar la señal de manipulación."); }
        finally { if (lockTaken) writeMutex.ReleaseMutex(); }
        logger?.LogWarning("Manipulación detectada: {Reason}", reason);
        try
        {
            EventLog.WriteEntry(GuardianConstants.EventSource, reason, EventLogEntryType.Warning, 1001);
        }
        catch (Exception ex) { logger?.LogWarning(ex, "No se pudo escribir en el Visor de eventos."); }
        try { TamperWebhookNotifier.Enqueue(code); }
        catch (Exception ex) { logger?.LogWarning(ex, "No se pudo poner en cola la alerta remota de manipulación."); }
    }

    public static void WriteRecoveryEvent(string message)
    {
        try { EventLog.WriteEntry(GuardianConstants.EventSource, message, EventLogEntryType.Information, 1002); }
        catch { }
    }

    public static void EnterSafeRecovery(string reason, ILogger? logger = null)
    {
        if (IsMaintenanceActive()) return;
        try
        {
            Directory.CreateDirectory(GuardianConstants.StateFolder);
            if (File.Exists(GuardianConstants.SafeRecoveryPath)) return;
            WriteJsonAtomically(GuardianConstants.SafeRecoveryPath,
                new SafeRecoveryState(DateTimeOffset.UtcNow, reason));
            EventLog.WriteEntry(GuardianConstants.EventSource, reason, EventLogEntryType.Error, 1003);
            TamperWebhookNotifier.Enqueue(TamperEventCode.RecoveryFailed);
        }
        catch (Exception ex) { logger?.LogWarning(ex, "No se pudo activar el estado de recuperación segura."); }
    }

    public static void ExitSafeRecovery()
    {
        try { File.Delete(GuardianConstants.SafeRecoveryPath); }
        catch { }
    }

    public static bool IsAuditTrailValid()
    {
        try
        {
            if (!File.Exists(GuardianConstants.TamperAuditPath))
                return !File.Exists(GuardianConstants.TamperAuditKeyPath);
            var entries = JsonSerializer.Deserialize<List<TamperAuditEntry>>(
                File.ReadAllText(GuardianConstants.TamperAuditPath)) ?? [];
            if (entries.Count == 0) return !File.Exists(GuardianConstants.TamperAuditKeyPath);
            var key = GetOrCreateAuditKey();
            var previousHash = string.Empty;
            foreach (var entry in entries)
            {
                if (!string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(entry.Hash),
                        Convert.FromHexString(ComputeAuditHash(key, previousHash, entry.Id, entry.TimestampUtc, entry.Reason, entry.Code))))
                    return false;
                previousHash = entry.Hash;
            }
            return true;
        }
        catch { return false; }
    }

    private static DateTimeOffset CurrentBootUtc() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temp = path + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, true);
    }

    private static List<TamperSignal> ReadSignals()
    {
        if (!File.Exists(GuardianConstants.TamperPath)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(GuardianConstants.TamperPath));
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<TamperSignal>>(document.RootElement.GetRawText()) ?? [];
            var previous = JsonSerializer.Deserialize<TamperSignal>(document.RootElement.GetRawText());
            return previous is null ? [] : [previous];
        }
        catch { return []; }
    }

    private static void AppendAuditEntry(string reason, TamperEventCode code)
    {
        lock (AuditSync)
        {
            var entries = File.Exists(GuardianConstants.TamperAuditPath)
                ? JsonSerializer.Deserialize<List<TamperAuditEntry>>(File.ReadAllText(GuardianConstants.TamperAuditPath)) ?? []
                : [];
            var previousHash = entries.LastOrDefault()?.Hash ?? string.Empty;
            var timestamp = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid().ToString("N");
            var hash = ComputeAuditHash(GetOrCreateAuditKey(), previousHash, id, timestamp, reason, code.ToString());
            entries.Add(new TamperAuditEntry(id, timestamp, reason, code.ToString(), previousHash, hash));
            if (entries.Count > 200) entries.RemoveRange(0, entries.Count - 200);
            // The retained first entry becomes a new, self-contained chain.
            if (entries.Count > 0 && !string.IsNullOrEmpty(entries[0].PreviousHash))
                RebuildAuditChain(entries);
            WriteJsonAtomically(GuardianConstants.TamperAuditPath, entries);
        }
    }

    private static void RebuildAuditChain(List<TamperAuditEntry> entries)
    {
        var key = GetOrCreateAuditKey();
        var previousHash = string.Empty;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var hash = ComputeAuditHash(key, previousHash, entry.Id, entry.TimestampUtc, entry.Reason, entry.Code);
            entries[index] = entry with { PreviousHash = previousHash, Hash = hash };
            previousHash = hash;
        }
    }

    private static byte[] GetOrCreateAuditKey()
    {
        if (File.Exists(GuardianConstants.TamperAuditKeyPath))
            return ProtectedData.Unprotect(File.ReadAllBytes(GuardianConstants.TamperAuditKeyPath), null,
                DataProtectionScope.LocalMachine);
        Directory.CreateDirectory(GuardianConstants.PolicyFolder);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(GuardianConstants.TamperAuditKeyPath,
            ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine));
        return key;
    }

    private static string ComputeAuditHash(byte[] key, string previousHash, string id, DateTimeOffset timestamp,
        string reason, string code)
    {
        var payload = $"{previousHash}\n{id}\n{timestamp:O}\n{reason}\n{code}";
        return Convert.ToHexString(HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    private sealed record ServiceLease(string Id, int ProcessId, DateTimeOffset BootUtc, DateTimeOffset StartedUtc);
    private sealed record AgentLease(uint SessionId, DateTimeOffset BootUtc, DateTimeOffset ObservedUtc);
    private sealed record TamperSignal(string Id, DateTimeOffset TimestampUtc, string Reason);
    private sealed record TamperAuditEntry(string Id, DateTimeOffset TimestampUtc, string Reason, string Code,
        string PreviousHash, string Hash);
    private sealed record SafeRecoveryState(DateTimeOffset EnteredUtc, string Reason);
}
