using System.IO.Pipes;
using System.Security.Principal;
using ProtectedApp.Service;
using ProtectedApp.Shared;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public class IpcClientIdentityTests
{
    [Fact]
    public void ClientsConnectWithIdentificationLevelOnly() =>
        Assert.Equal(TokenImpersonationLevel.Identification, GuardianProtocol.ClientImpersonationLevel);

    [Fact]
    public async Task ServerStillIdentifiesTheCallerAtIdentificationLevel()
    {
        // A private pipe name keeps this away from a Guardian installed on the machine.
        var name = $"ProtectedApp.Guardian.Tests.{Guid.NewGuid():N}";
        var expected = WindowsIdentity.GetCurrent().User!.Value;

        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, GuardianProtocol.ClientImpersonationLevel);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));

        Assert.Equal(expected, GuardianIpcServer.GetCallerSid(server));
    }
}
