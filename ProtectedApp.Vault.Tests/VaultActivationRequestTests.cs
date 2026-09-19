using ProtectedApp.Services;
using Xunit;

namespace ProtectedApp.Vault.Tests;

public sealed class VaultActivationRequestTests
{
    [Fact]
    public void ExplicitOpenVaultActivation_IsSilentAndDoesNotUseTheMountToggle()
    {
        var vaultPath = Path.Combine(Path.GetTempPath(), "ProtectedApp-Activation-Test.pavault");
        var expected = Path.GetFullPath(vaultPath);

        Assert.Equal(expected, VaultActivationRequest.TryGetVaultOpenPath(
            ["ProtectedApp.exe", "--open-vault", vaultPath]));
        Assert.Null(VaultActivationRequest.TryGetVaultActionPath(
            ["ProtectedApp.exe", "--open-vault", vaultPath]));
        Assert.True(AppLaunchMode.IsSilent(["ProtectedApp.exe", "--open-vault", vaultPath]));

        Assert.Equal(expected, VaultActivationRequest.TryGetVaultActionPath(
            ["ProtectedApp.exe", "--vault-action", vaultPath]));
        Assert.Null(VaultActivationRequest.TryGetVaultOpenPath(
            ["ProtectedApp.exe", "--vault-action", vaultPath]));
    }
}
