using System.IO.Pipes;
using PCHelper.Contracts;

namespace PCHelper.Ipc;

public sealed class NamedPipeRequestClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _operationTimeout;

    /// <summary>
    /// <paramref name="connectTimeout"/> bounds only reaching the service (kept
    /// short so a down service fails fast). <paramref name="operationTimeout"/>
    /// bounds the request/response once connected, and defaults to 10 seconds
    /// because a transactional hardware apply — prepare, apply, read-back
    /// verify, and the service serialising rapid back-to-back writes on the
    /// same channel — can take several seconds. Sharing one short timeout for
    /// both used to spuriously time out those applies client-side.
    /// </summary>
    public NamedPipeRequestClient(string pipeName, TimeSpan? connectTimeout = null, TimeSpan? operationTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        await using NamedPipeClientStream client = new(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            // The service evaluates each normal client token with RunAsClient.
            // Identification is insufficient for that Windows API and can
            // surface ERROR_BAD_IMPERSONATION_LEVEL (1346), which silently
            // downgrades a legitimate operator to read-only. Private Adapter
            // Host pipes do not impersonate; they remain token-authenticated
            // and fail closed in the server identity-mode policy.
            System.Security.Principal.TokenImpersonationLevel.Impersonation);
        try
        {
            using (CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(_connectTimeout);
                await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The RigPilot service did not accept a connection within {_connectTimeout.TotalSeconds:0} s. It may be starting up or stopped.");
        }

        try
        {
            using CancellationTokenSource operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationTimeout.CancelAfter(_operationTimeout);
            await PipeFraming.WriteAsync(client, request, operationTimeout.Token).ConfigureAwait(false);
            return await PipeFraming.ReadAsync<IpcResponse>(client, operationTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The RigPilot service did not respond within {_operationTimeout.TotalSeconds:0} s. It may be busy with another hardware operation — try again in a moment.");
        }
    }

    public static IpcRequest CreateRequest<T>(
        IpcCommand command,
        T? payload = default,
        long? expectedRevision = null,
        string? idempotencyKey = null) => new(
            ProtocolConstants.Version,
            Guid.NewGuid().ToString("N"),
            command,
            expectedRevision,
            idempotencyKey,
            payload is null ? null : IpcJson.ToElement(payload));

    public static IpcRequest CreateRequest(
        IpcCommand command,
        long? expectedRevision = null,
        string? idempotencyKey = null) => new(
            ProtocolConstants.Version,
            Guid.NewGuid().ToString("N"),
            command,
            expectedRevision,
            idempotencyKey,
            null);

    public static IpcRequest CreateLegacyReadOnlyRequest(IpcCommand command) => new(
        ProtocolConstants.LegacyReadOnlyVersion,
        Guid.NewGuid().ToString("N"),
        command,
        null,
        null,
        null);
}
