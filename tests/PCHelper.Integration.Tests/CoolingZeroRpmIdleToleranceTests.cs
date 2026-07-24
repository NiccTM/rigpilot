using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The running cooling loop tolerates a case fan that sits physically stopped (~0%)
/// under a low commanded duty — the motherboard/BIOS keeping it off at low temperature
/// is the same zero-RPM idle the GPU fan exhibits, not a control failure, and failing
/// closed on it needlessly breaks cooling automation.
///
/// The safety guarantee that makes this acceptable is the exclusion, so it is what these
/// tests pin: a pump or CPU fan is NEVER excused for sitting stopped, and no output of any
/// role is excused under a maximum/emergency command. Without that, this tolerance would
/// silently accept a dead pump.
/// </summary>
public sealed class CoolingZeroRpmIdleToleranceTests
{
    private const string CapabilityId = "lhm.control:/lpc/nct6798d/0/control/1";

    [Fact]
    public void ACaseFanStoppedAtALowDutyIsAcceptedAsZeroRpmIdle()
    {
        Assert.True(PCHelperRuntime.IsAcceptableZeroRpmIdle(
            Capability(),
            commandedDutyPercent: 20,
            Verification(observed: 0),
            Assignment(CoolingOutputRole.CaseFan)));
    }

    [Fact]
    public void AnUnassignedOutputStoppedAtALowDutyIsAcceptedAsZeroRpmIdle()
    {
        // No persisted role at all is the common state for a freshly discovered case
        // header; absence of a role must not be read as "safety critical".
        Assert.True(PCHelperRuntime.IsAcceptableZeroRpmIdle(
            Capability(),
            commandedDutyPercent: 20,
            Verification(observed: 0),
            assignment: null));
    }

    [Theory]
    [InlineData(CoolingOutputRole.Pump)]
    [InlineData(CoolingOutputRole.CpuFan)]
    public void ASafetyCriticalOutputIsNeverExcusedForSittingStopped(CoolingOutputRole role)
    {
        // The whole point of the exclusion: a stopped pump or CPU fan must still fail
        // closed, exactly as it did before the tolerance existed.
        Assert.False(PCHelperRuntime.IsAcceptableZeroRpmIdle(
            Capability(),
            commandedDutyPercent: 20,
            Verification(observed: 0),
            Assignment(role)));
    }

    [Fact]
    public void AMaximumCommandMustPhysicallyMoveEvenACaseFan()
    {
        // Emergency cooling commands the range maximum. A fan reading 0% there is a
        // genuine fault, never zero-RPM idle, whatever its role.
        Assert.False(PCHelperRuntime.IsAcceptableZeroRpmIdle(
            Capability(),
            commandedDutyPercent: 100,
            Verification(observed: 0),
            Assignment(CoolingOutputRole.CaseFan)));
    }

    [Fact]
    public void APartiallySpinningFanIsAMismatchRatherThanZeroRpmIdle()
    {
        // 35% under a commanded 60% is a fan that is running but not tracking: a real
        // read-back mismatch that must keep failing, not a stopped fan.
        Assert.False(PCHelperRuntime.IsAcceptableZeroRpmIdle(
            Capability(),
            commandedDutyPercent: 60,
            Verification(observed: 35),
            Assignment(CoolingOutputRole.CaseFan)));
    }

    [Fact]
    public void AnUnavailableReadBackIsNotTreatedAsAStoppedFan()
    {
        // No observed value means the state is unknown, which must fail closed rather
        // than be optimistically read as "the fan is simply idle".
        Assert.False(PCHelperRuntime.IsAcceptableZeroRpmIdle(
            Capability(),
            commandedDutyPercent: 20,
            new ActionVerification("cooling", false, null, "read-back unavailable"),
            Assignment(CoolingOutputRole.CaseFan)));
    }

    private static CapabilityDescriptor Capability() => new(
        CapabilityId,
        "librehardwaremonitor",
        "lhm.device:/lpc/nct6798d/0",
        "Fan #2",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.AdapterHost,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "%",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "Case fan duty",
        CanResetToDefault: true,
        Domain: ControlDomain.Cooling);

    private static ActionVerification Verification(double observed) =>
        new("cooling", false, ControlValue.FromNumeric(observed), $"read-back {observed}%");

    private static CoolingOutputAssignmentV1 Assignment(CoolingOutputRole role) => new(
        CoolingOutputAssignmentV1.CurrentSchemaVersion,
        CapabilityId,
        CapabilityId,
        "librehardwaremonitor",
        "lhm.device:/lpc/nct6798d/0",
        "lhm.sensor:/lpc/nct6798d/0/fan/1",
        "CASE_FAN_2",
        role,
        DateTimeOffset.UnixEpoch,
        null);
}
