using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Service;

/// <summary>
/// An <see cref="IGpuPowerLimitTransport"/> whose NVAPI session lives in a dedicated
/// <c>PCHelper.AdapterHost --gpu-power-session</c> child rather than in the service.
///
/// This closes the long-standing "NVAPI/NVML writes are refused in the LocalSystem
/// service context" defect for the power family. The refusals were not a session-type
/// wall — the live fan work proved they were the rapid-call fragility of one NVAPI
/// session shared by fan settle-polls, clock reads, and the service's own traffic.
/// A quiet, power-only session removes that. When the driver still refuses a write,
/// the helper is recycled once for a genuinely fresh session and the write retried;
/// unlike the fan, killing this child does NOT itself reset the limit, so the retry
/// is the point, not reclaim-by-exit.
/// </summary>
internal sealed class RemoteGpuPowerLimitTransport : IGpuPowerLimitTransport
{
    private readonly GpuSessionHost _host;
    private readonly object _gate = new();
    private bool _armed;
    private bool _disposed;

    private RemoteGpuPowerLimitTransport()
    {
        _host = new GpuSessionHost(
            "--gpu-power-session",
            "GPU power session",
            async (host, cancellationToken) =>
            {
                bool armed;
                lock (_gate)
                {
                    armed = _armed;
                }

                if (armed)
                {
                    _ = await host.SendOnCurrentSessionAsync<GpuPowerSessionRequest, GpuPowerSessionResult>(
                        IpcCommand.GpuPowerSession,
                        new GpuPowerSessionRequest(GpuPowerSessionOps.SetArmed, "0", 0, true),
                        cancellationToken).ConfigureAwait(false);
                }
            });
    }

    public bool CanWrite => !_disposed;

    /// <summary>
    /// Spawns the helper and confirms it bound a controllable NVAPI power policy,
    /// returning null (so the caller falls back to NVML) when it cannot.
    /// </summary>
    public static async Task<RemoteGpuPowerLimitTransport?> TryCreateAsync(CancellationToken cancellationToken)
    {
        RemoteGpuPowerLimitTransport transport = new();
        try
        {
            if (await transport.ReadBoundsAsync("0", cancellationToken).ConfigureAwait(false) is { IsValid: true })
            {
                return transport;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // NVAPI power policy unavailable in the helper; fall through.
        }

        transport.Dispose();
        return null;
    }

    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
        }

        try
        {
            _ = SendAsync(new GpuPowerSessionRequest(GpuPowerSessionOps.SetArmed, "0", 0, armed), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Stored locally; re-applied whenever the helper is next (re)started.
        }
    }

    public async Task<GpuPowerLimitBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken)
    {
        GpuPowerSessionResult result = await SendAsync(
            new GpuPowerSessionRequest(GpuPowerSessionOps.ReadBounds, channelId, 0, false),
            cancellationToken).ConfigureAwait(false);
        return result.Bounds;
    }

    public async Task<GpuPowerLimitState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
    {
        GpuPowerSessionResult result = await SendAsync(
            new GpuPowerSessionRequest(GpuPowerSessionOps.ReadState, channelId, 0, false),
            cancellationToken).ConfigureAwait(false);
        return result.State ?? new GpuPowerLimitState(null);
    }

    public async Task SetPowerLimitAsync(string channelId, uint milliwatts, CancellationToken cancellationToken)
    {
        GpuPowerSessionRequest write = new(GpuPowerSessionOps.SetLimit, channelId, milliwatts, false);
        GpuPowerSessionResult result = await SendAsync(write, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            return;
        }

        if (!result.Refused)
        {
            throw new GpuPowerSafetyException($"GPU power helper failed a write: {result.Message}");
        }

        // The driver refused this write. Recycle for a clean NVAPI session — the
        // documented mitigation for the rapid-call fragility — and try exactly once
        // more before surfacing the refusal.
        await _host.RecycleAsync(cancellationToken).ConfigureAwait(false);
        GpuPowerSessionResult retry = await SendAsync(write, cancellationToken).ConfigureAwait(false);
        if (!retry.Ok)
        {
            throw new GpuPowerSafetyException(
                $"GPU power write was refused on a fresh NVAPI session too: {retry.Message}");
        }
    }

    private Task<GpuPowerSessionResult> SendAsync(GpuPowerSessionRequest payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _host.SendAsync<GpuPowerSessionRequest, GpuPowerSessionResult>(
            IpcCommand.GpuPowerSession,
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
