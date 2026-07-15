using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Adds a bounded, in-memory operational trace to an adapter without changing
/// its hardware semantics. The trace intentionally records only adapter and
/// capability identities plus a coarse outcome; it never records values,
/// device paths, or exception messages that could leak local identity data.
/// </summary>
public sealed class TraceableHardwareAdapter : IHardwareAdapter, ITraceableAdapter, IAdapterDiagnosticsProvider
{
    private const int DefaultCapacity = 512;
    private readonly IHardwareAdapter _inner;
    private readonly ConcurrentQueue<AdapterTraceEvent> _events = new();
    private readonly int _capacity;
    private int _eventCount;
    private int _disposed;

    public TraceableHardwareAdapter(IHardwareAdapter inner, int capacity = DefaultCapacity)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _capacity = capacity is > 0 and <= 4096
            ? capacity
            : throw new ArgumentOutOfRangeException(nameof(capacity), "Trace capacity must be 1-4096 events.");
    }

    public AdapterManifest Manifest => _inner.Manifest;

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        RecordAsync("Probe", string.Empty, () => _inner.ProbeAsync(cancellationToken));

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        RecordAsync("ReadSensors", string.Empty, () => _inner.ReadSensorsAsync(cancellationToken));

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        RecordAsync("Prepare", action.CapabilityId, () => _inner.PrepareAsync(action, cancellationToken));

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        RecordAsync("Apply", action.Action.CapabilityId, () => _inner.ApplyAsync(action, cancellationToken));

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        RecordAsync("Verify", action.Action.CapabilityId, () => _inner.VerifyAsync(action, cancellationToken));

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) =>
        RecordAsync("Rollback", action.Action.CapabilityId, () => _inner.RollbackAsync(action, cancellationToken));

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        RecordAsync("ResetToDefault", capabilityId, () => _inner.ResetToDefaultAsync(capabilityId, cancellationToken));

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) =>
        RecordAsync("GetHealth", string.Empty, () => _inner.GetHealthAsync(cancellationToken));

    public Task<AdapterHostDiagnosticsV1?> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
        _inner is IAdapterDiagnosticsProvider diagnostics
            ? diagnostics.GetDiagnosticsAsync(cancellationToken)
            : Task.FromResult<AdapterHostDiagnosticsV1?>(null);

    public async IAsyncEnumerable<AdapterTraceEvent> ReadTraceAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (AdapterTraceEvent trace in _events.ToArray().OrderBy(item => item.Timestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return trace;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<T> RecordAsync<T>(string operation, string capabilityId, Func<Task<T>> action)
    {
        try
        {
            T result = await action().ConfigureAwait(false);
            Record(operation, capabilityId, success: true, "Completed.");
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Record(operation, capabilityId, success: false, $"{exception.GetType().Name}.");
            throw;
        }
    }

    private async Task RecordAsync(string operation, string capabilityId, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            Record(operation, capabilityId, success: true, "Completed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Record(operation, capabilityId, success: false, $"{exception.GetType().Name}.");
            throw;
        }
    }

    private void Record(string operation, string capabilityId, bool success, string message)
    {
        _events.Enqueue(new AdapterTraceEvent(
            DateTimeOffset.UtcNow,
            Manifest.Id,
            operation,
            capabilityId,
            success,
            message));
        if (Interlocked.Increment(ref _eventCount) <= _capacity)
        {
            return;
        }

        while (Volatile.Read(ref _eventCount) > _capacity && _events.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _eventCount);
        }
    }
}
