namespace ProtectedApp.Models;

public sealed record VaultWriteRecoveryResult(bool Restored, bool PasswordVerified, string? Error);
