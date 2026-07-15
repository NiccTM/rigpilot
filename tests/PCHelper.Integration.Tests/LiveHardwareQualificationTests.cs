using System.Security.Principal;
using System.Text.Json;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class LiveHardwareQualificationTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task RepeatedProbesRetainPreviouslyDiscoveredCoolingControls()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCHELPER_LIVE_HARDWARE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        await using AdapterHostProxy proxy = new();
        AdapterProbeResult initial = await proxy.ProbeAsync(CancellationToken.None);
        string[] initialControlIds = initial.Capabilities
            .Where(capability => capability.Domain == ControlDomain.Cooling
                && capability.Id.StartsWith("lhm.control:", StringComparison.Ordinal))
            .Select(capability => capability.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (initialControlIds.Length == 0)
        {
            return;
        }

        for (int sample = 0; sample < 5; sample++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            _ = await proxy.ReadSensorsAsync(CancellationToken.None);
            AdapterProbeResult next = await proxy.ProbeAsync(CancellationToken.None);
            HashSet<string> nextControlIds = next.Capabilities
                .Where(capability => capability.Domain == ControlDomain.Cooling)
                .Select(capability => capability.Id)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(initialControlIds, id => Assert.Contains(id, nextControlIds));
        }
    }

    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task DirectLibreHardwareMonitorPrepareDoesNotWriteSelectedController()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCHELPER_LIVE_NO_WRITE_PREPARE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        string capabilityId = Environment.GetEnvironmentVariable("PCHELPER_LIVE_NO_WRITE_CAPABILITY")
            ?? throw new InvalidOperationException("PCHELPER_LIVE_NO_WRITE_CAPABILITY is required.");
        string reportPath = Environment.GetEnvironmentVariable("PCHELPER_LIVE_NO_WRITE_REPORT")
            ?? throw new InvalidOperationException("PCHELPER_LIVE_NO_WRITE_REPORT is required.");

        await using LibreHardwareMonitorAdapter adapter = new();
        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        CapabilityDescriptor capability = probe.Capabilities.Single(item => item.Id == capabilityId);
        Assert.Equal(ControlDomain.Cooling, capability.Domain);
        Assert.NotNull(capability.Range);

        try
        {
            PreparedAction prepared = await FanCommissioningWorkflow.PrepareIdentificationPulseAsync(
                capability,
                adapter,
                CancellationToken.None);
            Assert.Equal(capability.Id, prepared.Action.CapabilityId);
            Assert.True(prepared.Action.Value.Numeric >= capability.Range!.Minimum);
            Assert.True(prepared.Action.Value.Numeric <= capability.Range.Maximum);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                CapturedAt = DateTimeOffset.UtcNow,
                CapabilityId = capability.Id,
                Prepared = true,
                ApplyIssued = false,
                VerifyIssued = false,
                RollbackIssued = false,
                ResetIssued = false,
                RequestedDuty = prepared.Action.Value.Numeric,
                PreviousValue = prepared.PreviousValue?.Numeric
            }, JsonDefaults.Options));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                CapturedAt = DateTimeOffset.UtcNow,
                CapabilityId = capability.Id,
                Prepared = false,
                ApplyIssued = false,
                VerifyIssued = false,
                RollbackIssued = false,
                ResetIssued = false,
                ExceptionType = exception.GetType().Name,
                HResult = exception.HResult
            }, JsonDefaults.Options));
            throw;
        }
    }

    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task ResetHighDutyReadBackAndFirmwareReturnOnExplicitlyAuthorisedMachine()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCHELPER_LIVE_HARDWARE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        string reportPath = Environment.GetEnvironmentVariable("PCHELPER_LIVE_HARDWARE_REPORT")
            ?? throw new InvalidOperationException("PCHELPER_LIVE_HARDWARE_REPORT is required.");
        List<object> results = [];
        await using AdapterHostProxy proxy = new();
        AdapterProbeResult probe = await proxy.ProbeAsync(CancellationToken.None);
        IReadOnlyList<CapabilityDescriptor> controls = probe.Capabilities
            .Where(capability => capability.Domain == ControlDomain.Cooling
                && capability.ValueKind == ControlValueKind.Numeric
                && capability.CanResetToDefault)
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(controls);

        try
        {
            foreach (CapabilityDescriptor control in controls)
            {
                await proxy.ResetToDefaultAsync(control.Id, CancellationToken.None);
            }
            await Task.Delay(TimeSpan.FromSeconds(2));

            foreach (CapabilityDescriptor control in controls)
            {
                IReadOnlyList<SensorSample> before = await proxy.ReadSensorsAsync(CancellationToken.None);
                SensorSample? rpmSensor = before.FirstOrDefault(sensor =>
                    sensor.DeviceId == control.DeviceId
                    && sensor.Unit.Equals("RPM", StringComparison.OrdinalIgnoreCase)
                    && sensor.Name.Equals(control.Name, StringComparison.Ordinal));
                SensorSample? controlBefore = FindControlSensor(before, control);
                bool gpu = control.DeviceId.Contains("gpu", StringComparison.OrdinalIgnoreCase);
                bool testHighDuty = gpu || rpmSensor?.Value is > 100;
                if (!testHighDuty)
                {
                    results.Add(new
                    {
                        control.Id,
                        control.Name,
                        control.DeviceId,
                        Mode = "ResetOnly",
                        ControlBefore = controlBefore?.Value,
                        RpmBefore = rpmSensor?.Value,
                        ResetSucceeded = true
                    });
                    continue;
                }

                ProfileAction action = new(
                    $"live-high-duty:{Guid.NewGuid():N}",
                    control.AdapterId,
                    control.Id,
                    ControlValue.FromNumeric(control.Range!.Maximum),
                    Required: true,
                    Order: 0);
                PreparedAction prepared = await proxy.PrepareAsync(action, CancellationToken.None);
                try
                {
                    await proxy.ApplyAsync(prepared, CancellationToken.None);
                    ActionVerification verification = await proxy.VerifyAsync(prepared, CancellationToken.None);
                    Assert.True(verification.Success, verification.Message);
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    IReadOnlyList<SensorSample> atMaximum = await proxy.ReadSensorsAsync(CancellationToken.None);
                    SensorSample? rpmAtMaximum = atMaximum.FirstOrDefault(sensor => sensor.SensorId == rpmSensor?.SensorId);
                    if (gpu || rpmSensor?.Value is > 100)
                    {
                        Assert.True(rpmAtMaximum?.Value is > 100, $"{control.Name} did not report physical RPM at maximum duty.");
                    }
                    results.Add(new
                    {
                        control.Id,
                        control.Name,
                        control.DeviceId,
                        Mode = "HighDutyReadBack",
                        ControlBefore = controlBefore?.Value,
                        RpmBefore = rpmSensor?.Value,
                        Verification = verification.Message,
                        RpmAtMaximum = rpmAtMaximum?.Value
                    });
                }
                finally
                {
                    await proxy.RollbackAsync(prepared, CancellationToken.None);
                    await proxy.ResetToDefaultAsync(control.Id, CancellationToken.None);
                }
            }
        }
        finally
        {
            foreach (CapabilityDescriptor control in controls)
            {
                await proxy.ResetToDefaultAsync(control.Id, CancellationToken.None);
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
            IReadOnlyList<SensorSample> finalSensors = await proxy.ReadSensorsAsync(CancellationToken.None);
            var report = new
            {
                CapturedAt = DateTimeOffset.UtcNow,
                BoardControls = controls.Select(control => new
                {
                    control.Id,
                    control.Name,
                    control.DeviceId,
                    control.State,
                    control.Evidence,
                    control.CanResetToDefault
                }),
                Results = results,
                FinalControlSensors = controls.Select(control => new
                {
                    control.Id,
                    Value = FindControlSensor(finalSensors, control)?.Value,
                    Quality = FindControlSensor(finalSensors, control)?.Quality
                })
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonDefaults.Options));
        }
    }

    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task CalibratesBothRtx3090FansWithinAdapterBoundsAndRestoresFirmwareMode()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCHELPER_LIVE_HARDWARE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string baseReportPath = Environment.GetEnvironmentVariable("PCHELPER_LIVE_HARDWARE_REPORT")
            ?? throw new InvalidOperationException("PCHELPER_LIVE_HARDWARE_REPORT is required.");
        string reportPath = Path.ChangeExtension(baseReportPath, ".gpu-calibration.json");
        List<FanCalibrationResult> results = [];
        await using AdapterHostProxy proxy = new();
        AdapterProbeResult probe = await proxy.ProbeAsync(CancellationToken.None);
        CapabilityDescriptor[] gpuFans = probe.Capabilities
            .Where(capability => capability.DeviceId == "lhm.device:/gpu-nvidia/0"
                && capability.Domain == ControlDomain.Cooling
                && capability.CanResetToDefault)
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, gpuFans.Length);

        try
        {
            foreach (CapabilityDescriptor fan in gpuFans)
            {
                await proxy.ResetToDefaultAsync(fan.Id, CancellationToken.None);
                IReadOnlyList<SensorSample> sensors = await proxy.ReadSensorsAsync(CancellationToken.None);
                SensorSample[] gpuTemperatures = sensors.Where(sensor =>
                        sensor.DeviceId == fan.DeviceId
                        && sensor.Unit == "°C"
                        && sensor.Quality == SensorQuality.Good
                        && sensor.Value.HasValue
                        && sensor.Name is "GPU Core" or "GPU Hot Spot" or "GPU Memory Junction")
                    .ToArray();
                Assert.NotEmpty(gpuTemperatures);
                foreach (SensorSample temperature in gpuTemperatures)
                {
                    double precondition = temperature.Name == "GPU Memory Junction" ? 85
                        : temperature.Name == "GPU Hot Spot" ? 75
                        : 70;
                    Assert.True(
                        temperature.Value <= precondition,
                        $"GPU fan-stop calibration requires thermal headroom; {temperature.Name} was {temperature.Value:0.0} °C (precondition {precondition:0.0} °C)." );
                }
                SensorSample rpm = sensors.Single(sensor =>
                    sensor.DeviceId == fan.DeviceId
                    && sensor.Unit == "RPM"
                    && sensor.Name == fan.Name);
                StartCalibrationRequest request = new(
                    fan.Id,
                    rpm.SensorId,
                    ConfirmExperimental: true,
                    ConfirmDevice: true,
                    AllowFanStop: true,
                    SettlingTime: TimeSpan.FromSeconds(3),
                    StableSampleCount: 3,
                    MaximumSampleCount: 15,
                    SampleInterval: TimeSpan.FromMilliseconds(500),
                    StabilityTolerancePercent: 10,
                    RestartVerificationCycles: 2,
                    TemperatureLimits: gpuTemperatures.Select(temperature => new FanCalibrationTemperatureLimit(
                        temperature.SensorId,
                        temperature.Name == "GPU Memory Junction" ? 90
                            : temperature.Name == "GPU Hot Spot" ? 85
                            : 80)).ToArray());
                FanCalibrationResult result = await new FanCalibrationEngine().RunAsync(
                    request,
                    fan,
                    proxy,
                    reportProgress: null,
                    CancellationToken.None);
                Assert.True(result.MaximumRpm > 100);
                Assert.True(result.MinimumDutyPercent >= fan.Range!.Minimum);
                Assert.Contains(result.Measurements, point => point.DutyPercent == fan.Range.Minimum);
                Assert.True(result.RestartVerified);
                Assert.Equal(2, result.RestartVerificationCyclesCompleted);
                results.Add(result);
            }
        }
        finally
        {
            foreach (CapabilityDescriptor fan in gpuFans)
            {
                await proxy.ResetToDefaultAsync(fan.Id, CancellationToken.None);
            }
        }

        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Device = "NVIDIA GeForce RTX 3090",
            Results = results
        }, JsonDefaults.Options));
    }

    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task AppliesReadsBackAndRollsBackWindowsPowerScheme()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCHELPER_LIVE_HARDWARE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string baseReportPath = Environment.GetEnvironmentVariable("PCHELPER_LIVE_HARDWARE_REPORT")
            ?? throw new InvalidOperationException("PCHELPER_LIVE_HARDWARE_REPORT is required.");
        string reportPath = Path.ChangeExtension(baseReportPath, ".power.json");
        await using WindowsPowerAdapter adapter = new();
        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None);
        CapabilityDescriptor capability = Assert.Single(probe.Capabilities);
        Assert.Equal(CapabilityAccessState.Verified, capability.State);
        string[] schemes = probe.Devices.Single().Properties["availableSchemes"]
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(schemes);
        string active = (await adapter.GetHealthAsync(CancellationToken.None)).Message.Split(": ")[1];
        string requested = schemes.FirstOrDefault(scheme => !scheme.Equals(active, StringComparison.OrdinalIgnoreCase)) ?? active;
        ProfileAction action = new(
            $"live-power:{Guid.NewGuid():N}",
            capability.AdapterId,
            capability.Id,
            ControlValue.FromText(requested),
            Required: true,
            Order: 0);
        PreparedAction prepared = await adapter.PrepareAsync(action, CancellationToken.None);
        ActionVerification applied;
        ActionVerification restored;
        try
        {
            await adapter.ApplyAsync(prepared, CancellationToken.None);
            applied = await adapter.VerifyAsync(prepared, CancellationToken.None);
            Assert.True(applied.Success, applied.Message);
        }
        finally
        {
            await adapter.RollbackAsync(prepared, CancellationToken.None);
        }
        PreparedAction restoreCheck = prepared with
        {
            Action = prepared.Action with { Value = prepared.PreviousValue! }
        };
        restored = await adapter.VerifyAsync(restoreCheck, CancellationToken.None);
        Assert.True(restored.Success, restored.Message);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            CapturedAt = DateTimeOffset.UtcNow,
            OriginalScheme = prepared.PreviousValue?.Text,
            RequestedScheme = requested,
            Applied = applied,
            Restored = restored
        }, JsonDefaults.Options));
    }

    private static SensorSample? FindControlSensor(
        IReadOnlyList<SensorSample> sensors,
        CapabilityDescriptor control) => sensors.FirstOrDefault(sensor =>
            sensor.SensorId == control.Id.Replace("lhm.control:", "lhm.sensor:", StringComparison.Ordinal));
}
