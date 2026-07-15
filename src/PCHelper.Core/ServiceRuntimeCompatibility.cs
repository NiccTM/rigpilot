using System.Text.RegularExpressions;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Deterministic policy for app/service compatibility. A protocol or feature
/// mismatch may preserve read-only monitoring, but it never enables a write.
/// </summary>
public static class ServiceRuntimeCompatibility
{
    public static ServiceRuntimeCompatibilityV1 Evaluate(
        string clientVersion,
        HandshakeResponseV2? handshake,
        string? unavailableReason = null)
    {
        if (handshake is null)
        {
            return Unavailable(clientVersion, unavailableReason ?? "The RigPilot service did not return a compatible handshake.");
        }

        if (handshake.SelectedProtocolVersion == ProtocolConstants.LegacyReadOnlyVersion)
        {
            return new ServiceRuntimeCompatibilityV1(
                ServiceRuntimeCompatibilityV1.CurrentSchemaVersion,
                ServiceCompatibilityState.ReadOnly,
                clientVersion,
                handshake.ServiceVersion,
                ProtocolConstants.Version,
                handshake.SelectedProtocolVersion,
                ServiceRuntimeFeatures.RequiredByDashboard.ToArray(),
                "The connected service negotiated read-only protocol 1. Install the matching RigPilot service before using profiles, commissioning, or hardware writes.");
        }

        if (handshake.SelectedProtocolVersion != ProtocolConstants.Version)
        {
            return new ServiceRuntimeCompatibilityV1(
                ServiceRuntimeCompatibilityV1.CurrentSchemaVersion,
                ServiceCompatibilityState.UpgradeRequired,
                clientVersion,
                handshake.ServiceVersion,
                ProtocolConstants.Version,
                handshake.SelectedProtocolVersion,
                ServiceRuntimeFeatures.RequiredByDashboard.ToArray(),
                $"The service negotiated protocol {handshake.SelectedProtocolVersion}; this dashboard requires protocol {ProtocolConstants.Version}. Update the installed RigPilot runtime.");
        }

        IReadOnlyList<string> advertised = handshake.Features ?? [];
        string[] missing = ServiceRuntimeFeatures.RequiredByDashboard
            .Where(required => !advertised.Contains(required, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (missing.Length > 0)
        {
            return new ServiceRuntimeCompatibilityV1(
                ServiceRuntimeCompatibilityV1.CurrentSchemaVersion,
                ServiceCompatibilityState.UpgradeRequired,
                clientVersion,
                handshake.ServiceVersion,
                ProtocolConstants.Version,
                handshake.SelectedProtocolVersion,
                missing,
                $"The service is missing required features: {string.Join(", ", missing)}. Update the installed RigPilot service; write workflows remain locked.");
        }

        if (!HasSameMajorMinor(clientVersion, handshake.ServiceVersion))
        {
            return new ServiceRuntimeCompatibilityV1(
                ServiceRuntimeCompatibilityV1.CurrentSchemaVersion,
                ServiceCompatibilityState.UpgradeRequired,
                clientVersion,
                handshake.ServiceVersion,
                ProtocolConstants.Version,
                handshake.SelectedProtocolVersion,
                [],
                $"Dashboard {clientVersion} and service {handshake.ServiceVersion} are from different release lines. Update them together before enabling service writes.");
        }

        return new ServiceRuntimeCompatibilityV1(
            ServiceRuntimeCompatibilityV1.CurrentSchemaVersion,
            ServiceCompatibilityState.Ready,
            clientVersion,
            handshake.ServiceVersion,
            ProtocolConstants.Version,
            handshake.SelectedProtocolVersion,
            [],
            $"Runtime contract ready: dashboard {clientVersion}, service {handshake.ServiceVersion}, protocol {handshake.SelectedProtocolVersion}.");
    }

    public static ServiceRuntimeCompatibilityV1 EvaluateLegacy(
        string clientVersion,
        HandshakeResponse? handshake)
    {
        if (handshake is null)
        {
            return Unavailable(clientVersion, "The service returned an empty legacy handshake.");
        }

        return new ServiceRuntimeCompatibilityV1(
            ServiceRuntimeCompatibilityV1.CurrentSchemaVersion,
            handshake.ProtocolVersion == ProtocolConstants.LegacyReadOnlyVersion
                ? ServiceCompatibilityState.ReadOnly
                : ServiceCompatibilityState.UpgradeRequired,
            clientVersion,
            handshake.ServiceVersion,
            ProtocolConstants.Version,
            handshake.ProtocolVersion,
            ServiceRuntimeFeatures.RequiredByDashboard.ToArray(),
            "Service is older than this dashboard. Monitoring may remain available; service writes are locked until both are updated together.");
    }

    public static ServiceRuntimeCompatibilityV1 Unavailable(string clientVersion, string reason) => new(
        ServiceRuntimeCompatibilityV1.CurrentSchemaVersion,
        ServiceCompatibilityState.Unavailable,
        clientVersion,
        null,
        ProtocolConstants.Version,
        null,
        ServiceRuntimeFeatures.RequiredByDashboard.ToArray(),
        reason);

    private static bool HasSameMajorMinor(string clientVersion, string serviceVersion)
    {
        Version? client = ParseVersionPrefix(clientVersion);
        Version? service = ParseVersionPrefix(serviceVersion);
        return client is not null
            && service is not null
            && client.Major == service.Major
            && client.Minor == service.Minor;
    }

    private static Version? ParseVersionPrefix(string value)
    {
        Match match = Regex.Match(value ?? string.Empty, "\\d+\\.\\d+(?:\\.\\d+){0,2}");
        return match.Success && Version.TryParse(match.Value, out Version? parsed)
            ? parsed
            : null;
    }
}
