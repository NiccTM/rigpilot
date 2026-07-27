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
            "0.5.0-alpha",
            17,
            ServiceRuntimeFeatures.AdvertisedByCurrentService);

        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.Evaluate("0.5.2-alpha", handshake);

        Assert.Equal(ServiceCompatibilityState.Ready, result.State);
        Assert.True(result.CanUseServiceWrites);
        Assert.Empty(result.MissingFeatures);
    }

    [Fact]
    public void ADottedPrereleaseSuffixStillResolvesToItsReleaseLine()
    {
        // 0.8.0-beta.1 is the first version the product has shipped whose suffix contains a
        // dot. The release-line check parses with a regex, so a suffix that itself looks
        // version-shaped is exactly where that parse could pick up the wrong number and
        // silently lock every write workflow behind "different release lines".
        HandshakeResponseV2 handshake = new(
            ProtocolConstants.Version,
            ProtocolConstants.LegacyReadOnlyVersion,
            "0.8.0-beta.1",
            17,
            ServiceRuntimeFeatures.AdvertisedByCurrentService);

        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.Evaluate("0.8.0-beta.1", handshake);

        Assert.Equal(ServiceCompatibilityState.Ready, result.State);
        Assert.True(result.CanUseServiceWrites);
    }

    [Fact]
    public void TheReleaseLineGateStillCatchesAnUnupgradedServiceAcrossAMinorBump()
    {
        // The 0.7 to 0.8 move is the real case: a dashboard updated ahead of its service must
        // lock writes rather than talk to it.
        HandshakeResponseV2 handshake = new(
            ProtocolConstants.Version,
            ProtocolConstants.LegacyReadOnlyVersion,
            "0.7.0",
            17,
            ServiceRuntimeFeatures.AdvertisedByCurrentService);

        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.Evaluate("0.8.0-beta.1", handshake);

        Assert.Equal(ServiceCompatibilityState.UpgradeRequired, result.State);
        Assert.False(result.CanUseServiceWrites);
        Assert.Contains("different release lines", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFeatureLocksWritesEvenWhenProtocolMatches()
    {
        HandshakeResponseV2 handshake = new(
            ProtocolConstants.Version,
            ProtocolConstants.LegacyReadOnlyVersion,
            "0.5.0-alpha",
            17,
            [ServiceRuntimeFeatures.ServiceStatus, ServiceRuntimeFeatures.CapabilityV2]);

        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.Evaluate("0.5.0-alpha", handshake);

        Assert.Equal(ServiceCompatibilityState.UpgradeRequired, result.State);
        Assert.False(result.CanUseServiceWrites);
        Assert.Contains(ServiceRuntimeFeatures.FanCommissioning, result.MissingFeatures);
        Assert.Contains(ServiceRuntimeFeatures.CoolingOutputRoles, result.MissingFeatures);
    }

    [Fact]
    public void LegacyHandshakePreservesReadOnlyClassificationOnly()
    {
        ServiceRuntimeCompatibilityV1 result = ServiceRuntimeCompatibility.EvaluateLegacy(
            "0.5.0-alpha",
            new HandshakeResponse(ProtocolConstants.LegacyReadOnlyVersion, "0.3.0-alpha", 9));

        Assert.Equal(ServiceCompatibilityState.ReadOnly, result.State);
        Assert.True(result.IsServiceReachable);
        Assert.False(result.CanUseServiceWrites);
    }
}
