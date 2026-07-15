using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

/// <summary>
/// Starts an explicit UAC diagnostic that is restricted to adapter Prepare.
/// It is deliberately not a hardware-control bridge: the service never sees
/// or trusts this result as permission to apply a fan value.
/// </summary>
public interface IInteractiveFanPreflightLauncher
{
    Task<InteractiveFanPreflightResultV1> RunAsync(
        InteractiveFanPreflightRequestV1 request,
        CancellationToken cancellationToken);
}

public sealed class ElevatedInteractiveFanPreflightLauncher : IInteractiveFanPreflightLauncher
{
    private static readonly Regex SupportedCapabilityId = new(
        @"\Alhm\.control:/lpc/[A-Za-z0-9_-]+/\d+/control/\d+\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SessionTokenPattern = new(
        @"\A[0-9A-F]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _hostExecutablePath;
    private readonly TimeSpan _timeout;

    public ElevatedInteractiveFanPreflightLauncher(string? hostExecutablePath = null, TimeSpan? timeout = null)
    {
        _hostExecutablePath = hostExecutablePath ?? Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
    }

    public async Task<InteractiveFanPreflightResultV1> RunAsync(
        InteractiveFanPreflightRequestV1 request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (!File.Exists(_hostExecutablePath))
        {
            return LauncherFailure(
                request.CapabilityId,
                "ELEVATED_HOST_UNAVAILABLE",
                "The RigPilot dashboard executable required for the explicit UAC diagnostic is unavailable.");
        }

        string pipeName = $"pchelper.interactive-preflight.{Environment.ProcessId}.{Guid.NewGuid():N}";
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        TaskCompletionSource<InteractiveFanPreflightResultV1> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource serverCancellation = new();
        NamedPipeRequestServer server = new(
            pipeName,
            (requestMessage, _, tokenCancellation) => ReceiveResultAsync(
                requestMessage,
                request.CapabilityId,
                token,
                completion,
                tokenCancellation),
            clientIdentityMode: NamedPipeClientIdentityMode.TokenAuthenticatedPrivateChannel);
        Task serverTask = server.RunAsync(serverCancellation.Token);
        Process? child = null;

        try
        {
            try
            {
                child = StartElevatedChild(pipeName, token, request.CapabilityId);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return LauncherFailure(request.CapabilityId, "UAC_CANCELLED", "The elevated no-write diagnostic was cancelled before it started.");
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return LauncherFailure(request.CapabilityId, "ELEVATED_HOST_START_FAILED", "Windows did not start the elevated no-write diagnostic.");
            }

            Task<InteractiveFanPreflightResultV1> resultTask = completion.Task;
            Task exitTask = child.WaitForExitAsync(CancellationToken.None);
            Task timeoutTask = Task.Delay(_timeout, CancellationToken.None);
            Task cancelledTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Task completed = await Task.WhenAny(resultTask, exitTask, timeoutTask, cancelledTask).ConfigureAwait(false);
            if (completed == resultTask)
            {
                return await resultTask.ConfigureAwait(false);
            }
            if (completed == cancelledTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (completed == exitTask)
            {
                // The child can close its process a few milliseconds before the
                // pipe handler schedules the authenticated result.
                Task lateResult = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None)).ConfigureAwait(false);
                if (lateResult == resultTask)
                {
                    return await resultTask.ConfigureAwait(false);
                }
                return LauncherFailure(
                    request.CapabilityId,
                    "ELEVATED_HOST_EXITED",
                    "The elevated no-write diagnostic exited without returning an authenticated result.");
            }

            TryTerminate(child);
            return LauncherFailure(
                request.CapabilityId,
                "ELEVATED_HOST_TIMEOUT",
                "The elevated no-write diagnostic timed out. No service hardware command was issued.");
        }
        finally
        {
            serverCancellation.Cancel();
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown of the one-shot private pipe.
            }
            child?.Dispose();
        }
    }

    public static bool IsSupportedCapabilityId(string? capabilityId) =>
        !string.IsNullOrWhiteSpace(capabilityId)
        && SupportedCapabilityId.IsMatch(capabilityId);

    private Process StartElevatedChild(string pipeName, string token, string capabilityId)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _hostExecutablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(_hostExecutablePath) ?? Environment.CurrentDirectory,
            Arguments = $"--interactive-fan-preflight --pipe {pipeName} --token {token} --capability {capabilityId}"
        };
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the explicit elevated diagnostic process.");
    }

    private static async Task<IpcResponse> ReceiveResultAsync(
        IpcRequest request,
        string expectedCapabilityId,
        string expectedToken,
        TaskCompletionSource<InteractiveFanPreflightResultV1> completion,
        CancellationToken cancellationToken)
    {
        if (request.Command != IpcCommand.SubmitInteractiveFanPreflight)
        {
            return Failure(request, "INTERACTIVE_PREFLIGHT_COMMAND_INVALID", "The private diagnostic pipe accepts only its result message.");
        }

        InteractivePreflightEnvelope<InteractiveFanPreflightResultV1>? envelope =
            IpcJson.FromElement<InteractivePreflightEnvelope<InteractiveFanPreflightResultV1>>(request.Payload);
        if (envelope is null || !TokensEqual(expectedToken, envelope.SessionToken))
        {
            return Failure(request, "INTERACTIVE_PREFLIGHT_TOKEN_INVALID", "The private diagnostic token is invalid.");
        }

        InteractiveFanPreflightResultV1 result = envelope.Payload;
        if (!IsValidResult(result, expectedCapabilityId))
        {
            return Failure(request, "INTERACTIVE_PREFLIGHT_RESULT_INVALID", "The elevated diagnostic returned an invalid no-write result.");
        }

        completion.TrySetResult(result);
        await Task.CompletedTask.ConfigureAwait(false);
        return Success(request, "accepted");
    }

    private static bool IsValidResult(InteractiveFanPreflightResultV1 result, string expectedCapabilityId) =>
        result.SchemaVersion == InteractiveFanPreflightResultV1.CurrentSchemaVersion
        && string.Equals(result.CapabilityId, expectedCapabilityId, StringComparison.Ordinal)
        && !result.ApplyIssued
        && !result.VerifyIssued
        && !result.RollbackIssued
        && !result.ResetIssued
        && (!result.Prepared || result.IsElevated)
        && !string.IsNullOrWhiteSpace(result.OutcomeCode)
        && result.OutcomeCode.Length <= 80
        && !string.IsNullOrWhiteSpace(result.Summary)
        && result.Summary.Length <= 600;

    private static void ValidateRequest(InteractiveFanPreflightRequestV1 request)
    {
        if (request.SchemaVersion != InteractiveFanPreflightRequestV1.CurrentSchemaVersion
            || !IsSupportedCapabilityId(request.CapabilityId))
        {
            throw new ArgumentException(
                "The elevated no-write diagnostic accepts only a bounded LibreHardwareMonitor LPC cooling capability.",
                nameof(request));
        }
    }

    private static bool TokensEqual(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected)
            || string.IsNullOrWhiteSpace(actual)
            || !SessionTokenPattern.IsMatch(expected)
            || !SessionTokenPattern.IsMatch(actual))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static void TryTerminate(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The parent may not hold terminate rights over the elevated child.
            // It was limited to Prepare and will still exit with the process.
        }
    }

    private static InteractiveFanPreflightResultV1 LauncherFailure(
        string capabilityId,
        string outcomeCode,
        string summary) => new(
            InteractiveFanPreflightResultV1.CurrentSchemaVersion,
            capabilityId,
            Prepared: false,
            ApplyIssued: false,
            VerifyIssued: false,
            RollbackIssued: false,
            ResetIssued: false,
            IsElevated: false,
            ExecutionContext: "UserAgentLauncher",
            DateTimeOffset.UtcNow,
            outcomeCode,
            summary,
            null);

    private static IpcResponse Success<T>(IpcRequest request, T payload) => new(
        ProtocolConstants.Version,
        request.RequestId,
        true,
        0,
        null,
        null,
        IpcJson.ToElement(payload));

    private static IpcResponse Failure(IpcRequest request, string code, string error) => new(
        ProtocolConstants.Version,
        request.RequestId,
        false,
        0,
        code,
        error,
        null);
}

