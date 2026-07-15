using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class ServiceRuntimeCompatibilityTests
{
    [Fact]
    public void MatchingProtocolFeaturesAndReleaseLinePermitServiceWrites()
    {
        HandshakeResponseV2 handshake = new(
            ProtocolConstants.Version,
            ProtocolConstants.LegacyReadOnlyVersion,
            "0.4.0-alpha",
            17,
            ServiceRuntimeFeatures.AdvertisedByCurrentService);

        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.Evaluate("0.4.2-alpha", handshake);

        Assert.Equal(ServiceCompatibilityState.Ready, result.State);
        Assert.True(result.CanUseServiceWrites);
        Assert.Empty(result.MissingFeatures);
    }

    [Fact]
    public void MissingFeatureLocksWritesEvenWhenProtocolMatches()
    {
        HandshakeResponseV2 handshake = new(
            ProtocolConstants.Version,
            ProtocolConstants.LegacyReadOnlyVersion,
            "0.4.0-alpha",
            17,
            [ServiceRuntimeFeatures.ServiceStatus, ServiceRuntimeFeatures.CapabilityV2]);

        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.Evaluate("0.4.0-alpha", handshake);

        Assert.Equal(ServiceCompatibilityState.UpgradeRequired, result.State);
        Assert.False(result.CanUseServiceWrites);
        Assert.Contains(ServiceRuntimeFeatures.FanCommissioning, result.MissingFeatures);
        Assert.Contains(ServiceRuntimeFeatures.CoolingOutputRoles, result.MissingFeatures);
    }

    [Fact]
    public void LegacyHandshakePreservesReadOnlyClassificationOnly()
    {
        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.EvaluateLegacy(
            "0.4.0-alpha",
            new HandshakeResponse(ProtocolConstants.LegacyReadOnlyVersion, "0.3.0-alpha", 9));

        Assert.Equal(ServiceCompatibilityState.ReadOnly, result.State);
        Assert.True(result.IsServiceReachable);
        Assert.False(result.CanUseServiceWrites);
    }
}
