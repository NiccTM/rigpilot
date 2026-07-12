using System.Reflection;
using System.Text.Json;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

await using AdapterCoordinator coordinator = new([new LibreHardwareMonitorAdapter()]);

if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
{
    HardwareSnapshot probe = await coordinator.CaptureAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(probe, JsonDefaults.Options));
    return;
}

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

NamedPipeRequestServer server = new(ProtocolConstants.AdapterHostPipeName, HandleAsync);
Console.WriteLine("PC Helper adapter host is running in read-only mode.");
await server.RunAsync(shutdown.Token);

async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
{
    try
    {
        return request.Command switch
        {
            IpcCommand.Handshake => Success(request, new HandshakeResponse(
                ProtocolConstants.Version,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.2.0",
                0)),
            IpcCommand.GetInventory => Success(request, await coordinator.CaptureAsync(cancellationToken)),
            IpcCommand.SubscribeSensors => Success(request, await coordinator.Adapters[0].ReadSensorsAsync(cancellationToken)),
            IpcCommand.GetServiceStatus => Success(request, new ServiceStatus(
                "0.2.0",
                DateTimeOffset.UtcNow,
                0,
                null,
                false,
                false,
                "Adapter host is read-only.")),
            _ => Failure(request, "READ_ONLY", "The adapter host does not accept hardware mutations directly.")
        };
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Failure(request, "ADAPTER_HOST_ERROR", exception.Message);
    }
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
