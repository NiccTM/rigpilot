using System.Diagnostics;
using System.Security.Cryptography;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Service;

/// <summary>
/// Owns one dedicated <c>PCHelper.AdapterHost</c> child that holds a single NVIDIA
/// control session (fan, power, or clock) and forwards typed requests to it.
///
/// Each family gets its OWN host on purpose. The refusals this exists to solve —
/// NVAPI returning INVALID_USER_PRIVILEGE on restore while the same session's
/// writes succeed — were the rapid-call fragility of one session shared by fan
/// settle-polls, clock reads, and the service's own traffic. Putting every family
/// back in one child would rebuild exactly that. Separate children keep each
/// session quiet, which is what actually fixed it.
///
/// <see cref="RecycleAsync"/> kills and respawns the child. For the fan that also
/// reclaims hardware (the driver hands the fan back when the owning process dies);
/// for power and clock it does not reset anything, and is used only to obtain a
/// fresh session before retrying a refused write.
/// </summary>
internal sealed class GpuSessionHost : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);

    private readonly string _modeArgument;
    private readonly string _label;
    private readonly Func<GpuSessionHost, CancellationToken, Task>? _onSessionStarted;
    private readonly string _pipeName;
    private readonly string _sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ChildProcessJob _job = new();
    private readonly object _gate = new();
    private Process? _process;
    private bool _disposed;

    /// <param name="modeArgument">The host switch, e.g. <c>--gpu-fan-session</c>.</param>
    /// <param name="label">Human name used in failure messages.</param>
    /// <param name="onSessionStarted">
    /// Runs against every freshly started child before it is published, so state the
    /// child cannot know (such as the armed flag) is re-applied after a recycle. It
    /// must use <see cref="SendOnCurrentSessionAsync"/>; calling <see cref="SendAsync"/>
    /// would re-enter the start gate and deadlock.
    /// </param>
    public GpuSessionHost(
        string modeArgument,
        string label,
        Func<GpuSessionHost, CancellationToken, Task>? onSessionStarted = null)
    {
        _modeArgument = modeArgument;
        _label = label;
        _onSessionStarted = onSessionStarted;
        _pipeName = $"{ProtocolConstants.AdapterHostPipeName}.{modeArgument.TrimStart('-')}.{Environment.ProcessId}.{Guid.NewGuid():N}";
    }

    public bool IsDisposed => _disposed;

    /// <summary>Ensures the child is up, then sends one request.</summary>
    public async Task<TResult> SendAsync<TPayload, TResult>(
        IpcCommand command,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnCurrentSessionAsync<TPayload, TResult>(command, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends without ensuring the child — the caller guarantees it is up.</summary>
    public async Task<TResult> SendOnCurrentSessionAsync<TPayload, TResult>(
        IpcCommand command,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        NamedPipeRequestClient client = new(_pipeName, ConnectTimeout, OperationTimeout);
        IpcResponse response = await client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                command,
                new AdapterHostEnvelope<TPayload>(_sessionToken, payload)),
            cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"{_label} helper rejected {command}: {response.ErrorCode}: {response.Error}");
        }

        return IpcJson.FromElement<TResult>(response.Payload)
            ?? throw new InvalidDataException($"{_label} helper returned an empty result.");
    }

    /// <summary>
    /// Kills the child without disposing the host; the next send transparently
    /// respawns it. Used to drop the NVAPI session while the family is disarmed, so
    /// an idle service carries neither the session nor the child's ~30 MB.
    /// </summary>
    public async Task ReleaseAsync()
    {
        Process? doomed;
        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
    }

    /// <summary>Kills the child and brings up a fresh session.</summary>
    public async Task RecycleAsync(CancellationToken cancellationToken)
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

        await EnsureAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
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
            startInfo.ArgumentList.Add(_modeArgument);
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(_pipeName);
            startInfo.Environment["PCHELPER_ADAPTER_HOST_TOKEN"] = _sessionToken;

            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{_label} helper could not be started.");
            _job.Add(process);

            Exception? lastError = null;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"{_label} helper exited with code {process.ExitCode} during startup.");
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
                        if (_onSessionStarted is not null)
                        {
                            await _onSessionStarted(this, cancellationToken).ConfigureAwait(false);
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
            throw new TimeoutException($"{_label} helper did not open its private pipe.", lastError);
        }
        finally
        {
            _startGate.Release();
        }
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
