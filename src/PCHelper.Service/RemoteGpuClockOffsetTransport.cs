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
            });
    }

    public bool CanWrite => !_disposed;

    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
        }

        try
        {
            _ = SendAsync(
                new GpuClockSessionRequest(GpuClockSessionOps.SetArmed, GpuClockOffsetDomain.Core, 0, armed),
                CancellationToken.None)
                .GetAwaiter()
                .GetResult();
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
        GpuClockSessionResult result = await SendAsync(
            new GpuClockSessionRequest(GpuClockSessionOps.ReadBounds, domain, 0, false),
            cancellationToken).ConfigureAwait(false);
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
