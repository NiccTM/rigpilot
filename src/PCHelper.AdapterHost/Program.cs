using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

await using AdapterCoordinator coordinator = new([new LibreHardwareMonitorAdapter()]);
string pipeName = GetArgument("--pipe") ?? ProtocolConstants.AdapterHostPipeName;
string? sessionToken = GetArgument("--token")
    ?? Environment.GetEnvironmentVariable("PCHELPER_ADAPTER_HOST_TOKEN");
AdapterHostFailureV1? lastFailure = null;

if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
{
    HardwareSnapshot probe = await coordinator.CaptureAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(probe, JsonDefaults.Options));
    return;
}

if (args.Contains("--discover-controllers", StringComparer.OrdinalIgnoreCase))
{
    // This process IS the disposable controller-discovery child. If native HidSharp
    // code terminates it, the parent contains the abnormal exit; a managed failure is
    // reported as a contained result. Controllers here are read-only inventory only.
    ControllerDiscoveryResultV1 discovery = LibreHardwareMonitorAdapter.DiscoverControllersInProcess();
    Console.WriteLine(JsonSerializer.Serialize(discovery, JsonDefaults.Options));
    return;
}

if (args.Contains("--read-ryzen-smu", StringComparer.OrdinalIgnoreCase))
{
    // Disposable read-only Ryzen SMU feasibility child. Only read-class module
    // functions are referenced; a native PawnIO fault is contained by the parent.
    RyzenSmuFeasibilityV1 smu = RyzenSmuFeasibilityReader.Read();
    Console.WriteLine(JsonSerializer.Serialize(smu, JsonDefaults.Options));
    return;
}

