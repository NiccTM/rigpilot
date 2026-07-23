using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Service;

internal sealed class AdapterHostProxy : IHardwareAdapter, IHardwareStateVerifier, IAdapterDiagnosticsProvider, IAdapterTopologyCachePolicy
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Topology discovery gets a longer budget than any other host call.
    /// Enumerating hardware with elevated access is genuinely slow — measured at
    /// ~16.5 s on the reference ROG STRIX X570-E, dominated by Super-I/O and
    /// storage enumeration — and exceeding the timeout was self-perpetuating:
    /// the probe was killed, the host recycled, the topology cache never
    /// populated, so the next capture reran the same doomed probe. That cost a
    /// fixed ~11 s per capture (10 s timeout plus recycle), which made every
    /// other adapter's sensors look stale to the cooling freshness gate, and it
    /// hid every motherboard fan control on the system.
    ///
    /// This applies to discovery only. Sensor reads, health checks, and above
    /// all mutations keep <see cref="OperationTimeout"/>: a mutation that
    /// outlives its timeout leaves hardware state unknown, and that must be
    /// detected quickly rather than waited out.
    /// </summary>
    private static readonly TimeSpan TopologyProbeTimeout = TimeSpan.FromSeconds(45);
    private static readonly Regex StructuredFailurePattern = new(
        @"Adapter-host (?<command>\w+) failed at (?<stage>[^()]+) \((?<type>[^;]+); HResult=0x(?<hresult>[0-9A-Fa-f]{8})(?:; Win32=(?<win32>\d+))?\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _pipeName = $"{ProtocolConstants.AdapterHostPipeName}.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly string _sessionToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ChildProcessJob _job = new();
    private readonly object _diagnosticsGate = new();
    private Process? _process;
    private readonly StringBuilder _processLog = new();
    private AdapterHostDiagnosticsV1? _lastDiagnostics;
    private bool _disposed;

    internal string PipeName => _pipeName;

    public AdapterManifest Manifest { get; } = new(
        "librehardwaremonitor",
        "LibreHardwareMonitor Adapter Host",
        "0.9.6",
        "MPL-2.0",
        "Signed PawnIO for privileged motherboard access",
        AdapterExecutionContext.AdapterHost,
        ["LibreHardwareMonitor 0.9.6 supported devices"],
        ["Monitoring", "MotherboardFanExperimental", "GpuFanExperimental", "UsbControllerDiscoveryContainedReadOnly"]);

    public TimeSpan TopologyCacheDuration => TimeSpan.FromSeconds(30);

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        await SendReadAsync<AdapterProbeResult>(IpcCommand.AdapterProbe, cancellationToken, TopologyProbeTimeout).ConfigureAwait(false);

    public async Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        await SendReadAsync<IReadOnlyList<SensorSample>>(IpcCommand.AdapterReadSensors, cancellationToken).ConfigureAwait(false);

    public async Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
    {
        ClearCachedFailure();
        return await SendMutationAsync<ProfileAction, PreparedAction>(
            IpcCommand.AdapterPrepare,
            action,
            cancellationToken,
            mutationOutcomeUnknownOnTimeout: false).ConfigureAwait(false);
    }

    public async Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        _ = await SendMutationAsync<PreparedAction, string>(
            IpcCommand.AdapterApply,
            action,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        await SendMutationAsync<PreparedAction, ActionVerification>(
            IpcCommand.AdapterVerify,
            action,
            cancellationToken,
            mutationOutcomeUnknownOnTimeout: false).ConfigureAwait(false);

    public async Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        _ = await SendMutationAsync<PreparedAction, string>(
            IpcCommand.AdapterRollback,
            action,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        _ = await SendMutationAsync<AdapterResetRequest, string>(
            IpcCommand.AdapterReset,
            new AdapterResetRequest(capabilityId),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<HardwareStateVerification> VerifyDefaultStateAsync(
        string capabilityId,
        CancellationToken cancellationToken) =>
        SendMutationAsync<AdapterDefaultVerificationRequest, HardwareStateVerification>(
            IpcCommand.AdapterVerifyDefault,
            new AdapterDefaultVerificationRequest(capabilityId),
            cancellationToken,
            mutationOutcomeUnknownOnTimeout: false);

    public Task<HardwareStateVerification> VerifyRollbackStateAsync(
        PreparedAction action,
        CancellationToken cancellationToken) =>
        SendMutationAsync<AdapterRollbackVerificationRequest, HardwareStateVerification>(
            IpcCommand.AdapterVerifyRollback,
            new AdapterRollbackVerificationRequest(action),
            cancellationToken,
            mutationOutcomeUnknownOnTimeout: false);

    public async Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SendReadAsync<AdapterHealth>(IpcCommand.AdapterHealth, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AdapterHealth(
                Manifest.Id,
                false,
                DateTimeOffset.UtcNow,
                "Adapter Host is unavailable.",
                [exception.Message]);
        }
    }

    public async Task<AdapterHostDiagnosticsV1?> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        AdapterHostDiagnosticsV1? cached = GetCachedDiagnostics();
        try
        {
            AdapterHostDiagnosticsV1 live = await SendReadAsync<AdapterHostDiagnosticsV1>(
                IpcCommand.AdapterDiagnostics,
                cancellationToken).ConfigureAwait(false);
            AdapterHostDiagnosticsV1 merged = cached?.LastFailure is AdapterHostFailureV1 priorFailure
                && live.LastFailure is null
                ? live with { LastFailure = priorFailure }
                : live;
            SetCachedDiagnostics(merged);
            return merged;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return cached;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? process = _process;
        if (process is not null && !process.HasExited)
        {
            try
            {
                NamedPipeRequestClient client = CreateClient(TimeSpan.FromSeconds(1));
                IpcRequest request = NamedPipeRequestClient.CreateRequest(
                    IpcCommand.AdapterShutdown,
                    new AdapterHostEnvelope<string>(_sessionToken, "shutdown"));
                _ = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }

        process?.Dispose();
        _job.Dispose();
        _startGate.Dispose();
    }

    private async Task<T> SendReadAsync<T>(
        IpcCommand command,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null)
    {
        TimeSpan budget = operationTimeout ?? OperationTimeout;
        Process? generation = null;
        try
        {
            await EnsureHostAsync(cancellationToken).ConfigureAwait(false);
            generation = _process;
            NamedPipeRequestClient client = CreateClient(budget);
            IpcResponse response = await client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    command,
                    new AdapterHostEnvelope<string>(_sessionToken, "read")),
                cancellationToken).ConfigureAwait(false);
            return ReadPayload<T>(response);
        }
        catch (TimeoutException exception)
        {
            await RecycleHostAsync(generation).ConfigureAwait(false);
            throw new TimeoutException($"Adapter Host {command} did not complete within {budget.TotalSeconds:0} s; the host was terminated and will be recycled.", exception);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            InvalidOperationException failure = HostCommunicationException(exception);
            await RecycleHostAsync(generation).ConfigureAwait(false);
            throw failure;
        }
    }

    private async Task<TResult> SendMutationAsync<TPayload, TResult>(
        IpcCommand command,
        TPayload payload,
        CancellationToken cancellationToken,
        bool mutationOutcomeUnknownOnTimeout = true)
    {
        Process? generation = null;
        try
        {
            await EnsureHostAsync(cancellationToken).ConfigureAwait(false);
            generation = _process;
            NamedPipeRequestClient client = CreateClient(OperationTimeout);
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                command,
                new AdapterHostEnvelope<TPayload>(_sessionToken, payload));
            IpcResponse response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return ReadPayload<TResult>(response);
        }
        catch (TimeoutException exception)
        {
            await RecycleHostAsync(generation).ConfigureAwait(false);
            Exception failure = mutationOutcomeUnknownOnTimeout
                ? new HardwareStateUnknownException(
                    Manifest.Id,
                    command.ToString(),
                    $"Adapter Host {command} timed out after transmission. The process tree was terminated; hardware state is unknown and recovery is required.",
                    exception)
                : new TimeoutException(
                    $"Adapter Host {command} did not complete within {OperationTimeout.TotalSeconds:0} s; the host was terminated and will be recycled.",
                    exception);
            CacheFailure(command, failure);
            throw failure;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            InvalidOperationException communicationFailure = HostCommunicationException(exception);
            await RecycleHostAsync(generation).ConfigureAwait(false);
            Exception failure = mutationOutcomeUnknownOnTimeout
                ? new HardwareStateUnknownException(
                    Manifest.Id,
                    command.ToString(),
                    $"Adapter Host {command} lost its pipe after transmission. Hardware state is unknown and recovery is required.",
                    communicationFailure)
                : communicationFailure;
            CacheFailure(command, failure);
            throw failure;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CacheFailure(command, exception);
            throw;
        }
    }

    private AdapterHostDiagnosticsV1? GetCachedDiagnostics()
    {
        lock (_diagnosticsGate)
        {
            return _lastDiagnostics;
        }
    }

    private void SetCachedDiagnostics(AdapterHostDiagnosticsV1 diagnostics)
    {
        lock (_diagnosticsGate)
        {
            _lastDiagnostics = diagnostics;
        }
    }

    private void ClearCachedFailure()
    {
        lock (_diagnosticsGate)
        {
            if (_lastDiagnostics is not null)
            {
                _lastDiagnostics = _lastDiagnostics with { LastFailure = null };
            }
        }
    }

    private void CacheFailure(IpcCommand command, Exception exception)
    {
        AdapterHostFailureV1 failure = CreateFailure(command, exception);
        lock (_diagnosticsGate)
        {
            AdapterHostDiagnosticsV1? existing = _lastDiagnostics;
            _lastDiagnostics = existing is null
                ? new AdapterHostDiagnosticsV1(
                    AdapterHostDiagnosticsV1.CurrentSchemaVersion,
                    DateTimeOffset.UtcNow,
                    GetHostProcessId(),
                    "Unavailable",
                    null,
                    "UnavailableAfterHostFailure",
                    "PerRequestSessionToken",
                    "UnavailableAfterHostFailure",
                    failure)
                : existing with
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    LastFailure = failure
                };
        }
    }

    private AdapterHostFailureV1 CreateFailure(IpcCommand command, Exception exception)
    {
        Match match = StructuredFailurePattern.Match(exception.Message);
        if (match.Success
            && uint.TryParse(match.Groups["hresult"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsedHResult))
        {
            int? win32 = int.TryParse(match.Groups["win32"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedWin32)
                ? parsedWin32
                : null;
            return new AdapterHostFailureV1(
                match.Groups["command"].Value,
                match.Groups["stage"].Value.Trim(),
                match.Groups["type"].Value.Trim(),
                unchecked((int)parsedHResult),
                win32,
                DateTimeOffset.UtcNow);
        }

        return new AdapterHostFailureV1(
            command.ToString(),
            IsHostExited() ? "HostExited" : "HostCommunication",
            exception.GetType().Name,
            exception.HResult,
            null,
            DateTimeOffset.UtcNow);
    }

    private int GetHostProcessId()
    {
        try
        {
            return _process is null || _process.HasExited ? 0 : _process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private bool IsHostExited()
    {
        try
        {
            return _process?.HasExited == true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private NamedPipeRequestClient CreateClient(TimeSpan timeout) => new(
        _pipeName,
        ConnectTimeout,
        timeout);

    internal async Task RecycleHostAsync(Process? expectedGeneration)
    {
        if (expectedGeneration is null)
        {
            return;
        }

        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_process, expectedGeneration))
            {
                return;
            }

            _process = null;
            await TerminateProcessTreeAsync(expectedGeneration).ConfigureAwait(false);
        }
        catch
        {
            Process? failedGeneration = _process;
            _process = null;
            if (failedGeneration is not null)
            {
                await TerminateProcessTreeAsync(failedGeneration).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private static async Task TerminateProcessTreeAsync(Process process)
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
            // The process exited between the state check and termination.
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task EnsureHostAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            string executable = ResolveAdapterHostPath();
            ProcessStartInfo startInfo = new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(_pipeName);
            startInfo.Environment["PCHELPER_ADAPTER_HOST_TOKEN"] = _sessionToken;
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Adapter Host process could not be started.");
            _job.Add(_process);
            _process.OutputDataReceived += CaptureProcessOutput;
            _process.ErrorDataReceived += CaptureProcessOutput;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Exception? lastError = null;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"Adapter Host exited with code {_process.ExitCode} during startup.");
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
                                new HandshakeRequest("PCHelper.Service", Manifest.Version))),
                        cancellationToken).ConfigureAwait(false);
                    _ = ReadPayload<HandshakeResponse>(handshake);
                    return;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
                {
                    lastError = exception;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }

            Process? failedGeneration = _process;
            _process = null;
            if (failedGeneration is not null)
            {
                await TerminateProcessTreeAsync(failedGeneration).ConfigureAwait(false);
            }
            throw new TimeoutException("Adapter Host did not open its private pipe; the failed process tree was terminated.", lastError);
        }
        catch
        {
            Process? failedGeneration = _process;
            _process = null;
            if (failedGeneration is not null)
            {
                await TerminateProcessTreeAsync(failedGeneration).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private static T ReadPayload<T>(IpcResponse response)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException($"{response.ErrorCode}: {response.Error}");
        }

        return IpcJson.FromElement<T>(response.Payload)
            ?? throw new InvalidDataException("Adapter Host returned an empty payload.");
    }

    private void CaptureProcessOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            return;
        }

        lock (_processLog)
        {
            _processLog.AppendLine(eventArgs.Data);
        }
    }

    private InvalidOperationException HostCommunicationException(Exception innerException)
    {
        string log;
        lock (_processLog)
        {
            log = _processLog.ToString().Trim();
        }

        string state = _process is { HasExited: true } process
            ? $"exited with code {process.ExitCode}"
            : "closed its pipe";
        return new InvalidOperationException(
            $"Adapter Host {state}. {(string.IsNullOrWhiteSpace(log) ? "No child-process diagnostics were emitted." : log)}",
            innerException);
    }

    internal static string ResolveAdapterHostPath()
    {
        string? configured = Environment.GetEnvironmentVariable("PCHELPER_ADAPTER_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        string[] installedCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "PCHelper.AdapterHost.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "adapter-host", "PCHelper.AdapterHost.exe"))
        ];
        string? installed = installedCandidates.FirstOrDefault(File.Exists);
        if (installed is not null)
        {
            return installed;
        }

        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string project = Path.Combine(directory.FullName, "src", "PCHelper.AdapterHost", "bin");
            if (!Directory.Exists(project))
            {
                continue;
            }

            string? development = Directory.EnumerateFiles(
                    project,
                    "PCHelper.AdapterHost.exe",
                    SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (development is not null)
            {
                return development;
            }
        }

        throw new FileNotFoundException(
            "PCHelper.AdapterHost.exe was not found. Set PCHELPER_ADAPTER_HOST_PATH for a development build.");
    }
}
