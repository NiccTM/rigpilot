using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Proves the per-GPU fan curve binding end to end: a cooling graph whose
/// sensor is the GPU temperature and whose output is the real
/// <see cref="NvidiaGpuFanAdapter"/> capability drives manual duty through the
/// adapter's prepare/apply/verify path exactly the way the service cooling
/// loop does, holds the last good temperature across a brief sensor dropout,
/// commands maximum duty on sustained staleness, and returns the fan to the
/// driver's automatic curve on reset. The GPU is an in-memory fake.
/// </summary>
public sealed class GpuFanCoolingGraphBindingTests
{
    private const string DeviceId = "nvidia:gpu-test";
    private const string ChannelId = "0";
    private const string GpuTemperatureSensorId = "gpu.core.temperature";
    private static string CapabilityId => $"{NvidiaGpuFanAdapter.CapabilityPrefix}{ChannelId}";

    [Fact]
    public async Task GpuTemperatureCurveDrivesManualDutyThroughTheRealAdapter()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(30, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);
        CoolingGraphRuntime runtime = new();
        CoolingGraphV1 graph = GpuFanGraph();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Steady 60 °C for a few ticks lets the slew limiter settle on the curve value.
        double target = 0;
        for (int tick = 0; tick < 3; tick++)
        {
            CoolingGraphRuntimeTick result = runtime.Evaluate(
                graph,
                [GpuSample(now.AddSeconds(tick), 60)],
                new Dictionary<string, FanCalibrationV2>(),
                stalePollLimit: 3,
                now.AddSeconds(tick));
            Assert.False(result.Evaluation.Emergency);
            target = result.Evaluation.OutputValues[CapabilityId];
        }

        // 60 °C on a 30→30 / 90→100 curve interpolates to 65 % duty.
        Assert.Equal(65, target, precision: 5);
        await ApplyLikeTheServiceLoopAsync(adapter, target);
        Assert.Equal([65], transport.ManualDutyCommands);
        Assert.Equal(GpuFanControlPolicy.Manual, transport.State.Policy);
    }

    [Fact]
    public async Task SustainedGpuSensorLossCommandsMaximumDutyAndResetRestoresAutomatic()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(30, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);
        CoolingGraphRuntime runtime = new();
        CoolingGraphV1 graph = GpuFanGraph();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        runtime.Evaluate(graph, [GpuSample(now, 55)], new Dictionary<string, FanCalibrationV2>(), 2, now);
        CoolingGraphRuntimeTick held = runtime.Evaluate(
            graph, [StaleGpuSample(now.AddSeconds(1))], new Dictionary<string, FanCalibrationV2>(), 2, now.AddSeconds(1));
        CoolingGraphRuntimeTick emergency = runtime.Evaluate(
            graph, [StaleGpuSample(now.AddSeconds(2))], new Dictionary<string, FanCalibrationV2>(), 2, now.AddSeconds(2));

        Assert.False(held.Evaluation.Emergency);
        Assert.Contains(GpuTemperatureSensorId, held.HeldSensorIds);
        Assert.True(emergency.Evaluation.Emergency);
        Assert.Equal(100, emergency.Evaluation.OutputValues[CapabilityId]);

        // The service loop drives the emergency duty through the adapter, then
        // recovery resets the output to the driver's automatic curve.
        await ApplyLikeTheServiceLoopAsync(adapter, emergency.Evaluation.OutputValues[CapabilityId]);
        await adapter.ResetToDefaultAsync(CapabilityId, default);

        Assert.Equal([100], transport.ManualDutyCommands);
        Assert.True(transport.RestoredAutomatic);
    }

    [Fact]
    public async Task TheGraphOutputContractMatchesTheArmedGpuFanCapability()
    {
        FakeGpuFanCoolerTransport transport = new(new GpuFanBounds(30, 100));
        await using NvidiaGpuFanAdapter adapter = new(transport, DeviceId, ChannelId, enableWrites: true);

        CapabilityDescriptor capability = Assert.Single((await adapter.ProbeAsync(default)).Capabilities);

        // The exact invariants ApplyCoolingGraphOutputsAsync enforces per tick.
        Assert.Equal(CapabilityId, capability.Id);
        Assert.Equal(CapabilityAccessState.Experimental, capability.State);
        Assert.Equal(ControlDomain.Cooling, capability.Domain);
        Assert.True(capability.CanResetToDefault);
        Assert.NotNull(capability.Range);
        Assert.Equal(30, capability.Range!.Minimum);
        Assert.Equal(100, capability.Range!.Maximum);
    }

    /// <summary>Prepare → apply → verify, exactly like PCHelperRuntime.ApplyCoolingDutyAsync.</summary>
    private static async Task ApplyLikeTheServiceLoopAsync(NvidiaGpuFanAdapter adapter, double dutyPercent)
    {
        ProfileAction action = new(
            $"cooling-loop:test:{CapabilityId}",
            NvidiaGpuFanAdapter.AdapterId,
            CapabilityId,
            ControlValue.FromNumeric(dutyPercent),
            Required: true,
            Order: 0);
        PreparedAction prepared = await adapter.PrepareAsync(action, default);
        await adapter.ApplyAsync(prepared, default);
        ActionVerification verification = await adapter.VerifyAsync(prepared, default);
        Assert.True(verification.Success, verification.Message);
    }

    private static CoolingGraphV1 GpuFanGraph() => new(
        CoolingGraphV1.CurrentSchemaVersion,
        "graph.gpu-fan",
        "GPU fan curve",
        [
            new CoolingGraphNodeV1(
                GpuTemperatureSensorId,
                "GPU temperature",
                CoolingNodeKind.Sensor,
                [],
                GpuTemperatureSensorId,
                [],
                new Dictionary<string, double>()),
            new CoolingGraphNodeV1(
                "curve",
                "GPU fan curve",
                CoolingNodeKind.Graph,
                [GpuTemperatureSensorId],
                null,
                [new CurvePoint(30, 30), new CurvePoint(90, 100)],
                new Dictionary<string, double>())
        ],
        [new CoolingGraphOutputV1(CapabilityId, "curve", FanOutputMode.DutyPercent, 30, 100, 0, 100, 100, [])]);

    private static SensorSample GpuSample(DateTimeOffset timestamp, double celsius) => new(
        GpuTemperatureSensorId,
        "lhm",
        DeviceId,
        "GPU Core",
        timestamp,
        celsius,
        "C",
        SensorQuality.Good,
        TimeSpan.Zero);

    private static SensorSample StaleGpuSample(DateTimeOffset timestamp) => new(
        GpuTemperatureSensorId,
        "lhm",
        DeviceId,
        "GPU Core",
        timestamp,
        null,
        "C",
        SensorQuality.Unavailable,
        TimeSpan.Zero);

    private sealed class FakeGpuFanCoolerTransport(GpuFanBounds? bounds) : IGpuFanCoolerTransport
    {
        public GpuFanChannelState State { get; private set; } = new(GpuFanControlPolicy.Automatic, null, null);

        public List<int> ManualDutyCommands { get; } = [];

        public bool RestoredAutomatic { get; private set; }

        public bool CanWrite => true;

        public void SetArmed(bool armed) { }

        public void Dispose() { }

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(bounds);

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(State);

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
        {
            ManualDutyCommands.Add(dutyPercent);
            State = new GpuFanChannelState(GpuFanControlPolicy.Manual, dutyPercent, dutyPercent);
            return Task.CompletedTask;
        }

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
        {
            RestoredAutomatic = true;
            State = new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null);
            return Task.CompletedTask;
        }
    }
}
