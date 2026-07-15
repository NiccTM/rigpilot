using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class HardwareOperationsTests
{
    [Fact]
    public void ExperimentalOperationRequiresGlobalAndDeviceAcknowledgement()
    {
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);

        HardwareOperationEligibility missingDevice = HardwareOperationEligibilityEvaluator.ForCalibration(
            capability,
            confirmExperimental: true,
            confirmDevice: false);
        HardwareOperationEligibility confirmed = HardwareOperationEligibilityEvaluator.ForCalibration(
            capability,
            confirmExperimental: true,
            confirmDevice: true);

        Assert.False(missingDevice.Eligible);
        Assert.Contains("exact-device", missingDevice.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(confirmed.Eligible);
    }

    [Fact]
    public void AutomaticVoltageTuningIsRejected()
    {
        CapabilityDescriptor voltage = Capability(CapabilityAccessState.Experimental) with
        {
            Name = "Core voltage",
            Unit = "mV",
            Domain = ControlDomain.Cpu
        };
        TunePlan plan = Plan(voltage);

        HardwareOperationEligibility result = HardwareOperationEligibilityEvaluator.ForTuning(
            voltage,
            plan,
            confirmExperimental: true,
            confirmDevice: true);

        Assert.False(result.Eligible);
        Assert.Contains("voltage", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateGeneratorStaysInsideBoundsAndHonoursDirection()
    {
        IReadOnlyList<double> candidates = TuneCandidateGenerator.Generate(
            new NumericRange(0, 100, 1),
            new TuneBounds(20, 80, 5),
            TuneDirection.Minimize,
            maximumCandidates: 5);

        Assert.Equal(80, candidates[0]);
        Assert.Equal(20, candidates[^1]);
        Assert.Equal(5, candidates.Count);
        Assert.All(candidates, value => Assert.InRange(value, 20, 80));
    }

    [Fact]
    public async Task CalibrationFindsStallAndRestartThenRestoresPreviousState()
    {
        FakeNumericAdapter adapter = new(initialValue: 40, rpmForDuty: duty => duty < 20 ? 0 : duty * 20);
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: true,
            SettlingTime: TimeSpan.Zero);

        FanCalibrationResult result = await engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None);

        Assert.Equal(2_000, result.MaximumRpm);
        Assert.Equal(15, result.StallDutyPercent);
        Assert.Equal(0, result.VerifiedStopDutyPercent);
        Assert.Equal(20, result.RestartDutyPercent);
        Assert.Equal(25, result.MinimumDutyPercent);
        Assert.True(result.RestartVerified);
        Assert.Equal(2, result.RestartVerificationCyclesCompleted);
        Assert.Equal(3, result.StableSampleCount);
        Assert.All(result.Measurements, point => Assert.True(point.Stable));
        Assert.Equal(40, adapter.CurrentValue);
        Assert.True(adapter.RollbackCalled);
    }

    [Fact]
    public async Task CalibrationWaitsForStableRpmWindowAndIgnoresTransientReadings()
    {
        FakeNumericAdapter adapter = new(
            initialValue: 40,
            rpmForDuty: duty => duty < 20 ? 0 : duty * 20,
            rpmForDutyAndRead: (duty, read) => duty < 20
                ? 0
                : read switch
                {
                    1 => duty * 10,
                    2 => duty * 15,
                    _ => duty * 20
                });
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: true,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 7,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2);

        FanCalibrationResult result = await engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None);

        Assert.True(result.RestartVerified);
        Assert.Equal(20, result.RestartDutyPercent);
        Assert.Equal(25, result.MinimumDutyPercent);
        Assert.Equal(2_000, result.MaximumRpm);
        Assert.Contains(result.Measurements, point => point.SampleCount > request.StableSampleCount);
        Assert.Equal(40, adapter.CurrentValue);
    }

    [Fact]
    public async Task UnstableRpmWindowFailsSafeAndRestoresFirmwareDefault()
    {
        FakeNumericAdapter adapter = new(
            initialValue: 40,
            rpmForDuty: _ => 1_000,
            rpmForDutyAndRead: (_, read) => read % 2 == 0 ? 500 : 1_500);
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: false,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 6,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2);

        HardwareSafetyException exception = await Assert.ThrowsAsync<HardwareSafetyException>(() => engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None));

        Assert.Contains("did not settle", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.ResetCalled);
        Assert.Equal(40, adapter.CurrentValue);
    }

    [Fact]
    public async Task RestartSearchPromotesCandidateThatCannotRepeatAcrossCycles()
    {
        int twentyPercentAttempts = 0;
        FakeNumericAdapter adapter = new(
            initialValue: 40,
            rpmForDuty: duty => duty < 20 ? 0 : duty * 20,
            rpmForDutyAndRead: (duty, read) =>
            {
                if (Math.Abs(duty - 20) < 0.001 && read == 1)
                {
                    twentyPercentAttempts++;
                }

                if (duty < 20)
                {
                    return 0;
                }

                return Math.Abs(duty - 20) < 0.001 && twentyPercentAttempts > 2
                    ? 0
                    : duty * 20;
            });
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: true,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 6,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2);

        FanCalibrationResult result = await engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None);

        Assert.True(result.RestartVerified);
        Assert.Equal(25, result.RestartDutyPercent);
        Assert.Equal(30, result.MinimumDutyPercent);
        Assert.Equal(2, result.RestartVerificationCyclesCompleted);
        Assert.Contains(result.Measurements, point => point.DutyPercent == 100 && point.Rpm >= 100);
        Assert.Equal(40, adapter.CurrentValue);
    }

    [Fact]
    public async Task RestartVerificationUsesLowestConfirmedStopDutyAfterHysteresis()
    {
        bool stallObserved = false;
        bool restartPhase = false;
        FakeNumericAdapter adapter = new(
            initialValue: 40,
            rpmForDuty: duty => duty < 20 ? 0 : duty * 20,
            rpmForDutyAndRead: (duty, _) =>
            {
                if (duty <= 15)
                {
                    stallObserved = true;
                }

                if (stallObserved && duty >= 20)
                {
                    restartPhase = true;
                }

                if (duty <= 10)
                {
                    return 0;
                }

                if (duty <= 15)
                {
                    return restartPhase ? 400 : 0;
                }

                return duty * 20;
            });
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: true,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 6,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2);

        FanCalibrationResult result = await engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None);

        Assert.Equal(15, result.StallDutyPercent);
        Assert.Equal(0, result.VerifiedStopDutyPercent);
        Assert.Equal(20, result.RestartDutyPercent);
        Assert.True(result.RestartVerified);
        Assert.Equal(40, adapter.CurrentValue);
    }

    [Fact]
    public async Task CalibrationTemperatureCeilingForcesFirmwareRecovery()
    {
        FakeNumericAdapter adapter = new(
            initialValue: 40,
            rpmForDuty: duty => duty * 20,
            temperatureForDuty: duty => duty <= 25 ? 86 : 60);
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: false,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 6,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2,
            TemperatureLimits: [new(FakeNumericAdapter.TemperatureSensorId, 85)]);

        HardwareSafetyException exception = await Assert.ThrowsAsync<HardwareSafetyException>(() => engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None));

        Assert.Contains("Temperature safety ceiling", exception.Message, StringComparison.Ordinal);
        Assert.True(adapter.ResetCalled);
        Assert.Equal(40, adapter.CurrentValue);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(45)]
    public async Task CalibrationRoundsUnverifiedRunningFloorToConservativeTenPercentBand(double lowestRunningDuty)
    {
        FakeNumericAdapter adapter = new(
            initialValue: 50,
            rpmForDuty: duty => duty < lowestRunningDuty ? 0 : duty * 20);
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental) with
        {
            Range = new NumericRange(30, 100, 1)
        };
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: false,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 6,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2);

        FanCalibrationResult result = await engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None);

        Assert.False(result.RestartVerified);
        Assert.Equal(50, result.MinimumDutyPercent);
        Assert.Equal(50, adapter.CurrentValue);
    }

    [Fact]
    public async Task CalibrationLeavesFlatTachometerUnqualifiedAndRestoresPriorPolicy()
    {
        FakeNumericAdapter adapter = new(initialValue: 50, rpmForDuty: _ => 540);
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: true,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 6,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2);

        FanCalibrationResult result = await engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None);

        Assert.Null(result.StallDutyPercent);
        Assert.Null(result.RestartDutyPercent);
        Assert.False(result.RestartVerified);
        Assert.False(result.NonStopFloorObserved);
        Assert.Null(result.EffectiveFloorDutyPercent);
        Assert.False(FanCalibrationPolicy.SupportsNonZeroCurve(result));
        Assert.Equal(0, result.RestartVerificationCyclesCompleted);
        Assert.Contains(result.Measurements, point => point.DutyPercent == 0 && point.Rpm == 540);
        Assert.Equal(50, adapter.CurrentValue);
    }

    [Fact]
    public async Task CalibrationCharacterizesNonStoppingControllerWithAdaptiveNonzeroFloor()
    {
        FakeNumericAdapter adapter = new(
            initialValue: 50,
            rpmForDuty: duty => duty <= 20 ? 540 : 540 + ((duty - 20) * 18));
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: true,
            SettlingTime: TimeSpan.Zero,
            StableSampleCount: 3,
            MaximumSampleCount: 6,
            SampleInterval: TimeSpan.Zero,
            StabilityTolerancePercent: 5,
            RestartVerificationCycles: 2);

        FanCalibrationResult result = await engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None);

        Assert.Null(result.StallDutyPercent);
        Assert.Null(result.RestartDutyPercent);
        Assert.False(result.RestartVerified);
        Assert.True(result.NonStopFloorObserved);
        Assert.Equal(20, result.EffectiveFloorDutyPercent);
        Assert.Equal(540, result.EffectiveFloorRpm);
        Assert.Equal(25, result.FirstResponsiveDutyPercent);
        Assert.Equal(10, result.MinimumDutyPercent);
        Assert.True(FanCalibrationPolicy.SupportsNonZeroCurve(result));
        Assert.False(FanCalibrationPolicy.SupportsVerifiedFanStop(result));
        Assert.Equal(50, adapter.CurrentValue);
    }

    [Fact]
    public async Task TuneStopsAtRejectedCandidateGeneratesProvisionalProfileAndRollsBack()
    {
        FakeNumericAdapter adapter = new(initialValue: 100, rpmForDuty: duty => duty * 20);
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        TunePlan plan = Plan(capability) with
        {
            Bounds = new Dictionary<string, TuneBounds> { [capability.Id] = new(50, 100, 25) },
            ScreeningDuration = TimeSpan.FromMinutes(10)
        };
        StartTuneRequest request = new(
            plan,
            capability.Id,
            TuneDirection.Minimize,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            CandidateScreeningTime: TimeSpan.Zero,
            MaximumCandidates: 3);
        QueueScreeningMonitor monitor = new(
            Pass("candidate 100 passed"),
            Pass("candidate 75 passed"),
            Reject("candidate 50 rejected"),
            Pass("final passed"));

        TuneResult result = await HardwareTuneEngine.RunAsync(
            request,
            capability,
            adapter,
            monitor,
            reportProgress: null,
            CancellationToken.None);

        Assert.Equal("Passed 10-minute screening", result.StatusLabel);
        Assert.Equal(75, result.SelectedValue);
        Assert.NotNull(result.GeneratedProfile);
        Assert.True(result.GeneratedProfile.IsExperimental);
        Assert.Equal(75, result.GeneratedProfile.Actions.Single().Value.Numeric);
        Assert.Equal(100, adapter.CurrentValue);
        Assert.True(adapter.RollbackCalled);
    }

    [Fact]
    public async Task InvalidRpmSourceForcesFirmwareDefaultRecovery()
    {
        FakeNumericAdapter adapter = new(initialValue: 40, rpmForDuty: _ => double.NaN);
        CapabilityDescriptor capability = Capability(CapabilityAccessState.Experimental);
        FanCalibrationEngine engine = new((_, _) => Task.CompletedTask);
        StartCalibrationRequest request = new(
            capability.Id,
            FakeNumericAdapter.RpmSensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: false,
            SettlingTime: TimeSpan.Zero);

        await Assert.ThrowsAsync<HardwareSafetyException>(() => engine.RunAsync(
            request,
            capability,
            adapter,
            reportProgress: null,
            CancellationToken.None));

        Assert.True(adapter.ResetCalled);
    }

    private static CapabilityDescriptor Capability(CapabilityAccessState state) => new(
        "fan.control",
        "fake",
        "device:fan",
        "Case fan",
        state,
        AdapterExecutionContext.AdapterHost,
        ControlValueKind.Numeric,
        new NumericRange(0, 100, 1),
        "%",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "Fake bounded control.",
        CanResetToDefault: true,
        Domain: ControlDomain.Cooling);

    private static TunePlan Plan(CapabilityDescriptor capability) => new(
        "plan",
        capability.DeviceId,
        TuningObjective.Quiet,
        new Dictionary<string, TuneBounds> { [capability.Id] = new(0, 100, 5) },
        TimeSpan.FromMinutes(10),
        TemperatureCeilingCelsius: 85,
        PowerCeilingWatts: null,
        Provisional: true,
        SoakStartedAt: null,
        ActiveUseRequired: TimeSpan.FromHours(10),
        ColdBootsRequired: 3);

    private static TuneScreeningResult Pass(string message) => new(true, message, 60, 100, 1_500);

    private static TuneScreeningResult Reject(string message) => new(false, message, 90, 120, 1_200);

    private sealed class QueueScreeningMonitor(params TuneScreeningResult[] results) : ITuneScreeningMonitor
    {
        private readonly Queue<TuneScreeningResult> _results = new(results);

        public Task<TuneScreeningResult> ScreenAsync(
            CapabilityDescriptor capability,
            TunePlan plan,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeNumericAdapter(
        double initialValue,
        Func<double, double> rpmForDuty,
        Func<double, int, double>? rpmForDutyAndRead = null,
        Func<double, double>? temperatureForDuty = null) : IHardwareAdapter
    {
        public const string RpmSensorId = "sensor:rpm";
        public const string TemperatureSensorId = "sensor:temperature";
        private readonly Func<double, double> _rpmForDuty = rpmForDuty;
        private readonly Func<double, int, double>? _rpmForDutyAndRead = rpmForDutyAndRead;
        private readonly Func<double, double>? _temperatureForDuty = temperatureForDuty;
        private readonly double _initialValue = initialValue;
        private int _readsAtCurrentDuty;

        public double CurrentValue { get; private set; } = initialValue;

        public bool RollbackCalled { get; private set; }

        public bool ResetCalled { get; private set; }

        public AdapterManifest Manifest { get; } = new(
            "fake",
            "Fake adapter",
            "1.0",
            "GPL-3.0-only",
            null,
            AdapterExecutionContext.AdapterHost,
            ["fake"],
            ["Cooling"]);

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<SensorSample> samples = [new(
                RpmSensorId,
                Manifest.Id,
                "device:fan",
                "Case fan",
                DateTimeOffset.UtcNow,
                _rpmForDutyAndRead?.Invoke(CurrentValue, ++_readsAtCurrentDuty) ?? _rpmForDuty(CurrentValue),
                "RPM",
                SensorQuality.Good,
                TimeSpan.Zero)];
            if (_temperatureForDuty is not null)
            {
                samples.Add(new SensorSample(
                    TemperatureSensorId,
                    Manifest.Id,
                    "device:fan",
                    "GPU Core",
                    DateTimeOffset.UtcNow,
                    _temperatureForDuty(CurrentValue),
                    "°C",
                    SensorQuality.Good,
                    TimeSpan.Zero));
            }

            return Task.FromResult<IReadOnlyList<SensorSample>>(samples);
        }

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PreparedAction(
                action,
                ControlValue.FromNumeric(CurrentValue),
                DateTimeOffset.UtcNow,
                "fake"));
        }

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentValue = action.Action.Value.Numeric!.Value;
            _readsAtCurrentDuty = 0;
            return Task.CompletedTask;
        }

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double expected = action.Action.Value.Numeric!.Value;
            return Task.FromResult(new ActionVerification(
                action.Action.Id,
                Math.Abs(CurrentValue - expected) < 0.001,
                ControlValue.FromNumeric(CurrentValue),
                "fake read-back"));
        }

        public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            RollbackCalled = true;
            CurrentValue = action.PreviousValue?.Numeric ?? _initialValue;
            return Task.CompletedTask;
        }

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
        {
            ResetCalled = true;
            CurrentValue = _initialValue;
            return Task.CompletedTask;
        }

        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "healthy", []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
