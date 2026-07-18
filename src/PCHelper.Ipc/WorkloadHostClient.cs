using System.IO.Pipes;
using PCHelper.Contracts;

namespace PCHelper.Ipc;

/// <summary>
/// Short-lived authenticated requests to the per-operation GPU workload host.
/// Every request opens a fresh connection so a dead interactive-session host
/// cannot leave a pooled pipe in an apparently usable state.
/// </summary>
public sealed class WorkloadHostClient(
    WorkloadHostDescriptorV1 descriptor,
    TimeSpan? connectTimeout = null,
    TimeSpan? operationTimeout = null)
{
    private readonly TimeSpan _connectTimeout = ValidateTimeout(connectTimeout ?? TimeSpan.FromSeconds(2), nameof(connectTimeout));
    private readonly TimeSpan _operationTimeout = ValidateTimeout(operationTimeout ?? TimeSpan.FromSeconds(5), nameof(operationTimeout));

    public async Task<WorkloadHostStatusV1> SendAsync(
        WorkloadHostCommand command,
        AutoOcWorkloadMode mode,
        CancellationToken cancellationToken)
    {
        await using NamedPipeClientStream client = new(
            ".",
            descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(_connectTimeout);
            await client.ConnectAsync(connect.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Auto OC workload host did not accept a connection within {_connectTimeout.TotalSeconds:0.###} seconds.");
        }

        WorkloadHostRequestV1 request = new(
            WorkloadHostRequestV1.CurrentSchemaVersion,
            descriptor.SessionId,
            descriptor.AuthenticationToken,
            command,
            mode);
        try
        {
            using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancelAfter(_operationTimeout);
            await PipeFraming.WriteAsync(client, request, operation.Token).ConfigureAwait(false);
            WorkloadHostStatusV1 status = await PipeFraming
                .ReadAsync<WorkloadHostStatusV1>(client, operation.Token)
                .ConfigureAwait(false);
            if (!status.Authenticated || !string.Equals(status.SessionId, descriptor.SessionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Auto OC workload host failed session authentication.");
            }

            return status;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Auto OC workload host did not respond within {_operationTimeout.TotalSeconds:0.###} seconds.");
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Workload-host timeouts must be greater than zero and at most one minute.");
        }

        return timeout;
    }
}
