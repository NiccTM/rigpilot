using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class FanCalibrationPolicyTests
{
    [Fact]
    public void NonStoppingCalibrationAllowsOnlyMeasuredNonzeroGraphFloor()
    {
        FanCalibrationV2 calibration = NonStoppingCalibration();
        CoolingGraphOutputV1 zeroFloor = Output(minimum: 0, maximum: 100);
        CoolingGraphOutputV1 lowFloor = Output(minimum: 5, maximum: 100);
        CoolingGraphOutputV1 measuredFloor = Output(minimum: 10, maximum: 100);

        Assert.True(FanCalibrationPolicy.SupportsNonZeroCurve(calibration));
        Assert.False(FanCalibrationPolicy.SupportsVerifiedFanStop(calibration));
        Assert.Contains("zero", FanCalibrationPolicy.ValidateOutput(zeroFloor, calibration)!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("below", FanCalibrationPolicy.ValidateOutput(lowFloor, calibration)!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(FanCalibrationPolicy.ValidateOutput(measuredFloor, calibration));
    }

    [Fact]
    public void VerifiedStopCalibrationAllowsExactZeroAndClampsUnsafePositiveCommands()
    {
        FanCalibrationV2 calibration = NonStoppingCalibration() with
        {
            StallDutyPercent = 0,
            RestartDutyPercent = 20,
            SupportsVerifiedFanStop = true,
            NonStopFloorObserved = false
        };

        Assert.True(FanCalibrationPolicy.SupportsVerifiedFanStop(calibration));
        Assert.Null(FanCalibrationPolicy.ValidateOutput(Output(minimum: 0, maximum: 100), calibration));
        Assert.Equal(0, FanCalibrationPolicy.EnforceSafeDuty(0, calibration));
        Assert.Equal(10, FanCalibrationPolicy.EnforceSafeDuty(1, calibration));
        Assert.Equal(45, FanCalibrationPolicy.EnforceSafeDuty(45, calibration));
    }

    [Fact]
    public void SchemaTwoRestartEvidenceRemainsUsableDuringUpgrade()
    {
        FanCalibrationV2 calibration = NonStoppingCalibration() with
        {
            SchemaVersion = 2,
            StallDutyPercent = 0,
            RestartDutyPercent = 20,
            SupportsVerifiedFanStop = false,
            NonStopFloorObserved = false
        };

        Assert.True(FanCalibrationPolicy.SupportsVerifiedFanStop(calibration));
        Assert.True(FanCalibrationPolicy.SupportsNonZeroCurve(calibration));
    }

    private static FanCalibrationV2 NonStoppingCalibration() => new(
        FanCalibrationV2.CurrentSchemaVersion,
        "fan.case1",
        "fan.case1.rpm",
        [
            new FanCalibrationPoint(0, 540),
            new FanCalibrationPoint(20, 540),
            new FanCalibrationPoint(25, 630),
            new FanCalibrationPoint(100, 1_980)
        ],
        1_980,
        StallDutyPercent: null,
        RestartDutyPercent: null,
        MinimumDutyPercent: 10,
        KickStartDutyPercent: 10,
        [],
        DateTimeOffset.UtcNow,
        CommissioningSessionId: "commission.case1",
        EffectiveFloorDutyPercent: 20,
        EffectiveFloorRpm: 540,
        FirstResponsiveDutyPercent: 25,
        NonStopFloorObserved: true,
        SupportsVerifiedFanStop: false);

    private static CoolingGraphOutputV1 Output(double minimum, double maximum) => new(
        "fan.case1",
        "curve",
        FanOutputMode.DutyPercent,
        minimum,
        maximum,
        0,
        100,
        100,
        []);
}
