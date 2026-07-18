using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class CapabilityMetadataTests
{
    [Theory]
    [InlineData(NvidiaGpuFanAdapter.AdapterId)]
    [InlineData(NvidiaGpuPowerLimitAdapter.AdapterId)]
    [InlineData(NvidiaGpuClockOffsetAdapter.CoreAdapterId)]
    [InlineData(NvidiaGpuClockOffsetAdapter.MemoryAdapterId)]
    [InlineData(LibreHardwareMonitorAdapter.AdapterId)]
    public void TransactionalHardwareAdaptersAdvertiseTheirImplementedReadBack(string adapterId)
    {
        CapabilityDescriptor capability = Capability(adapterId);

        CapabilityDescriptorV2 result = PCHelperRuntime.ToV2(capability);

        Assert.True(result.SupportsReadBack);
        Assert.Equal(ResetGuarantee.ReadBackVerified, result.ResetGuarantee);
    }

    [Fact]
    public void UnknownDetectedAdapterDoesNotGainReadBackFromResetAlone()
    {
        CapabilityDescriptorV2 result = PCHelperRuntime.ToV2(Capability("unknown.adapter"));

        Assert.False(result.SupportsReadBack);
    }

    private static CapabilityDescriptor Capability(string adapterId) => new(
        "control:0",
        adapterId,
        "device:0",
        "Bounded control",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "%",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "Test capability.",
        CanResetToDefault: true,
        Domain: ControlDomain.Cooling);
}
