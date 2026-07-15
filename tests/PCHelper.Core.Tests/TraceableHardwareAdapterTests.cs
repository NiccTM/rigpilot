using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class TraceableHardwareAdapterTests
{
    [Fact]
    public async Task SuccessfulOperationsProduceBoundedRedactedTraceEvents()
    {
        await using TraceableHardwareAdapter adapter = new(new FakeAdapter(), capacity: 4);

        _ = await adapter.ProbeAsync(CancellationToken.None);
        _ = await adapter.ReadSensorsAsync(CancellationToken.None);
        _ = await adapter.GetHealthAsync(CancellationToken.None);
        IReadOnlyList<AdapterTraceEvent> trace = await ReadAsync(adapter);

        Assert.Equal(3, trace.Count);
        Assert.All(trace, item => Assert.True(item.Success));
        Assert.Equal(["Probe", "ReadSensors", "GetHealth"], trace.Select(item => item.Operation));
        Assert.All(trace, item => Assert.Equal("Completed.", item.Message));
        Assert.All(trace, item => Assert.Equal("test.trace", item.AdapterId));
    }

    [Fact]
    public async Task FailedOperationRecordsOnlyExceptionTypeAndRetainsCapabilityId()
    {
        FakeAdapter inner = new() { ThrowOnApply = true };
        await using TraceableHardwareAdapter adapter = new(inner);
        ProfileAction action = new("action.apply", "test.trace", "fan.case", ControlValue.FromNumeric(50), true, 0);
        PreparedAction prepared = new(action, null, DateTimeOffset.UtcNow, "token");

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ApplyAsync(prepared, CancellationToken.None));
        AdapterTraceEvent trace = Assert.Single(await ReadAsync(adapter));

        Assert.False(trace.Success);
        Assert.Equal("Apply", trace.Operation);
        Assert.Equal("fan.case", trace.CapabilityId);
        Assert.Equal("InvalidOperationException.", trace.Message);
        Assert.DoesNotContain("C:\\Users", trace.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapacityDropsOldestTraceEntries()
    {
        await using TraceableHardwareAdapter adapter = new(new FakeAdapter(), capacity: 2);

        _ = await adapter.ProbeAsync(CancellationToken.None);
        _ = await adapter.ReadSensorsAsync(CancellationToken.None);
        _ = await adapter.GetHealthAsync(CancellationToken.None);
        IReadOnlyList<AdapterTraceEvent> trace = await ReadAsync(adapter);

        Assert.Equal(2, trace.Count);
        Assert.Equal(["ReadSensors", "GetHealth"], trace.Select(item => item.Operation));
    }

    private static async Task<IReadOnlyList<AdapterTraceEvent>> ReadAsync(TraceableHardwareAdapter adapter)
    {
        List<AdapterTraceEvent> trace = [];
        await foreach (AdapterTraceEvent item in adapter.ReadTraceAsync(CancellationToken.None))
        {
            trace.Add(item);
        }
        return trace;
    }

    private sealed class FakeAdapter : IHardwareAdapter
    {
        public bool ThrowOnApply { get; init; }

        public AdapterManifest Manifest { get; } = new(
            "test.trace",
            "Trace test adapter",
            "1.0.0",
            "GPL-3.0-only",
            null,
            AdapterExecutionContext.UserSession,
            ["test"],
            ["Test"]);

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterProbeResult(Manifest, [], [], []));

        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SensorSample>>([]);

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new PreparedAction(action, null, DateTimeOffset.UtcNow, "token"));

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) => ThrowOnApply
            ? Task.FromException(new InvalidOperationException("C:\\Users\\private\\hardware value is unavailable."))
            : Task.CompletedTask;

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionVerification(action.Action.Id, true, action.Action.Value, "ok"));

        public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "ok", []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
