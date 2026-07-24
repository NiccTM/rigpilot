using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Service;

/// <summary>
/// An <see cref="IGpuFanCoolerTransport"/> whose NVAPI session lives in a dedicated,
/// recyclable child process (<c>PCHelper.AdapterHost --gpu-fan-session</c>) rather
/// than in the service. Reads and manual writes forward to that helper.
///
/// The reason it exists is reclaim. This class of GeForce driver refuses every
/// documented in-session restore-to-automatic (NVAPI_INVALID_USER_PRIVILEGE), but
/// the driver returns the fan to its firmware curve the instant the process holding
/// the NVAPI session exits. So <see cref="RestoreAutomaticAsync"/> asks the helper to
/// restore in-session first and, only if that is refused, kills the helper —
/// reclaiming the fan in seconds without restarting the whole service.
///
/// Isolating the session also turned out to fix the refusals themselves: a quiet,
/// fan-only session avoids the rapid-call fragility of sharing one NVAPI session
/// with clock reads and settle-poll bursts, so the in-session restore now usually
/// succeeds and the recycle is a proven-safe fallback rather than the common path.
///
/// It is also strictly safer than holding the session in-service: any helper death
/// (crash, kill, service stop) returns the fan to firmware automatic, where the
/// in-service transport would strand it under the last manual duty.
/// </summary>
internal sealed class RemoteGpuFanCoolerTransport : IGpuFanCoolerTransport
{
    private readonly GpuSessionHost _host;
    private readonly object _gate = new();
    private bool _armed;
    private bool _disposed;

    private RemoteGpuFanCoolerTransport()
    {
        _host = new GpuSessionHost(
            "--gpu-fan-session",
            "GPU fan session",
            async (host, cancellationToken) =>
            {
                // Re-apply the armed state to a fresh session so writes that follow a
                // recycle are not silently blocked by the transport's own gate.
                bool armed;
                lock (_gate)
                {
                    armed = _armed;
                }

                if (armed)
                {
                    _ = await host.SendOnCurrentSessionAsync<GpuFanSessionRequest, GpuFanSessionResult>(
                        IpcCommand.GpuFanSession,
                        new GpuFanSessionRequest(GpuFanSessionOps.SetArmed, "0", 0, true),
                        cancellationToken).ConfigureAwait(false);
                }
            });
    }

    public bool CanWrite => !_disposed;

    /// <summary>
    /// Spawns the helper and confirms it bound a controllable NVAPI cooler,
    /// returning null (so the service falls back to the NVML transport) when NVAPI
    /// or an NVIDIA cooler is unavailable.
    /// </summary>
    public static async Task<RemoteGpuFanCoolerTransport?> TryCreateAsync(CancellationToken cancellationToken)
    {
        RemoteGpuFanCoolerTransport transport = new();
        try
        {
            if (await transport.ReadBoundsAsync("0", cancellationToken).ConfigureAwait(false) is { IsValid: true })
            {
                return transport;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // NVAPI unavailable in the helper (no NVIDIA GPU / cooler, or the helper
            // could not start). Fall through to disposing and returning null.
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
            // The interface method is synchronous and infrequent (operator-initiated
            // arm/disarm). Block on the forward; the service has no synchronization
            // context so this cannot deadlock. On failure the state is still stored
            // and re-applied whenever the helper is next (re)started.
            _ = SendAsync(new GpuFanSessionRequest(GpuFanSessionOps.SetArmed, "0", 0, armed), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Stored locally; re-applied on the next session start.
        }
    }

    public async Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken)
    {
        GpuFanSessionResult result = await SendAsync(
            new GpuFanSessionRequest(GpuFanSessionOps.ReadBounds, channelId, 0, false),
            cancellationToken).ConfigureAwait(false);
        return result.Bounds;
    }

    public async Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
    {
        GpuFanSessionResult result = await SendAsync(
            new GpuFanSessionRequest(GpuFanSessionOps.ReadState, channelId, 0, false),
            cancellationToken).ConfigureAwait(false);
        return result.State ?? new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null);
    }

    public async Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
    {
        GpuFanSessionResult result = await SendAsync(
            new GpuFanSessionRequest(GpuFanSessionOps.SetManual, channelId, dutyPercent, false),
            cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw new GpuFanSafetyException($"GPU fan helper refused a manual write: {result.Message}");
        }
    }

    public async Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
    {
        GpuFanSessionResult result = await SendAsync(
            new GpuFanSessionRequest(GpuFanSessionOps.Restore, channelId, 0, false),
            cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            // In-session restore succeeded, or there was nothing under manual control
            // to undo. No recycle needed.
            return;
        }

        if (!result.Refused)
        {
            throw new GpuFanSafetyException($"GPU fan helper restore failed: {result.Message}");
        }

        // The helper exhausted every in-session restore and the driver refused them
        // all. Reclaim the fan by killing the helper: its NVAPI session dies with the
        // process and the driver returns the fan to the firmware curve. A fresh
        // helper is brought up and the reclaim is confirmed by read-back.
        await _host.RecycleAsync(cancellationToken).ConfigureAwait(false);

        // The driver's reclaim on process death can lag the exit by a beat, so poll
        // the fresh session briefly rather than trusting a single read.
        GpuFanControlPolicy lastPolicy = GpuFanControlPolicy.Manual;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            GpuFanChannelState state = await ReadStateAsync(channelId, cancellationToken).ConfigureAwait(false);
            lastPolicy = state.Policy;
            if (lastPolicy == GpuFanControlPolicy.Automatic)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }

        throw new GpuFanSafetyException(
            $"Recycling the GPU fan helper did not reclaim the fan; it read back {lastPolicy}. "
            + $"The in-session refusal was: {result.Message}");
    }

    private Task<GpuFanSessionResult> SendAsync(GpuFanSessionRequest payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _host.SendAsync<GpuFanSessionRequest, GpuFanSessionResult>(
            IpcCommand.GpuFanSession,
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

        // Killing the helper is the correct shutdown behaviour: its NVAPI session
        // release returns the fan to the firmware curve, so the service never leaves
        // the GPU fan stranded under manual control on stop.
        _host.Dispose();
    }
}
