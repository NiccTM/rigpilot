using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Service;

/// <summary>
/// An <see cref="IGpuClockOffsetTransport"/> whose NVAPI session lives in a dedicated
/// <c>PCHelper.AdapterHost --gpu-clock-session</c> child rather than in the service.
///
/// Clock offsets were the other half of the "NVAPI writes are refused in the
/// LocalSystem service context" defect, and the refused restore was what drove the
/// service into RecoveryRequired and then left it unable to restore. Isolating the
/// session in a quiet, clock-only child is the fix the live fan work proved; a
/// refused write additionally gets one retry on a freshly recycled session.
///
/// The base seam deliberately has no arm gate, because restores must never be
/// blocked. The arm state lives here only so it can be forwarded to the child and
/// re-applied to a fresh session after a recycle; restores stay ungated either way,
/// and the adapter above enforces arming for real writes.
/// </summary>
internal sealed class RemoteGpuClockOffsetTransport : IArmedGpuClockOffsetTransport
{
    private readonly GpuSessionHost _host;
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<GpuClockOffsetDomain, GpuClockOffsetBounds> _cachedBounds = new();
    private bool _armed;
    private bool _disposed;

    private RemoteGpuClockOffsetTransport()
    {
        _host = new GpuSessionHost(
            "--gpu-clock-session",
            "GPU clock session",
            async (host, cancellationToken) =>
            {
                bool armed;
                lock (_gate)
                {
                    armed = _armed;
                }

                if (armed)
                {
                    _ = await host.SendOnCurrentSessionAsync<GpuClockSessionRequest, GpuClockSessionResult>(
                        IpcCommand.GpuClockSession,
                        new GpuClockSessionRequest(GpuClockSessionOps.SetArmed, GpuClockOffsetDomain.Core, 0, true),
                        cancellationToken).ConfigureAwait(false);
                }
            },
            IdleSessionTimeout,
            HoldsAppliedOffset);
    }

    /// <summary>
    /// The offset last written per domain, in kilohertz. An NVAPI pstates20 delta lives with
    /// the session that set it, so this is what decides whether the helper may be released.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<GpuClockOffsetDomain, int> _appliedOffsets = new();

    private bool HoldsAppliedOffset() => _appliedOffsets.Values.Any(offset => offset != 0);

    /// <summary>
    /// Releases an idle helper so an untouched service holds no NVAPI session.
    ///
    /// <para>This used to fire unconditionally, on the belief that clock offsets persist in
    /// the driver the way the NVML power limit does. They do not. Measured on an RTX 3090: a
    /// saved +49/+126 MHz overclock applied and read-back verified at service start, and read
    /// back as 0/0 once this timeout dropped the helper roughly two minutes later, while the
    /// 385 W power limit applied in the same transaction survived. Nothing was logged, because
    /// releasing an idle child is routine — the overclock simply evaporated.</para>
    ///
    /// <para>So the release is now suppressed while any domain holds a non-stock offset, which
    /// is the same rule the fan helper follows for the same reason: releasing that child hands
    /// the cooler back to firmware. An offset of zero is stock and holds nothing, so the
    /// resting service still ends up with no session.</para>
    /// </summary>
    private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(2);

    public bool CanWrite => !_disposed;

    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
        }

        try
        {
            if (armed)
            {
                _ = SendAsync(
                    new GpuClockSessionRequest(GpuClockSessionOps.SetArmed, GpuClockOffsetDomain.Core, 0, true),
                    CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                // Disarmed is the resting state; drop the child rather than leaving an
                // idle NVAPI session running. Offsets persist in the driver, so
                // releasing changes no hardware state.
                _host.ReleaseAsync().GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Stored locally; re-applied whenever the helper is next (re)started.
        }
    }

    /// <summary>
    /// Spawns the helper and confirms it bound an editable pstates20 delta range,
    /// returning null (so the caller falls back) when it cannot.
    /// </summary>
    public static async Task<RemoteGpuClockOffsetTransport?> TryCreateAsync(CancellationToken cancellationToken)
    {
        RemoteGpuClockOffsetTransport transport = new();
        try
        {
            if (await transport.ReadBoundsAsync(GpuClockOffsetDomain.Core, cancellationToken).ConfigureAwait(false) is { IsValid: true })
            {
                // Warm the memory domain too before releasing. Registration reads both
                // domains' bounds, and a cache warmed for Core alone would respawn the
                // child for Memory and leave it resident for the life of the service —
                // exactly the idle cost this release exists to avoid.
                _ = await transport.ReadBoundsAsync(GpuClockOffsetDomain.Memory, cancellationToken).ConfigureAwait(false);

                // Bounds cached and the resting state is disarmed: drop the child so an
                // idle service holds no NVAPI session. The next write respawns it.
                await transport._host.ReleaseAsync().ConfigureAwait(false);
                return transport;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // NVAPI clock domain unavailable in the helper; fall through.
        }

        transport.Dispose();
        return null;
    }

    public async Task<GpuClockOffsetBounds?> ReadBoundsAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken)
    {
        // Static driver delta range per domain, and a routine capability probe reads
        // nothing else, so caching keeps the child released while disarmed.
        if (_cachedBounds.TryGetValue(domain, out GpuClockOffsetBounds cached))
        {
            return cached;
        }

        GpuClockSessionResult result = await SendAsync(
            new GpuClockSessionRequest(GpuClockSessionOps.ReadBounds, domain, 0, false),
            cancellationToken).ConfigureAwait(false);
        if (result.Bounds is { IsValid: true } discovered)
        {
            _cachedBounds[domain] = discovered;
        }

        return result.Bounds;
    }

    public async Task<GpuClockOffsetState> ReadStateAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken)
    {
        GpuClockSessionResult result = await SendAsync(
            new GpuClockSessionRequest(GpuClockSessionOps.ReadState, domain, 0, false),
            cancellationToken).ConfigureAwait(false);
        return result.State ?? new GpuClockOffsetState(null);
    }

    public Task SetOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken) =>
        WriteAsync(GpuClockSessionOps.SetOffset, domain, offsetKiloHertz, cancellationToken);

    public Task RestoreOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken) =>
        WriteAsync(GpuClockSessionOps.RestoreOffset, domain, offsetKiloHertz, cancellationToken);

    private async Task WriteAsync(string op, GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
    {
        GpuClockSessionRequest write = new(op, domain, offsetKiloHertz, false);
        GpuClockSessionResult result = await SendAsync(write, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            // Recorded only once the driver accepted it, so a refused write cannot pin the
            // session open for an offset the card never took.
            _appliedOffsets[domain] = offsetKiloHertz;
            return;
        }

        if (!result.Refused)
        {
            throw new GpuClockSafetyException($"GPU clock helper failed a write: {result.Message}");
        }

        // Refused by the driver: recycle for a clean NVAPI session and retry once.
        await _host.RecycleAsync(cancellationToken).ConfigureAwait(false);
        GpuClockSessionResult retry = await SendAsync(write, cancellationToken).ConfigureAwait(false);
        if (!retry.Ok)
        {
            throw new GpuClockSafetyException(
                $"GPU clock {domain} write was refused on a fresh NVAPI session too: {retry.Message}");
        }

        _appliedOffsets[domain] = offsetKiloHertz;
    }

    private Task<GpuClockSessionResult> SendAsync(GpuClockSessionRequest payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _host.SendAsync<GpuClockSessionRequest, GpuClockSessionResult>(
            IpcCommand.GpuClockSession,
            payload,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Dispose();
    }
}
