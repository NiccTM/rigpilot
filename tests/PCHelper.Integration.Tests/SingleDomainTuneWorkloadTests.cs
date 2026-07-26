using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The single-domain "Auto OC core" / "Auto OC memory" buttons routed to
/// <c>StartTune</c> without ever starting a workload. The screening monitor
/// correctly refuses to certify a candidate that never reached the required
/// measured device load, so every run ended in "no candidate passed screening"
/// — a real refusal that read to the operator as the feature silently doing
/// nothing. The request now carries the same isolated exact-device workload
/// host the composite Auto OC uses, and a CPU/GPU tune without one is refused
/// up front with a reason instead of after a search that cannot succeed.
/// </summary>
public sealed class SingleDomainTuneWorkloadTests
{
    [Theory]
    [InlineData(ControlDomain.Gpu)]
    [InlineData(ControlDomain.Cpu)]
    public void TuningAProcessorControlWithoutAWorkloadIsRefusedUpFront(ControlDomain domain)
    {
        (string Code, string Reason)? failure = PCHelperRuntime.ValidateTuneWorkload(
            Capability(domain), Request(workloadHost: null, workloadMode: null));

        Assert.NotNull(failure);
        Assert.Equal("TUNE_WORKLOAD_REQUIRED", failure!.Value.Code);
    }

    [Fact]
    public void CoolingCalibrationStillRunsWithoutAWorkload()
    {
        // An unloaded search is the correct shape for a fan curve — this must
        // not become collateral damage of the processor-domain requirement.
        Assert.Null(PCHelperRuntime.ValidateTuneWorkload(
            Capability(ControlDomain.Cooling), Request(workloadHost: null, workloadMode: null)));
    }

    [Fact]
    public void AWorkloadTargetingADifferentDeviceIsRefused()
    {
        // Load on the wrong GPU would certify an offset on a card that was never
        // exercised — the exact failure the exact-device binding exists to stop.
        (string Code, string Reason)? failure = PCHelperRuntime.ValidateTuneWorkload(
            Capability(ControlDomain.Gpu, deviceId: "nvidia:gpu-0"),
            Request(Descriptor("nvidia:gpu-1"), AutoOcWorkloadMode.Core));

        Assert.NotNull(failure);
        Assert.Equal("TUNE_WORKLOAD_TARGET_INVALID", failure!.Value.Code);
    }

    [Fact]
    public void AWorkloadWithNoRunningModeIsRefused()
    {
        // Stopped is a valid enum value but not a valid thing to screen against:
        // it would produce an idle "pass" indistinguishable from the old bug.
        foreach (AutoOcWorkloadMode? mode in new AutoOcWorkloadMode?[] { null, AutoOcWorkloadMode.Stopped })
        {
            (string Code, string Reason)? failure = PCHelperRuntime.ValidateTuneWorkload(
                Capability(ControlDomain.Gpu), Request(Descriptor("nvidia:gpu-0"), mode));

            Assert.NotNull(failure);
            Assert.Equal("TUNE_WORKLOAD_TARGET_INVALID", failure!.Value.Code);
        }
    }

    [Theory]
    [InlineData(AutoOcWorkloadMode.Core)]
    [InlineData(AutoOcWorkloadMode.Memory)]
    public void AnExactDeviceWorkloadInARunningModeIsAccepted(AutoOcWorkloadMode mode)
    {
        Assert.Null(PCHelperRuntime.ValidateTuneWorkload(
            Capability(ControlDomain.Gpu), Request(Descriptor("nvidia:gpu-0"), mode)));
    }

    private static WorkloadHostDescriptorV1 Descriptor(string targetDeviceId) => new(
        WorkloadHostDescriptorV1.CurrentSchemaVersion,
        "session",
        "pipe",
        "token",
        targetDeviceId,
        VendorId: 0x10DE,
        AdapterIndex: 0,
        HostProcessId: 1234);

    private static StartTuneRequest Request(
        WorkloadHostDescriptorV1? workloadHost,
        AutoOcWorkloadMode? workloadMode) => new(
        new TunePlan(
            "plan",
            "nvidia:gpu-0",
            TuningObjective.Performance,
            new Dictionary<string, TuneBounds> { ["control"] = new(0, 100, 15) },
            TimeSpan.FromMinutes(10),
            TemperatureCeilingCelsius: 83,
            PowerCeilingWatts: null,
            Provisional: true,
            SoakStartedAt: null,
            ActiveUseRequired: TimeSpan.FromHours(2),
            ColdBootsRequired: 2),
        "control",
        TuneDirection.Maximize,
        ConfirmExperimental: true,
        ConfirmDevice: true,
        WorkloadHost: workloadHost,
        WorkloadMode: workloadMode);

    private static CapabilityDescriptor Capability(ControlDomain domain, string deviceId = "nvidia:gpu-0") => new(
        "control",
        "adapter",
        deviceId,
        "Control",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "MHz",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "Control",
        CanResetToDefault: true,
        Domain: domain);
}
