using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Integration.Tests;

public sealed class NamedPipeRequestTests
{
    [Fact]
    public async Task CurrentUserCanCallSecuredPipe()
    {
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        NamedPipeRequestServer server = new(pipeName, (request, _) => Task.FromResult(new IpcResponse(
            ProtocolConstants.Version,
            request.RequestId,
            true,
            7,
            null,
            null,
            IpcJson.ToElement("ok"))));
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeRequestClient client = new(pipeName, TimeSpan.FromSeconds(5));

        IpcResponse response = await client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal(7, response.StateRevision);
        Assert.Equal("ok", IpcJson.FromElement<string>(response.Payload));
        shutdown.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task ProtocolMismatchReturnsTypedError()
    {
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        NamedPipeRequestServer server = new(pipeName, (_, _) => throw new InvalidOperationException("Handler must not run."));
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeRequestClient client = new(pipeName, TimeSpan.FromSeconds(5));
        IpcRequest request = new(
            ProtocolConstants.Version + 1,
            Guid.NewGuid().ToString("N"),
            IpcCommand.Handshake,
            null,
            null,
            null);

        IpcResponse response = await client.SendAsync(request, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("PROTOCOL_MISMATCH", response.ErrorCode);
        shutdown.Cancel();
        await serverTask;
    }
}
