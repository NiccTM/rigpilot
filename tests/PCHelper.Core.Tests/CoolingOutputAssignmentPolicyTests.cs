using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class CoolingOutputAssignmentPolicyTests
{
    [Fact]
    public void PumpAssignmentProtectsAGenericSuperIoLabel()
    {
        CapabilityDescriptor capability = Capability();
        CoolingOutputAssignmentV1 assignment = Assignment(CoolingOutputRole.Pump, "AIO_PUMP");

        Assert.True(CoolingOutputAssignmentPolicy.MatchesExactController(assignment, capability));
        Assert.True(CoolingOutputAssignmentPolicy.IsProtected(assignment, capability));
        Assert.True(assignment.IsSafetyCritical);
    }

    [Fact]
    public void StaleControllerMetadataStillFailsClosedForTheSameCapabilityId()
    {
        CapabilityDescriptor capability = Capability();
        CoolingOutputAssignmentV1 assignment = Assignment(CoolingOutputRole.Pump, "AIO_PUMP") with
        {
            DeviceId = "lhm.device:/lpc/old-controller/0"
        };

        Assert.False(CoolingOutputAssignmentPolicy.MatchesExactController(assignment, capability));
        Assert.True(CoolingOutputAssignmentPolicy.IsProtected(assignment, capability));
    }

    [Fact]
    public void RemovingAPumpRoleRequiresExplicitConfirmation()
    {
        CoolingOutputAssignmentV1 pump = Assignment(CoolingOutputRole.Pump, "AIO_PUMP");

        Assert.True(CoolingOutputAssignmentPolicy.RequiresExplicitProtectionRemoval(pump, CoolingOutputRole.CaseFan));
        Assert.True(CoolingOutputAssignmentPolicy.RequiresExplicitProtectionRemoval(pump, CoolingOutputRole.Unknown));
        Assert.False(CoolingOutputAssignmentPolicy.RequiresExplicitProtectionRemoval(pump, CoolingOutputRole.CpuFan));
        Assert.False(CoolingOutputAssignmentPolicy.RequiresExplicitProtectionRemoval(null, CoolingOutputRole.CaseFan));
    }

    [Fact]
    public void AssignmentValidationRejectsMissingPhysicalHeader()
    {
        CoolingOutputAssignmentV1 invalid = Assignment(CoolingOutputRole.Pump, " ");

        IReadOnlyList<string> errors = CoolingOutputAssignmentPolicy.Validate(invalid);

        Assert.Contains(errors, error => error.Contains("header", StringComparison.OrdinalIgnoreCase));
    }

    private static CapabilityDescriptor Capability() => new(
        "lhm.control:/lpc/nct6798d/0/control/4",
        "librehardwaremonitor",
        "lhm.device:/lpc/nct6798d/0",
        "Fan #5",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.AdapterHost,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "%",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "Generic Super I/O controller label.",
        CanResetToDefault: true,
        Domain: ControlDomain.Cooling);

    private static CoolingOutputAssignmentV1 Assignment(CoolingOutputRole role, string headerName) => new(
        CoolingOutputAssignmentV1.CurrentSchemaVersion,
        "lhm.control:/lpc/nct6798d/0/control/4",
        "lhm.control:/lpc/nct6798d/0/control/4",
        "librehardwaremonitor",
        "lhm.device:/lpc/nct6798d/0",
        "lhm.sensor:/lpc/nct6798d/0/fan/4",
        headerName,
        role,
        DateTimeOffset.UtcNow,
        "User confirmed NZXT AIO pump.");
}
