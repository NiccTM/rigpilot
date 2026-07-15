using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using PCHelper.Contracts;

namespace PCHelper.Ipc;

/// <summary>Identity facts about the connected pipe client, evaluated per request.</summary>
public sealed record IpcClientContext(
    bool IsOperator,
    string? UserName,
    string? UserSid = null,
    bool IsAppContainer = false);

/// <summary>
/// Controls whether the server evaluates the connected client's Windows token.
/// The normal service and user-agent pipes must evaluate it. The only allowed
/// exception is a private child-process pipe that authenticates every request
/// with an independent, high-entropy session token. That mode deliberately
/// returns a non-operator context so it cannot grant authority by accident.
/// </summary>
public enum NamedPipeClientIdentityMode
{
    Evaluate = 0,
    TokenAuthenticatedPrivateChannel = 1
}

public sealed class NamedPipeRequestServer
{
    private const string OperatorsGroupName = "PC Helper Operators";

    private readonly string _pipeName;
    private readonly Func<IpcRequest, IpcClientContext, CancellationToken, Task<IpcResponse>> _handler;
    private readonly SecurityIdentifier[] _allowedAppContainerSids;
    private readonly NamedPipeClientIdentityMode _clientIdentityMode;
    private int _serverInstancesCreated;

    public NamedPipeRequestServer(
        string pipeName,
        Func<IpcRequest, IpcClientContext, CancellationToken, Task<IpcResponse>> handler,
        IEnumerable<SecurityIdentifier>? allowedAppContainerSids = null,
        NamedPipeClientIdentityMode clientIdentityMode = NamedPipeClientIdentityMode.Evaluate)
    {
        if (!Enum.IsDefined(clientIdentityMode))
        {
            throw new ArgumentOutOfRangeException(nameof(clientIdentityMode));
        }

        _pipeName = pipeName;
        _handler = handler;
        _allowedAppContainerSids = allowedAppContainerSids?
            .GroupBy(sid => sid.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray()
            ?? [];
        _clientIdentityMode = clientIdentityMode;
    }

    public NamedPipeRequestServer(
        string pipeName,
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        IEnumerable<SecurityIdentifier>? allowedAppContainerSids = null,
        NamedPipeClientIdentityMode clientIdentityMode = NamedPipeClientIdentityMode.Evaluate)
        : this(
            pipeName,
            (request, _, cancellationToken) => handler(request, cancellationToken),
            allowedAppContainerSids,
            clientIdentityMode)
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested)
                {
                    // Windows can surface cancellation as "The pipe is being
                    // closed" rather than OperationCanceledException. A host
                    // shutdown is normal and must not become an unhandled
                    // process exception.
                    await server.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (IOException)
                {
                    // A client can disappear while the server instance is
                    // still accepting. Scope that race to this one instance
                    // and open a fresh one instead of terminating the service
                    // or Adapter Host.
                    await server.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    continue;
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
                IpcClientContext context = _clientIdentityMode == NamedPipeClientIdentityMode.Evaluate
                    ? EvaluateClient(server)
                    : new IpcClientContext(IsOperator: false, UserName: null);
                bool currentProtocol = request.ProtocolVersion == ProtocolConstants.Version;
                bool legacyReadOnly = request.ProtocolVersion == ProtocolConstants.LegacyReadOnlyVersion
                    && IpcCommandPolicy.IsReadOnly(request.Command);
                IpcResponse response;
                if (currentProtocol || legacyReadOnly)
                {
                    response = await _handler(request, context, cancellationToken).ConfigureAwait(false);
                    if (legacyReadOnly)
                    {
                        response = response with { ProtocolVersion = ProtocolConstants.LegacyReadOnlyVersion };
                    }
                }
                else
                {
                    response = new IpcResponse(
                        ProtocolConstants.Version,
                        request.RequestId,
                        false,
                        0,
                        "PROTOCOL_MISMATCH",
                        $"Client protocol {request.ProtocolVersion}; service protocol {ProtocolConstants.Version}. Protocol 1 is read-only during the compatibility window.",
                        null);
                }
                await PipeFraming.WriteAsync(server, response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A service stop or a client that disappears while a response is
                // being written must not fault the long-running server task.
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
                    await TryWriteResponseAsync(server, error).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task TryWriteResponseAsync(NamedPipeServerStream server, IpcResponse response)
    {
        try
        {
            await PipeFraming.WriteAsync(server, response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The error response is best-effort. A broken or disposed client pipe
            // is scoped to that connection and cannot be allowed to stop the host.
        }
    }

    private static IpcClientContext EvaluateClient(NamedPipeServerStream server)
    {
        bool isOperator = false;
        string? userName = null;
        string? userSid = null;
        bool isAppContainer = false;
        try
        {
            server.RunAsClient(() =>
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                userName = identity.Name;
                userSid = identity.User?.Value;
                isAppContainer = userSid?.StartsWith("S-1-15-2-", StringComparison.Ordinal) == true;
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

        return new IpcClientContext(isOperator, userName, userSid, isAppContainer);
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

        // AppContainer clients are not authenticated users. Microsoft requires the
        // World SID plus the exact package SID for a UWP Game Bar widget to reach a
        // desktop named pipe. The handler still authorises the package SID per
        // request, so this ACL never grants hardware-service access by itself.
        if (_allowedAppContainerSids.Length > 0)
        {
            AddRule(security, new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite);
            foreach (SecurityIdentifier sid in _allowedAppContainerSids)
            {
                AddRule(security, sid, PipeAccessRights.ReadWrite);
            }
        }

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
