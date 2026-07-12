using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Integration.Tests;

public sealed class PipeFramingTests
{
    [Fact]
    public async Task RoundTripsLengthPrefixedMessage()
    {
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.Handshake,
            new HandshakeRequest("tests", "1"));
        await using MemoryStream stream = new();

        await PipeFraming.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        IpcRequest result = await PipeFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None);

        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(IpcCommand.Handshake, result.Command);
    }

    [Fact]
    public async Task RejectsOversizedLengthBeforeAllocatingPayload()
    {
        await using MemoryStream stream = new();
        byte[] length = BitConverter.GetBytes(ProtocolConstants.MaximumMessageBytes + 1);
        await stream.WriteAsync(length);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PipeFraming.ReadAsync<IpcRequest>(stream, CancellationToken.None));
    }
}
