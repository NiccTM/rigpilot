using PCHelper.Contracts;
using PCHelper.Ipc;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class ServiceHandshakeTests
{
    [Fact]
    public void ProtocolTwoHandshakeAdvertisesRequiredRuntimeFeatures()
    {
        IpcRequest request = new(
            ProtocolConstants.Version,
            "handshake-test",
            IpcCommand.Handshake,
            null,
            null,
            IpcJson.ToElement(new HandshakeRequestV2(
                "RigPilot test",
                "0.5.0-alpha",
                ProtocolConstants.Version,
                ProtocolConstants.Version)));

        IpcResponse response = ServiceHandshake.Create(request, "0.5.0-alpha", 42);
        HandshakeResponseV2 handshake = IpcJson.FromElement<HandshakeResponseV2>(response.Payload)!;

        Assert.True(response.Success);
        Assert.Equal(42, response.StateRevision);
        Assert.Equal(ProtocolConstants.Version, handshake.SelectedProtocolVersion);
        Assert.All(ServiceRuntimeFeatures.RequiredByDashboard, feature =>
            Assert.Contains(feature, handshake.Features, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(ServiceRuntimeFeatures.AutoOcV3, handshake.Features, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ServiceRuntimeFeatures.ProfileDryRunV1, handshake.Features, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidProtocolRangeIsRejectedBeforeAnyRuntimeMutation()
    {
        IpcRequest request = new(
            ProtocolConstants.Version,
            "invalid-handshake-test",
            IpcCommand.Handshake,
            null,
            null,
            IpcJson.ToElement(new HandshakeRequestV2(
                "RigPilot test",
                "0.5.0-alpha",
                ProtocolConstants.Version,
                ProtocolConstants.LegacyReadOnlyVersion)));

        IpcResponse response = ServiceHandshake.Create(request, "0.5.0-alpha", 42);

        Assert.False(response.Success);
        Assert.Equal("INVALID_HANDSHAKE", response.ErrorCode);
    }
}
