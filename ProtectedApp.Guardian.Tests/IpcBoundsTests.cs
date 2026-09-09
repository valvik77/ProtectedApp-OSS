using System.Text;
using ProtectedApp.Service;
using ProtectedApp.Shared;
using Xunit;

namespace ProtectedApp.Guardian.Tests;

public class IpcBoundsTests
{
    [Fact]
    public async Task RejectsOversizedUnterminatedMessage()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', GuardianProtocol.MaxMessageCharacters + 4096)));
        using var reader = new StreamReader(stream);
        await Assert.ThrowsAsync<InvalidDataException>(() => GuardianIpcServer.ReadBoundedLineAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsMessageAtLimit()
    {
        var text = new string('a', GuardianProtocol.MaxMessageCharacters);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text + "\n"));
        using var reader = new StreamReader(stream);
        Assert.Equal(text, await GuardianIpcServer.ReadBoundedLineAsync(reader, CancellationToken.None));
    }
}
