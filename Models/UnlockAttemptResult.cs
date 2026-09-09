using ProtectedApp.Shared;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed record UnlockAttemptResult(bool Success, string? Error = null, int RetryAfterSeconds = 0,
    int FailureCount = 0)
{
    public static UnlockAttemptResult Accepted { get; } = new(true);
    public static UnlockAttemptResult Incorrect { get; } = new(false, LocalizationService.T("Contraseña incorrecta."));

    public static UnlockAttemptResult FromGuardian(GuardianResponse response, bool acceptPasswordWithoutLaunch = false)
    {
        var success = response.Success || acceptPasswordWithoutLaunch && response.PasswordAccepted;
        var error = response.Error
            ?? (response.PasswordRejected ? LocalizationService.T("Contraseña incorrecta.") : LocalizationService.T("Guardian rechazó la autenticación."));
        if (response.PasswordRejected && response.FailureCount > 0)
        {
            var separator = error.EndsWith('.') ? " " : ". ";
            error += response.FailureCount < 3
                ? LocalizationService.IsEnglish
                    ? $"{separator}Failed attempts: {response.FailureCount} of 3."
                    : $"{separator}Intentos fallidos: {response.FailureCount} de 3."
                : LocalizationService.IsEnglish
                    ? $"{separator}Failed attempts: {response.FailureCount}."
                    : $"{separator}Intentos fallidos: {response.FailureCount}.";
        }
        return success
            ? Accepted
            : new UnlockAttemptResult(false, error,
                response.RateLimited ? Math.Max(1, response.RetryAfterSeconds) : 0,
                response.FailureCount);
    }
}
