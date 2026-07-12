using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using PCHelper.Contracts;

namespace PCHelper.Ipc;

/// <summary>Identity facts about the connected pipe client, evaluated per request.</summary>
public sealed record IpcClientContext(bool IsOperator, string? UserName);

public sealed class NamedPipeRequestServer
{
    private const string OperatorsGroupName = "PC Helper Operators";

    private readonly string _pipeName;
    private readonly Func<IpcRequest, IpcClientContext, CancellationToken, Task<IpcResponse>> _handler;
    private int _serverInstancesCreated;

    public NamedPipeRequestServer(
        string pipeName,
        Func<IpcRequest, IpcClientContext, CancellationToken, Task<IpcResponse>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public NamedPipeRequestServer(
        string pipeName,
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
        : this(pipeName, (request, _, cancellationToken) => handler(request, cancellationToken))
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        HashSet<Task> clients = [];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream server = CreateServer();
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                Task client = HandleClientAsync(server, cancellationToken);
                clients.Add(client);
                clients.RemoveWhere(task => task.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        {
            IpcRequest? request = null;
            try
            {
                request = await PipeFraming.ReadAsync<IpcRequest>(server, cancellationToken).ConfigureAwait(false);
                IpcClientContext context = EvaluateClient(server);
                IpcResponse response = request.ProtocolVersion == ProtocolConstants.Version
                    ? await _handler(request, context, cancellationToken).ConfigureAwait(false)
                    : new IpcResponse(
                        ProtocolConstants.Version,
                        request.RequestId,
                        false,
                        0,
                        "PROTOCOL_MISMATCH",
                        $"Client protocol {request.ProtocolVersion}; service protocol {ProtocolConstants.Version}.",
                        null);
                await PipeFraming.WriteAsync(server, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                IpcResponse error = new(
                    ProtocolConstants.Version,
                    request?.RequestId ?? "unknown",
                    false,
                    0,
                    "IPC_ERROR",
                    exception.Message,
                    null);
                if (server.IsConnected)
                {
                    await PipeFraming.WriteAsync(server, error, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private static IpcClientContext EvaluateClient(NamedPipeServerStream server)
    {
        bool isOperator = false;
        string? userName = null;
        try
        {
            server.RunAsClient(() =>
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                userName = identity.Name;
                if (identity.IsSystem)
                {
                    isOperator = true;
                    return;
                }

                WindowsPrincipal principal = new(identity);
                isOperator = principal.IsInRole(WindowsBuiltInRole.Administrator)
                    || IsInOperatorsGroup(principal);
            });
        }
        catch (Exception)
        {
            // Impersonation failure means the client identity is unknown; treat as read-only.
        }

        return new IpcClientContext(isOperator, userName);
    }

    private static bool IsInOperatorsGroup(WindowsPrincipal principal)
    {
        try
        {
            NTAccount account = new(Environment.MachineName, OperatorsGroupName);
            SecurityIdentifier sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            return principal.IsInRole(sid);
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);

        // Interactive users may connect for read-only data; mutating commands are
        // authorised per request in EvaluateClient, not by pipe access.
        AddRule(security, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite);

        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            AddRule(security, currentUser, PipeAccessRights.FullControl);
        }

        PipeOptions options = PipeOptions.Asynchronous;
        if (Interlocked.Increment(ref _serverInstancesCreated) == 1)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            64 * 1024,
            64 * 1024,
            security);
    }

    private static void AddRule(PipeSecurity security, IdentityReference identity, PipeAccessRights rights) => security.AddAccessRule(
        new PipeAccessRule(identity, rights, AccessControlType.Allow));
}
