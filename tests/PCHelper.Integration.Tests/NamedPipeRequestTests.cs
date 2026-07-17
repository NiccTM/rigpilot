using PCHelper.Contracts;
using PCHelper.Ipc;
using System.IO.Pipes;

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
    public async Task NormalPipeEvaluatesTheCurrentClientIdentity()
    {
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        NamedPipeRequestServer server = new(
            pipeName,
            (request, context, _) => Task.FromResult(new IpcResponse(
                ProtocolConstants.Version,
                request.RequestId,
                true,
                0,
                null,
                null,
                IpcJson.ToElement(context))));
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeRequestClient client = new(pipeName, TimeSpan.FromSeconds(5));

        IpcResponse response = await client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
            CancellationToken.None);
        IpcClientContext observed = IpcJson.FromElement<IpcClientContext>(response.Payload)
            ?? throw new InvalidDataException("Server returned an empty client context.");

        Assert.True(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(observed.UserName));
        Assert.False(string.IsNullOrWhiteSpace(observed.UserSid));
        shutdown.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task TokenAuthenticatedPrivatePipeFailsClosedWithoutClientImpersonation()
    {
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        NamedPipeRequestServer server = new(
            pipeName,
            (request, context, _) => Task.FromResult(new IpcResponse(
                ProtocolConstants.Version,
                request.RequestId,
                true,
                0,
                null,
                null,
                IpcJson.ToElement(context))),
            clientIdentityMode: NamedPipeClientIdentityMode.TokenAuthenticatedPrivateChannel);
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeRequestClient client = new(pipeName, TimeSpan.FromSeconds(5));

        IpcResponse response = await client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
            CancellationToken.None);
        IpcClientContext observed = IpcJson.FromElement<IpcClientContext>(response.Payload)
            ?? throw new InvalidDataException("Server returned an empty client context.");

        Assert.True(response.Success);
        Assert.False(observed.IsOperator);
        Assert.Null(observed.UserName);
        Assert.Null(observed.UserSid);
        Assert.False(observed.IsAppContainer);
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

    [Fact]
    public async Task ProtocolOneCompatibilityWindowIsStrictlyReadOnly()
    {
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        int calls = 0;
        NamedPipeRequestServer server = new(pipeName, (request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new IpcResponse(
                ProtocolConstants.Version,
                request.RequestId,
                true,
                0,
                null,
                null,
                IpcJson.ToElement("ok")));
        });
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeRequestClient client = new(pipeName, TimeSpan.FromSeconds(5));

        IpcResponse read = await client.SendAsync(
            NamedPipeRequestClient.CreateLegacyReadOnlyRequest(IpcCommand.GetServiceStatus),
            CancellationToken.None);
        IpcRequest mutation = new(
            ProtocolConstants.LegacyReadOnlyVersion,
            Guid.NewGuid().ToString("N"),
            IpcCommand.ResetHardware,
            0,
            Guid.NewGuid().ToString("N"),
            IpcJson.ToElement("fan.cpu"));
        IpcResponse rejected = await client.SendAsync(mutation, CancellationToken.None);

        Assert.True(read.Success);
        Assert.Equal(ProtocolConstants.LegacyReadOnlyVersion, read.ProtocolVersion);
        Assert.False(rejected.Success);
        Assert.Equal("PROTOCOL_MISMATCH", rejected.ErrorCode);
        Assert.Equal(1, calls);
        shutdown.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task SlowResponseWithinTheOperationTimeoutStillSucceeds()
    {
        // Regression: a transactional apply can take longer than the short
        // connect timeout. The response here arrives after 700 ms, well past a
        // 250 ms connect timeout but inside the 10 s operation timeout, and must
        // still succeed — connect and operation timeouts are independent now.
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        NamedPipeRequestServer server = new(pipeName, async (request, cancellationToken) =>
        {
            await Task.Delay(700, cancellationToken);
            return new IpcResponse(
                ProtocolConstants.Version, request.RequestId, true, 5, null, null, IpcJson.ToElement("ok"));
        });
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeRequestClient client = new(
            pipeName,
            connectTimeout: TimeSpan.FromMilliseconds(250),
            operationTimeout: TimeSpan.FromSeconds(10));

        IpcResponse response = await client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal(5, response.StateRevision);
        shutdown.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task OperationTimeoutSurfacesAClearTimeoutException()
    {
        // A server that never responds must produce an actionable TimeoutException,
        // not a bare "operation was canceled" from the internal cancellation.
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        NamedPipeRequestServer server = new(pipeName, async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new IpcResponse(ProtocolConstants.Version, request.RequestId, true, 0, null, null, null);
        });
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeRequestClient client = new(
            pipeName,
            connectTimeout: TimeSpan.FromSeconds(2),
            operationTimeout: TimeSpan.FromMilliseconds(300));

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
            CancellationToken.None));

        Assert.Contains("did not respond", exception.Message, StringComparison.Ordinal);
        shutdown.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DisconnectedClientDoesNotFaultServer()
    {
        string pipeName = $"pchelper.tests.{Guid.NewGuid():N}";
        using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));
        NamedPipeRequestServer server = new(pipeName, async (request, cancellationToken) =>
        {
            await Task.Delay(100, cancellationToken);
            return new IpcResponse(
                ProtocolConstants.Version,
                request.RequestId,
                true,
                9,
                null,
                null,
                IpcJson.ToElement(new string('x', 1024 * 1024)));
        });
        Task serverTask = server.RunAsync(shutdown.Token);

        await using (NamedPipeClientStream abandoned = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await abandoned.ConnectAsync(shutdown.Token);
            await PipeFraming.WriteAsync(
                abandoned,
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
                shutdown.Token);
        }

        await Task.Delay(250, shutdown.Token);
        NamedPipeRequestClient client = new(pipeName, TimeSpan.FromSeconds(5));
        IpcResponse response = await client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetServiceStatus),
            shutdown.Token);

        Assert.True(response.Success);
        Assert.Equal(9, response.StateRevision);
        shutdown.Cancel();
        await serverTask;
    }
}
