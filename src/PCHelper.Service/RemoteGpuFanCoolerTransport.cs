using System.Diagnostics;
using System.Security.Cryptography;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Service;

/// <summary>
/// An <see cref="IGpuFanCoolerTransport"/> whose NVAPI session lives in a
/// dedicated, recyclable child process (<c>PCHelper.AdapterHost --gpu-fan-session</c>)
/// rather than in the service. Reads and manual writes forward to that helper.
///
/// The reason it exists is reclaim. This class of GeForce driver refuses every
/// documented in-session restore-to-automatic (NVAPI_INVALID_USER_PRIVILEGE), but
/// the driver returns the fan to its firmware curve the instant the process
/// holding the NVAPI session exits. So <see cref="RestoreAutomaticAsync"/> asks the
/// helper to restore in-session first and, only if that is refused, kills the
/// helper — reclaiming the fan in seconds without restarting the whole service.
///
/// It is also strictly safer than holding the session in-service: any helper
/// death (crash, kill, service stop) returns the fan to firmware automatic, where
/// the in-service transport would strand it under the last manual duty.
/// </summary>
internal sealed class RemoteGpuFanCoolerTransport : IGpuFanCoolerTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);

    private readonly string _pipeName =
        $"{ProtocolConstants.AdapterHostPipeName}.gpufan.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly string _sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ChildProcessJob _job = new();
    private readonly object _gate = new();
    private Process? _process;
    private bool _armed;
    private bool _disposed;

    private RemoteGpuFanCoolerTransport()
    {
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
            GpuFanBounds? bounds = await transport.ReadBoundsAsync("0", cancellationToken).ConfigureAwait(false);
            if (bounds is { IsValid: true })
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
            // and re-applied whenever the helper is next (re)ensured.
            _ = SendAsync(new GpuFanSessionRequest(GpuFanSessionOps.SetArmed, "0", 0, armed), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Stored locally; re-applied on the next EnsureHelperAsync.
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
        await RecycleHelperAsync(cancellationToken).ConfigureAwait(false);

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

    private async Task<GpuFanSessionResult> SendAsync(GpuFanSessionRequest payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureHelperAsync(cancellationToken).ConfigureAwait(false);
        return await SendDirectAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends without ensuring the helper — the caller guarantees it is up.</summary>
    private async Task<GpuFanSessionResult> SendDirectAsync(GpuFanSessionRequest payload, CancellationToken cancellationToken)
    {
        NamedPipeRequestClient client = new(_pipeName, ConnectTimeout, OperationTimeout);
        IpcResponse response = await client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.GpuFanSession,
                new AdapterHostEnvelope<GpuFanSessionRequest>(_sessionToken, payload)),
            cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new GpuFanSafetyException(
                $"GPU fan helper {payload.Op} failed: {response.ErrorCode}: {response.Error}");
        }

        return IpcJson.FromElement<GpuFanSessionResult>(response.Payload)
            ?? throw new InvalidDataException("GPU fan helper returned an empty result.");
    }

    private async Task EnsureHelperAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _process?.Dispose();
            string executable = AdapterHostProxy.ResolveAdapterHostPath();
            ProcessStartInfo startInfo = new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--gpu-fan-session");
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(_pipeName);
            startInfo.Environment["PCHELPER_ADAPTER_HOST_TOKEN"] = _sessionToken;

            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("GPU fan session helper could not be started.");
            _job.Add(process);

            Exception? lastError = null;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"GPU fan session helper exited with code {process.ExitCode} during startup.");
                }

                try
                {
                    NamedPipeRequestClient client = new(
                        _pipeName,
                        TimeSpan.FromMilliseconds(250),
                        TimeSpan.FromSeconds(2));
                    IpcResponse handshake = await client.SendAsync(
                        NamedPipeRequestClient.CreateRequest(
                            IpcCommand.Handshake,
                            new AdapterHostEnvelope<HandshakeRequest>(
                                _sessionToken,
                                new HandshakeRequest("PCHelper.Service", "0.7.0"))),
                        cancellationToken).ConfigureAwait(false);
                    if (handshake.Success)
                    {
                        _process = process;

                        // Re-apply the armed state to the fresh session so writes that
                        // follow a recycle are not silently blocked by the transport gate.
                        bool armed;
                        lock (_gate)
                        {
                            armed = _armed;
                        }

                        if (armed)
                        {
                            _ = await SendDirectAsync(
                                new GpuFanSessionRequest(GpuFanSessionOps.SetArmed, "0", 0, true),
                                cancellationToken).ConfigureAwait(false);
                        }

                        return;
                    }
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    lastError = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            await TerminateAsync(process).ConfigureAwait(false);
            throw new TimeoutException("GPU fan session helper did not open its private pipe.", lastError);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task RecycleHelperAsync(CancellationToken cancellationToken)
    {
        Process? doomed;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            doomed = _process;
            _process = null;
        }
        finally
        {
            _startGate.Release();
        }

        if (doomed is not null)
        {
            await TerminateAsync(doomed).ConfigureAwait(false);
        }

        await EnsureHelperAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            // Killing the helper is the correct shutdown behaviour: its NVAPI session
            // release returns the fan to the firmware curve, so the service never
            // leaves the GPU fan stranded under manual control on stop.
            try
            {
                TerminateAsync(process).GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort; the child job object also terminates it with the service.
            }
        }

        _job.Dispose();
        _startGate.Dispose();
    }
}