if (args.Contains("--read-kraken", StringComparer.OrdinalIgnoreCase))
{
    // Disposable read-only Kraken X3 telemetry child. The firmware streams status
    // reports unsolicited, so this path performs no HID writes; a native fault is
    // contained by the parent exactly like the HID inventory child.
    KrakenTelemetryV1 kraken = KrakenX3TelemetryReader.Read();
    Console.WriteLine(JsonSerializer.Serialize(kraken, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-kraken-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Disposable Kraken X3 lighting child: writes one fixed-colour (or off)
    // lighting report to the sync channel. Lighting only — the pump/cooling
    // registers are untouched — and a native fault is contained by the parent.
    int argumentIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-kraken-rgb", StringComparison.OrdinalIgnoreCase));
    string value = argumentIndex >= 0 && argumentIndex + 1 < args.Length ? args[argumentIndex + 1] : string.Empty;
    bool off = string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    KrakenLightingResultV1 lighting = KrakenX3LightingWriter.Write(off ? string.Empty : value, off);
    Console.WriteLine(JsonSerializer.Serialize(lighting, JsonDefaults.Options));
    return;
}

if (args.Contains("--discover-hid", StringComparer.OrdinalIgnoreCase))
{
    // Disposable read-only HID inventory child. Native HidSharp enumeration runs here so a
    // fault is contained by the parent; the result is classification-only inventory that
    // never carries a write capability.
    HidInventoryResultV1 hid = HidPeripheralInventory.Enumerate();
    Console.WriteLine(JsonSerializer.Serialize(hid, JsonDefaults.Options));
    return;
}

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

// The Adapter Host is a private child process. Every request is independently
// authenticated with the launch-time 256-bit token in Unwrap<T>(). Do not
// impersonate the service client here: low-level driver calls must execute
// under the host process token, and the context returned by this pipe fails
// closed as non-operator.
NamedPipeRequestServer server = new(
    pipeName,
    HandleAsync,
    clientIdentityMode: NamedPipeClientIdentityMode.TokenAuthenticatedPrivateChannel);
Console.WriteLine("RigPilot adapter host is running with capability-gated mutations.");
await server.RunAsync(shutdown.Token);

async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
{
    try
    {
        return request.Command switch
        {
            IpcCommand.Handshake => Handshake(request),
            IpcCommand.GetInventory => Success(request, await coordinator.CaptureAsync(cancellationToken)),
            IpcCommand.SubscribeSensors => Success(request, await coordinator.Adapters[0].ReadSensorsAsync(cancellationToken)),
            IpcCommand.AdapterProbe => await ProbeAsync(request, cancellationToken),
            IpcCommand.AdapterReadSensors => await ReadSensorsAsync(request, cancellationToken),
            IpcCommand.AdapterPrepare => await PrepareAsync(request, cancellationToken),
            IpcCommand.AdapterApply => await ApplyAsync(request, cancellationToken),
            IpcCommand.AdapterVerify => Success(
                request,
                await coordinator.Adapters[0].VerifyAsync(
                    Unwrap<PreparedAction>(request),
                    cancellationToken)),
            IpcCommand.AdapterRollback => await RollbackAsync(request, cancellationToken),
            IpcCommand.AdapterReset => await ResetAsync(request, cancellationToken),
            IpcCommand.AdapterHealth => await HealthAsync(request, cancellationToken),
            IpcCommand.AdapterDiagnostics => Diagnostics(request),
            IpcCommand.AdapterShutdown => Shutdown(request),
            IpcCommand.GetServiceStatus => Success(request, new ServiceStatus(
                "0.4.0-alpha",
                DateTimeOffset.UtcNow,
                0,
                null,
                false,
                false,
                "Adapter host is healthy.")),
            _ => Failure(request, "NOT_IMPLEMENTED", $"Adapter-host command {request.Command} is not implemented.")
        };
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        lastFailure = DescribeFailure(request.Command, exception);
        string message = FormatFailure(lastFailure, exception);
        Console.Error.WriteLine(message);
        return Failure(request, "ADAPTER_HOST_ERROR", message);
    }
}

IpcResponse Handshake(IpcRequest request)
{
    _ = Unwrap<HandshakeRequest>(request);
    return Success(request, new HandshakeResponse(
        ProtocolConstants.Version,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.4.0-alpha",
        0));
}

async Task<IpcResponse> ProbeAsync(IpcRequest request, CancellationToken cancellationToken)
{
    _ = Unwrap<string>(request);
    return Success(request, await coordinator.Adapters[0].ProbeAsync(cancellationToken));
}

async Task<IpcResponse> ReadSensorsAsync(IpcRequest request, CancellationToken cancellationToken)
{
    _ = Unwrap<string>(request);
    return Success(request, await coordinator.Adapters[0].ReadSensorsAsync(cancellationToken));
}

async Task<IpcResponse> PrepareAsync(IpcRequest request, CancellationToken cancellationToken)
{
    // Diagnostics must describe this Prepare request, not an older rejected
    // token or unrelated adapter operation.
    lastFailure = null;
    ProfileAction action = Unwrap<ProfileAction>(request);
    return Success(request, await coordinator.Adapters[0].PrepareAsync(action, cancellationToken));
}

async Task<IpcResponse> HealthAsync(IpcRequest request, CancellationToken cancellationToken)
{
    _ = Unwrap<string>(request);
    return Success(request, await coordinator.Adapters[0].GetHealthAsync(cancellationToken));
}

IpcResponse Diagnostics(IpcRequest request)
{
    _ = Unwrap<string>(request);
    return Success(request, CaptureDiagnostics(lastFailure));
}

async Task<IpcResponse> ApplyAsync(IpcRequest request, CancellationToken cancellationToken)
{
    PreparedAction action = Unwrap<PreparedAction>(request);
    await coordinator.Adapters[0].ApplyAsync(action, cancellationToken);
    return Success(request, action.Action.Id);
}

async Task<IpcResponse> RollbackAsync(IpcRequest request, CancellationToken cancellationToken)
{
    PreparedAction action = Unwrap<PreparedAction>(request);
    await coordinator.Adapters[0].RollbackAsync(action, cancellationToken);
    return Success(request, action.Action.Id);
}

async Task<IpcResponse> ResetAsync(IpcRequest request, CancellationToken cancellationToken)
{
    AdapterResetRequest reset = Unwrap<AdapterResetRequest>(request);
    await coordinator.Adapters[0].ResetToDefaultAsync(reset.CapabilityId, cancellationToken);
    return Success(request, reset.CapabilityId);
}

IpcResponse Shutdown(IpcRequest request)
{
    _ = Unwrap<string>(request);
    shutdown.CancelAfter(TimeSpan.FromMilliseconds(100));
    return Success(request, "shutting-down");
}

T Unwrap<T>(IpcRequest request)
{
    AdapterHostEnvelope<T> envelope = IpcJson.FromElement<AdapterHostEnvelope<T>>(request.Payload)
        ?? throw new InvalidDataException("Adapter-host mutation envelope is required.");
    if (sessionToken is null || !TokensEqual(sessionToken, envelope.SessionToken))
    {
        throw new UnauthorizedAccessException("Adapter-host session token is invalid.");
    }

    return envelope.Payload;
}

static bool TokensEqual(string expected, string actual)
{
    byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
    byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
    return expectedBytes.Length == actualBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
}

string? GetArgument(string name)
{
    int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static AdapterHostDiagnosticsV1 CaptureDiagnostics(AdapterHostFailureV1? failure)
{
    using WindowsIdentity processIdentity = WindowsIdentity.GetCurrent(ifImpersonating: false)
        ?? throw new InvalidOperationException("The Adapter Host process token is unavailable.");
    WindowsPrincipal principal = new(processIdentity);
    bool isElevated = processIdentity.IsSystem
        || principal.IsInRole(WindowsBuiltInRole.Administrator);
    WindowsIdentity? threadIdentity = WindowsIdentity.GetCurrent(ifImpersonating: true);
    string threadTokenState;
    try
    {
        threadTokenState = threadIdentity is null
            ? "NoThreadImpersonationToken"
            : $"ThreadToken:{threadIdentity.ImpersonationLevel}";
    }
    finally
    {
        threadIdentity?.Dispose();
    }

    string identityKind = processIdentity.IsSystem
        ? "LocalSystem"
        : isElevated
            ? "ElevatedAdministrator"
            : "StandardUser";
    return new AdapterHostDiagnosticsV1(
        AdapterHostDiagnosticsV1.CurrentSchemaVersion,
        DateTimeOffset.UtcNow,
        Environment.ProcessId,
        identityKind,
        isElevated,
        threadTokenState,
        "PerRequestSessionToken",
        "SkippedFailClosedForTokenAuthenticatedPrivatePipe",
        failure);
}

static AdapterHostFailureV1 DescribeFailure(IpcCommand command, Exception exception)
{
    string stage = "Unhandled";
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
        command.ToString(),
        stage,
        root.GetType().Name,
        hResult,
        TryGetWin32Error(hResult),
        DateTimeOffset.UtcNow);
}

static int? TryGetWin32Error(int hResult)
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

static string FormatFailure(AdapterHostFailureV1 failure, Exception exception)
{
    if (exception is UnauthorizedAccessException)
    {
        return "Adapter-host session token is invalid.";
    }

    string win32 = failure.Win32Error is int code ? $"; Win32={code}" : string.Empty;
    return $"Adapter-host {failure.Command} failed at {failure.Stage} ({failure.ExceptionType}; HResult=0x{unchecked((uint)failure.HResult):X8}{win32}).";
}

static IpcResponse Success<T>(IpcRequest request, T payload) => new(
    ProtocolConstants.Version,
    request.RequestId,
    true,
    0,
    null,
    null,
    IpcJson.ToElement(payload));

static IpcResponse Failure(IpcRequest request, string code, string error) => new(
    ProtocolConstants.Version,
    request.RequestId,
    false,
    0,
    code,
    error,
    null);
