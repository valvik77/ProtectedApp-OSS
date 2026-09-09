using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProtectedApp.Models;
using ProtectedApp.Shared;

namespace ProtectedApp.Services;

public sealed class GuardianClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _userSid = WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("No se pudo obtener la identidad del usuario.");

    public Task<GuardianResponse> GetStatusAsync() => SendAsync(new GuardianRequest { Type = GuardianProtocol.Status });

    public Task<GuardianResponse> ReportAgentHeartbeatAsync() =>
        SendAsync(new GuardianRequest { Type = GuardianProtocol.AgentHeartbeat });

    public Task<GuardianResponse> GetDiagnosticsAsync(string? token = null) =>
        SendAsync(new GuardianRequest { Type = GuardianProtocol.Diagnostics, Token = token });

    public async Task<bool> IsAvailableAsync() => (await GetStatusAsync()).Success;

    public Task<GuardianResponse> BootstrapPolicyDetailedAsync(AppState state, string bootstrapSecret) =>
        SendAsync(new GuardianRequest
        {
            Type = GuardianProtocol.BootstrapPolicy,
            Token = bootstrapSecret,
            Policy = CreatePolicy(state)
        });

    public async Task<bool> BootstrapPolicyAsync(AppState state, string bootstrapSecret) =>
        (await BootstrapPolicyDetailedAsync(state, bootstrapSecret)).Success;

    public Task<GuardianResponse> AuthenticateMasterDetailedAsync(string password) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.AuthenticateMaster,
        Password = password
    });

    public async Task<string?> AuthenticateMasterAsync(string password)
    {
        var response = await AuthenticateMasterDetailedAsync(password);
        return response.Success ? response.Token : null;
    }

    public Task<GuardianResponse> RecoverPolicyAsync(string? password = null, string? token = null) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.RecoverPolicy,
        Password = password,
        Token = token
    });

    public async Task<bool> SyncPolicyAsync(AppState state, string token)
    {
        var response = await SendAsync(new GuardianRequest
        {
            Type = GuardianProtocol.SyncPolicy,
            Token = token,
            Policy = CreatePolicy(state)
        });
        return response.Success;
    }

    public Task<GuardianResponse> SyncPolicyDetailedAsync(AppState state, string token) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.SyncPolicy,
        Token = token,
        Policy = CreatePolicy(state)
    });

    public Task<GuardianResponse> SetApplicationEnabledAsync(AppState state, Guid applicationId,
        bool isEnabled, string token)
    {
        var policy = CreatePolicy(state);
        var rule = policy.Rules.FirstOrDefault(candidate => candidate.Id == applicationId);
        if (rule is null)
            return Task.FromResult(new GuardianResponse { Error = "La aplicación ya no pertenece a la política." });

        rule.IsEnabled = isEnabled;
        return SendAsync(new GuardianRequest
        {
            Type = GuardianProtocol.SyncPolicy,
            Token = token,
            Policy = policy
        });
    }

    public Task<GuardianResponse> LockAllAsync(string token) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.LockAll,
        Token = token
    }, TimeSpan.FromSeconds(12));

    // This command revokes access and asks protected applications to close normally.
    // It is intentionally available to the verified interactive agent without
    // a management token so a user-configured emergency shortcut remains fast.
    public Task<GuardianResponse> EmergencyLockAsync() => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.EmergencyLock
    }, TimeSpan.FromSeconds(12));

    public Task<GuardianResponse> LockRuleAsync(Guid ruleId, string token) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.LockRule,
        RuleId = ruleId,
        Token = token
    });

    public Task<GuardianResponse> EndApplicationSessionAsync(Guid ruleId) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.EndApplicationSession,
        RuleId = ruleId
    });

    public Task<GuardianResponse> UnlockFolderAsync(Guid ruleId, string? password = null, string? token = null) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.UnlockFolder,
        RuleId = ruleId,
        Password = password,
        Token = token
    });

    public Task<GuardianResponse> LockFolderAsync(Guid ruleId, string token) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.LockFolder,
        RuleId = ruleId,
        Token = token
    });

    public Task<GuardianResponse> RestoreOrphanedFolderLockAsync(string path, string token) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.RestoreOrphanedFolderLock,
        TargetPath = path,
        Token = token
    });

    public Task<GuardianResponse> PrepareUninstallAsync(string token) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.PrepareUninstall,
        Token = token
    });

    public Task<GuardianResponse> GetTamperWebhookAsync(string token) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.GetTamperWebhook, Token = token
    });

    public Task<GuardianResponse> ConfigureTamperWebhookAsync(bool enabled, string? url, bool useHmac, string? secret, string token) =>
        SendAsync(new GuardianRequest
        {
            Type = GuardianProtocol.ConfigureTamperWebhook, Token = token,
            WebhookEnabled = enabled, WebhookUrl = url, WebhookUseHmac = useHmac, WebhookSecret = secret
        });

    public async Task<GuardianPendingRequest?> ClaimPendingAsync()
    {
        var response = await SendAsync(new GuardianRequest { Type = GuardianProtocol.ClaimPending });
        return response.Success ? response.Pending : null;
    }

    public async Task DismissPendingAsync(Guid ruleId) => await SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.DismissPending,
        RuleId = ruleId
    });

    public Task<GuardianResponse> AuthorizeAndLaunchAsync(Guid ruleId, string? password = null, string? token = null) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.AuthorizeAndLaunch,
        RuleId = ruleId,
        Password = password,
        Token = token
    });

    public Task<GuardianResponse> ExtendTimedSessionAsync(Guid ruleId, string? timedSessionToken,
        string? authorizationToken = null) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.ExtendTimedSession,
        RuleId = ruleId,
        TimedSessionToken = timedSessionToken,
        Token = authorizationToken
    });

    public Task<GuardianResponse> ExtendInactiveSessionAsync(Guid ruleId, string? timedSessionToken,
        string? authorizationToken = null) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.ExtendInactiveSession,
        RuleId = ruleId,
        TimedSessionToken = timedSessionToken,
        Token = authorizationToken
    });

    public Task<GuardianResponse> ReportApplicationActivityAsync(Guid ruleId) => SendAsync(new GuardianRequest
    {
        Type = GuardianProtocol.ReportApplicationActivity,
        RuleId = ruleId
    });

    private async Task<GuardianResponse> SendAsync(GuardianRequest request, TimeSpan? timeoutOverride = null)
    {
        request.UserSid = _userSid;
        request.SessionId = Process.GetCurrentProcess().SessionId;
        try
        {
            // Immediate lock deliberately grants applications five seconds to close
            // cleanly. Its IPC deadline must therefore exceed the server's ten-second
            // safety limit; ordinary requests remain responsive at three seconds.
            using var timeout = new CancellationTokenSource(timeoutOverride ?? TimeSpan.FromSeconds(3));
            await using var pipe = new NamedPipeClientStream(".", GuardianProtocol.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null || line.Length > GuardianProtocol.MaxMessageCharacters)
                return new GuardianResponse { Error = "Guardian no devolvió una respuesta válida." };
            return JsonSerializer.Deserialize<GuardianResponse>(line, JsonOptions)
                ?? new GuardianResponse { Error = "Respuesta de Guardian no válida." };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            return new GuardianResponse { Error = "Guardian no está disponible." };
        }
    }

    private GuardianPolicy CreatePolicy(AppState state) => new()
    {
        UserSid = _userSid,
        Revision = DateTime.UtcNow.Ticks,
        MasterPasswordHash = state.MasterPasswordHash,
        MasterPasswordSalt = state.MasterPasswordSalt,
        ScanIntervalMilliseconds = state.PollIntervalMilliseconds,
        CloseWarningNotificationsEnabled = state.CloseWarningNotificationsEnabled,
        Rules = state.Applications.Select(app => new GuardianRule
        {
            Id = app.Id,
            Name = app.Name,
            Path = app.Path,
            Category = app.Category,
            IsEnabled = app.IsEnabled,
            PasswordHash = app.PasswordHash,
            PasswordSalt = app.PasswordSalt,
            UnlockGraceMinutes = app.UnlockGraceMinutes,
            ForceCloseAfterMinutes = app.ForceCloseAfterMinutes,
            ForceCloseAfterInactivityMinutes = app.ForceCloseAfterInactivityMinutes,
            ScheduleEnabled = app.ScheduleEnabled,
            ScheduleDays = app.ScheduleDays,
            ScheduleStartMinutes = app.ScheduleStartMinutes,
            ScheduleEndMinutes = app.ScheduleEndMinutes,
            BlockOutsideSchedule = app.BlockOutsideSchedule
        }).ToList(),
        // Folder protection is now implemented as encrypted vault conversion.
        // Sending no rules also asks a current Guardian to restore any legacy
        // NTFS folder locks it still records.
        FolderRules = []
    };
}
