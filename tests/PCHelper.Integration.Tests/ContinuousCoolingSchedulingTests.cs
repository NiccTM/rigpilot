using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class ContinuousCoolingSchedulingTests
{
    [Fact]
    public void AutoOcDoesNotPauseCoolingOutputs()
    {
        CapabilityDescriptor core = Capability("gpuclock.core:0", ControlDomain.Gpu);
        HardwareOperationStatus operation = Operation(HardwareOperationKind.AutoOc, core);

        string? excluded = PCHelperRuntime.SelectCoolingOperationExclusion(operation, Snapshot(core));

        Assert.Null(excluded);
    }

    [Fact]
    public void CalibrationExcludesOnlyItsExactCoolingOutput()
    {
        CapabilityDescriptor fan = Capability("fan.case.1", ControlDomain.Cooling);
        CapabilityDescriptor otherFan = Capability("fan.case.2", ControlDomain.Cooling);
        HardwareOperationStatus operation = Operation(HardwareOperationKind.Calibration, fan);

        string? excluded = PCHelperRuntime.SelectCoolingOperationExclusion(operation, Snapshot(fan, otherFan));

        Assert.Equal(fan.Id, excluded);
        Assert.NotEqual(otherFan.Id, excluded);
    }

    private static HardwareOperationStatus Operation(HardwareOperationKind kind, CapabilityDescriptor capability) => new(
        "operation",
        kind,
        HardwareOperationState.Running,
        capability.Id,
        capability.Name,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        50,
        "Running",
        null,
        null,
        null);

    private static HardwareSnapshot Snapshot(params CapabilityDescriptor[] capabilities) => new(
        DateTimeOffset.UtcNow,
        [],
        capabilities,
        [],
        [],
        [],
        []);

    private static CapabilityDescriptor Capability(string id, ControlDomain domain) => new(
        id,
        "adapter",
        "device",
        id,
        CapabilityAccessState.Verified,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(20, 100, 1, 100),
        "%",
        RiskLevel.Guarded,
        EvidenceLevel.ReadBackVerified,
        null,
        "Test",
        true,
        domain);
}