/// <summary>
/// One-shot elevated mode of PCHelper.App.exe. It accepts only a token-bound
/// Prepare diagnostic and exits as soon as the result reaches its private
/// user-agent pipe. No adapter Apply, Verify, Rollback, or Reset method is
/// reachable from this entry point.
/// </summary>
public static class InteractiveFanPreflightHost
{
    private static readonly Regex PipeNamePattern = new(
        @"\Apchelper\.interactive-preflight\.\d+\.[0-9a-f]{32}\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SessionTokenPattern = new(
        @"\A[0-9A-F]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsInvocation(IReadOnlyList<string> args) =>
        args.Any(argument => string.Equals(argument, "--interactive-fan-preflight", StringComparison.OrdinalIgnoreCase));

    public static bool TryParseInvocation(
        IReadOnlyList<string> args,
        out InteractiveFanPreflightInvocation? invocation,
        out string? error)
    {
        invocation = null;
        error = null;
        if (args.Count != 7
            || !string.Equals(args[0], "--interactive-fan-preflight", StringComparison.OrdinalIgnoreCase))
        {
            error = "The elevated diagnostic invocation is malformed.";
            return false;
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < args.Count; index += 2)
        {
            string name = args[index];
            string value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value)
                || !values.TryAdd(name, value))
            {
                error = "The elevated diagnostic invocation contains an invalid argument.";
                return false;
            }
        }

