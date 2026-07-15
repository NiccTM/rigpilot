using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Service;

/// <summary>
/// Keeps protocol negotiation independent of adapter probing so install and
/// upgrade smoke tests can validate it without touching hardware.
/// </summary>
internal static class ServiceHandshake
{
    internal static IpcResponse Create(IpcRequest request, string serviceVersion, long stateRevision)
    {
        HandshakeRequestV2? v2 = IpcJson.FromElement<HandshakeRequestV2>(request.Payload);
        if (v2 is not null && v2.MinimumProtocolVersion > 0 && v2.MaximumProtocolVersion > 0)
        {
            if (v2.MinimumProtocolVersion > v2.MaximumProtocolVersion)
            {
                return Failure(request, "INVALID_HANDSHAKE", "The handshake protocol range is invalid.");
            }

            int selected = v2.MinimumProtocolVersion <= ProtocolConstants.Version
                && v2.MaximumProtocolVersion >= ProtocolConstants.Version
                ? ProtocolConstants.Version
                : v2.MinimumProtocolVersion <= ProtocolConstants.LegacyReadOnlyVersion
                    && v2.MaximumProtocolVersion >= ProtocolConstants.LegacyReadOnlyVersion
                    ? ProtocolConstants.LegacyReadOnlyVersion
                    : 0;
            if (selected == 0)
            {
                return Failure(
                    request,
                    "PROTOCOL_MISMATCH",
                    $"Service protocol {ProtocolConstants.Version} cannot satisfy client range {v2.MinimumProtocolVersion}-{v2.MaximumProtocolVersion}.");
            }

            return Success(request, stateRevision, new HandshakeResponseV2(
                selected,
                ProtocolConstants.LegacyReadOnlyVersion,
                serviceVersion,
                stateRevision,
                ServiceRuntimeFeatures.AdvertisedByCurrentService));
        }

        HandshakeRequest? legacy = IpcJson.FromElement<HandshakeRequest>(request.Payload);
        if (legacy is null || string.IsNullOrWhiteSpace(legacy.ClientName))
        {
            return Failure(request, "INVALID_HANDSHAKE", "A client name is required for the service handshake.");
        }

        return Success(request, stateRevision, new HandshakeResponse(
            ProtocolConstants.Version,
            serviceVersion,
            stateRevision));
    }

    private static IpcResponse Success<T>(IpcRequest request, long stateRevision, T payload) => new(
        ProtocolConstants.Version,
        request.RequestId,
        true,
        stateRevision,
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
