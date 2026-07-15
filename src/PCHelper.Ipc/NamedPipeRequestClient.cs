using System.IO.Pipes;
using PCHelper.Contracts;

namespace PCHelper.Ipc;

public sealed class NamedPipeRequestClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public NamedPipeRequestClient(string pipeName, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
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
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await PipeFraming.WriteAsync(client, request, timeout.Token).ConfigureAwait(false);
        return await PipeFraming.ReadAsync<IpcResponse>(client, timeout.Token).ConfigureAwait(false);
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