        if (!values.TryGetValue("--pipe", out string? pipeName)
            || !values.TryGetValue("--token", out string? sessionToken)
            || !values.TryGetValue("--capability", out string? capabilityId)
            || values.Count != 3
            || !PipeNamePattern.IsMatch(pipeName)
            || !SessionTokenPattern.IsMatch(sessionToken)
            || !ElevatedInteractiveFanPreflightLauncher.IsSupportedCapabilityId(capabilityId))
        {
            error = "The elevated diagnostic invocation did not pass its private-channel validation.";
            return false;
        }

        invocation = new InteractiveFanPreflightInvocation(pipeName, sessionToken, capabilityId);
        return true;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!TryParseInvocation(args, out InteractiveFanPreflightInvocation? invocation, out _)
            || invocation is null)
        {
            return 2;
        }

        InteractiveFanPreflightResultV1 result = await RunNoWritePrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
        try
        {
            NamedPipeRequestClient client = new(invocation.PipeName, TimeSpan.FromSeconds(10));
            IpcResponse response = await client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SubmitInteractiveFanPreflight,
                    new InteractivePreflightEnvelope<InteractiveFanPreflightResultV1>(invocation.SessionToken, result)),
                cancellationToken).ConfigureAwait(false);
            return response.Success ? result.Prepared ? 0 : 1 : 3;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The parent treats a missing authenticated result as a safe
            // diagnostic failure. Do not print the private token or error text.
            return 4;
        }
    }

    private static async Task<InteractiveFanPreflightResultV1> RunNoWritePrepareAsync(
        InteractiveFanPreflightInvocation invocation,
        CancellationToken cancellationToken)
    {
        (bool isElevated, string context) = GetExecutionContext();
        if (!isElevated)
        {
            return Failure(invocation.CapabilityId, isElevated, context, "HOST_NOT_ELEVATED", "The diagnostic child was not elevated, so adapter access was not attempted.", null);
        }

        string stage = "Probe";
        try
        {
            await using LibreHardwareMonitorAdapter adapter = new();
            AdapterProbeResult probe = await adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
            CapabilityDescriptor capability = probe.Capabilities.SingleOrDefault(item =>
                string.Equals(item.Id, invocation.CapabilityId, StringComparison.Ordinal))
                ?? throw new InvalidDataException("The requested bounded LPC cooling controller was not discovered by the elevated diagnostic.");
            if (capability.Domain != ControlDomain.Cooling
                || capability.ValueKind != ControlValueKind.Numeric
                || capability.Range is null
                || !ElevatedInteractiveFanPreflightLauncher.IsSupportedCapabilityId(capability.Id))
            {
                throw new InvalidDataException("The discovered capability is not eligible for the elevated no-write diagnostic.");
            }

            stage = "Prepare";
            _ = await FanCommissioningWorkflow.PrepareIdentificationPulseAsync(
                capability,
                adapter,
                cancellationToken).ConfigureAwait(false);
            return new InteractiveFanPreflightResultV1(
                InteractiveFanPreflightResultV1.CurrentSchemaVersion,
                invocation.CapabilityId,
                Prepared: true,
                ApplyIssued: false,
                VerifyIssued: false,
                RollbackIssued: false,
                ResetIssued: false,
                IsElevated: true,
                context,
                DateTimeOffset.UtcNow,
                "PREPARE_SUCCEEDED_NO_WRITE",
                "Elevated adapter Prepare completed. No apply, verify, rollback, reset, or service hardware command was issued.",
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterHostFailureV1 failure = DescribeFailure(stage, exception);
            return Failure(
                invocation.CapabilityId,
                isElevated,
                context,
                "PREPARE_FAILED_NO_WRITE",
                $"Elevated adapter Prepare failed at {failure.Stage}; no apply, verify, rollback, reset, or service hardware command was issued.",
                failure);
        }
    }

    private static (bool IsElevated, string Context) GetExecutionContext()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(ifImpersonating: false)
            ?? throw new InvalidOperationException("The elevated diagnostic process token is unavailable.");
        WindowsPrincipal principal = new(identity);
        bool elevated = identity.IsSystem || principal.IsInRole(WindowsBuiltInRole.Administrator);
        string context = identity.IsSystem
            ? "LocalSystem"
            : elevated
                ? "ElevatedInteractiveUser"
                : "StandardInteractiveUser";
        return (elevated, context);
    }

    private static AdapterHostFailureV1 DescribeFailure(string fallbackStage, Exception exception)
    {
        string stage = fallbackStage;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data["PCHelper.AdapterStage"] is string declaredStage
                && !string.IsNullOrWhiteSpace(declaredStage))
            {
                stage = declaredStage;
                break;
            }
        }

        Exception root = exception.GetBaseException();
        int hResult = root.HResult;
        return new AdapterHostFailureV1(
            "InteractiveFanPreflight",
            stage,
            root.GetType().Name,
            hResult,
            TryGetWin32Error(hResult),
            DateTimeOffset.UtcNow);
    }

    private static int? TryGetWin32Error(int hResult)
    {
        if (hResult is >= 0 and <= 0xFFFF)
        {
            return hResult;
        }

        uint unsigned = unchecked((uint)hResult);
        return (unsigned & 0xFFFF0000u) == 0x80070000u
            ? (int)(unsigned & 0xFFFFu)
            : null;
    }

    private static InteractiveFanPreflightResultV1 Failure(
        string capabilityId,
        bool isElevated,
        string context,
        string outcomeCode,
        string summary,
        AdapterHostFailureV1? failure) => new(
            InteractiveFanPreflightResultV1.CurrentSchemaVersion,
            capabilityId,
            Prepared: false,
            ApplyIssued: false,
            VerifyIssued: false,
            RollbackIssued: false,
            ResetIssued: false,
            IsElevated: isElevated,
            context,
            DateTimeOffset.UtcNow,
            outcomeCode,
            summary,
            failure);
}

public sealed record InteractiveFanPreflightInvocation(
    string PipeName,
    string SessionToken,
    string CapabilityId);
