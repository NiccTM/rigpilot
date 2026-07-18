using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Describes a non-privileged RGB endpoint discovered through a standard
/// Windows path or a user-supplied local bridge. It deliberately contains no
/// USB protocol data and cannot grant a direct-device write capability.
/// </summary>
public sealed record RgbBridgeEndpoint(
    string Id,
    string Name,
    string? Manufacturer,
    int LedCount,
    bool IsEnabled,
    string Detail);

public enum RgbRouteKind
{
    WindowsDynamicLighting,
    OpenRgbBridge,
    QualifiedAdapter,
    DirectQualification,
    BlockedByOwner,
    Unsupported
}

public enum RgbRouteState
{
    Ready,
    SetupRequired,
    ReadOnly,
    Blocked,
    Unsupported
}

/// <summary>
/// An evidence-backed route for one RGB endpoint. A Ready route means the
/// selected standard bridge is presently usable; it is not a claim that every
/// vendor model or firmware revision has been certified.
/// </summary>
public sealed record RgbRouteAssessment(
    string Id,
    string DeviceName,
    string Manufacturer,
    RgbRouteKind Route,
    RgbRouteState State,
    int LedCount,
    string Summary,
    string NextAction)
{
    public string RouteLabel => Route switch
    {
        RgbRouteKind.WindowsDynamicLighting => "Windows Dynamic Lighting",
        RgbRouteKind.OpenRgbBridge => "Local OpenRGB bridge",
        RgbRouteKind.QualifiedAdapter => "Qualified device adapter",
        RgbRouteKind.DirectQualification => "Direct adapter qualification",
        RgbRouteKind.BlockedByOwner => "Blocked by another lighting writer",
        _ => "Unsupported"
    };

    public string StateLabel => State switch
    {
        RgbRouteState.Ready => "Ready",
        RgbRouteState.SetupRequired => "Setup needed",
        RgbRouteState.ReadOnly => "Read-only",
        RgbRouteState.Blocked => "Blocked",
        _ => "Unsupported"
    };

    public string StateTone => State switch
    {
        RgbRouteState.Ready => "Safe",
        RgbRouteState.SetupRequired or RgbRouteState.ReadOnly => "Warning",
        RgbRouteState.Blocked or RgbRouteState.Unsupported => "Critical",
        _ => "Neutral"
    };

    public bool CanApply => State == RgbRouteState.Ready;

    /// <summary>
    /// Makes the trust boundary visible in the UI. A ready standard bridge is
    /// limited to its own Windows/OpenRGB contract; a direct route remains an
    /// evidence workflow until an exact adapter earns a write capability.
    /// </summary>
    public string EvidenceBoundary => Route switch
    {
        RgbRouteKind.WindowsDynamicLighting => "Windows-owned LampArray bridge; device priority can still change outside RigPilot.",
        RgbRouteKind.OpenRgbBridge => "User-enabled loopback SDK bridge; no raw USB/HID protocol is sent by RigPilot.",
        RgbRouteKind.QualifiedAdapter => "Exact adapter capability is responsible for bounded apply, read-back, reset, and ownership.",
        RgbRouteKind.DirectQualification => "Inventory evidence only; direct USB/HID output is disabled pending exact-device qualification.",
        RgbRouteKind.BlockedByOwner => "Another process owns the overlapping lighting resource; no output is sent.",
        _ => "No supported lighting transport is available for this endpoint."
    };
}

/// <summary>
/// Routes RGB inventory through open, inspectable control paths in priority
/// order. It never probes a proprietary USB/HID protocol and never uses a
/// manufacturer-name match to enable an output.
/// </summary>
public static class RgbRoutingPolicy
{
    public static IReadOnlyList<RgbRouteAssessment> Assess(
        HardwareSnapshot? snapshot,
        IEnumerable<RgbBridgeEndpoint>? dynamicLightingEndpoints,
        IEnumerable<RgbBridgeEndpoint>? openRgbEndpoints,
        bool openRgbEnabled,
        bool openRgbConnected)
    {
        RgbBridgeEndpoint[] dynamicLighting = (dynamicLightingEndpoints ?? [])
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Id))
            .GroupBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RgbBridgeEndpoint[] openRgb = (openRgbEndpoints ?? [])
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Id))
            .GroupBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<ConflictDescriptor> conflicts = snapshot?.Conflicts ?? [];
        string[] broadOwners = OwnerNames(RgbConflictPolicy.FindBroadBlockingOwners(conflicts));
        string[] dynamicPlaceholderOwners = openRgbEnabled && openRgbConnected && openRgb.Length > 0
            ? broadOwners.Append("the connected local OpenRGB bridge").ToArray()
            : broadOwners;
        List<RgbRouteAssessment> routes = [];

        if (dynamicLighting.Length == 0)
        {
            routes.Add(new RgbRouteAssessment(
                "rgb:dynamic-lighting",
                "Windows Dynamic Lighting",
                "Windows HID LampArray",
                dynamicPlaceholderOwners.Length == 0 ? RgbRouteKind.WindowsDynamicLighting : RgbRouteKind.BlockedByOwner,
                dynamicPlaceholderOwners.Length == 0 ? RgbRouteState.SetupRequired : RgbRouteState.Blocked,
                0,
                dynamicPlaceholderOwners.Length == 0
                    ? "No HID LampArray endpoint has been reported by Windows yet. This open standard is the first choice for compatible peripherals and chassis devices."
                    : OwnerSummary(dynamicPlaceholderOwners),
                dynamicPlaceholderOwners.Length == 0
                    ? "Probe Windows Dynamic Lighting after connecting or enabling a LampArray-compatible device."
                    : "Close or give control back from the listed lighting writer, then probe again."));
        }
        else
        {
            routes.AddRange(dynamicLighting.Select(endpoint => BuildDynamicLightingRoute(
                endpoint,
                AddOpenRgbOwner(
                    OwnerNames(RgbConflictPolicy.FindBlockingOwners(
                        conflicts,
                        endpoint.Name,
                        endpoint.Manufacturer)),
                    openRgbEnabled && openRgbConnected && openRgb.Length > 0))));
        }

        if (openRgb.Length == 0)
        {
            routes.Add(new RgbRouteAssessment(
                "rgb:openrgb-local",
                "Local OpenRGB server",
                "OpenRGB",
                broadOwners.Length == 0 ? RgbRouteKind.OpenRgbBridge : RgbRouteKind.BlockedByOwner,
                broadOwners.Length > 0 ? RgbRouteState.Blocked : RgbRouteState.SetupRequired,
                0,
                broadOwners.Length > 0
                    ? OwnerSummary(broadOwners)
                    : openRgbEnabled
                        ? "The local SDK bridge is enabled but has not enumerated a controller in this session."
                        : "The optional local OpenRGB SDK bridge is disabled. RigPilot does not bundle or contact a remote OpenRGB server.",
                broadOwners.Length > 0
                    ? "Close or give control back from the listed lighting writer, then retry the local bridge."
                    : openRgbEnabled
                        ? "Start the user-installed OpenRGB SDK server on 127.0.0.1:6742, then test it here."
                        : "Install and configure OpenRGB yourself, then explicitly enable the loopback SDK bridge."));
        }
        else
        {
            routes.AddRange(openRgb.Select(endpoint => BuildOpenRgbRoute(
                endpoint,
                OwnerNames(RgbConflictPolicy.FindBlockingOwners(
                    conflicts,
                    endpoint.Name,
                    endpoint.Manufacturer)),
                openRgbEnabled,
                openRgbConnected)));
        }

        if (snapshot is not null)
        {
            routes.AddRange(BuildDirectInventoryRoutes(snapshot));
        }

        return routes
            .OrderBy(route => RouteRank(route.Route))
            .ThenBy(route => route.State == RgbRouteState.Ready ? 0 : 1)
            .ThenBy(route => route.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static RgbRouteAssessment BuildDynamicLightingRoute(
        RgbBridgeEndpoint endpoint,
        string[] owners)
    {
        if (owners.Length > 0)
        {
            return new RgbRouteAssessment(
                $"dynamic:{endpoint.Id}",
                endpoint.Name,
                endpoint.Manufacturer ?? "Windows HID LampArray",
                RgbRouteKind.BlockedByOwner,
                RgbRouteState.Blocked,
                Math.Max(0, endpoint.LedCount),
                OwnerSummary(owners),
                "Close or give control back from the listed lighting writer, then apply the Windows scene.");
        }

        return endpoint.IsEnabled
            ? new RgbRouteAssessment(
                $"dynamic:{endpoint.Id}",
                endpoint.Name,
                endpoint.Manufacturer ?? "Windows HID LampArray",
                RgbRouteKind.WindowsDynamicLighting,
                RgbRouteState.Ready,
                Math.Max(0, endpoint.LedCount),
                $"Windows reports {Math.Max(0, endpoint.LedCount)} controllable lamp(s). {endpoint.Detail} Foreground/background priority can still deny ownership at apply time.",
                "Map physical zones, save a scene, then apply it through Windows Dynamic Lighting.")
            : new RgbRouteAssessment(
                $"dynamic:{endpoint.Id}",
                endpoint.Name,
                endpoint.Manufacturer ?? "Windows HID LampArray",
                RgbRouteKind.WindowsDynamicLighting,
                RgbRouteState.SetupRequired,
                Math.Max(0, endpoint.LedCount),
                $"Windows found this LampArray endpoint but reports it disabled. {endpoint.Detail}",
                "Enable Dynamic Lighting for this device in Windows Settings, then probe again.");
    }

    private static RgbRouteAssessment BuildOpenRgbRoute(
        RgbBridgeEndpoint endpoint,
        string[] owners,
        bool openRgbEnabled,
        bool openRgbConnected)
    {
        if (owners.Length > 0)
        {
            return new RgbRouteAssessment(
                $"openrgb:{endpoint.Id}",
                endpoint.Name,
                endpoint.Manufacturer ?? "OpenRGB controller",
                RgbRouteKind.BlockedByOwner,
                RgbRouteState.Blocked,
                Math.Max(0, endpoint.LedCount),
                OwnerSummary(owners),
                "Close or give control back from the listed lighting writer before using the local OpenRGB bridge.");
        }

        bool ready = openRgbEnabled && openRgbConnected && endpoint.IsEnabled;
        return new RgbRouteAssessment(
            $"openrgb:{endpoint.Id}",
            endpoint.Name,
            endpoint.Manufacturer ?? "OpenRGB controller",
            RgbRouteKind.OpenRgbBridge,
            ready ? RgbRouteState.Ready : RgbRouteState.SetupRequired,
            Math.Max(0, endpoint.LedCount),
            ready
                ? $"The local OpenRGB SDK enumerated this controller with {Math.Max(0, endpoint.LedCount)} LED(s). {endpoint.Detail}"
                : "A prior controller identity is retained only as a route hint; the local SDK is not ready in this session.",
            ready
                ? "Apply a static scene through the local bridge or use the controller in a saved layout."
                : "Enable and test the local OpenRGB bridge before sending lighting output.");
    }

    private static IEnumerable<RgbRouteAssessment> BuildDirectInventoryRoutes(
        HardwareSnapshot snapshot)
    {
        Dictionary<string, CapabilityDescriptor[]> capabilitiesByDevice = snapshot.Capabilities
            .Where(capability => capability.Domain == ControlDomain.Lighting)
            .GroupBy(capability => capability.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        return snapshot.Devices
            .Where(device => IsRgbInventoryDevice(device, capabilitiesByDevice))
            .Where(device => !IsLampArrayInventoryDevice(device))
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(device => BuildDirectInventoryRoute(
                device,
                capabilitiesByDevice.GetValueOrDefault(device.Id, []),
                OwnerNames(RgbConflictPolicy.FindBlockingOwners(
                    snapshot.Conflicts,
                    device.Name,
                    device.Manufacturer,
                    device.Model))));
    }

    private static bool IsRgbInventoryDevice(
        HardwareDevice device,
        Dictionary<string, CapabilityDescriptor[]> capabilitiesByDevice) => device.Kind == DeviceKind.Lighting
        || device.Kind == DeviceKind.Controller
            && (capabilitiesByDevice.ContainsKey(device.Id)
                || device.Properties.ContainsKey("compatibilityFamily"))
        || device.Kind == DeviceKind.Gpu
            && (capabilitiesByDevice.ContainsKey(device.Id)
                || device.Properties.ContainsKey("boardPartnerLabel"));

    private static bool IsLampArrayInventoryDevice(HardwareDevice device) =>
        device.Properties.TryGetValue("compatibilityFamily", out string? family)
        && string.Equals(family, "hid-lamparray", StringComparison.OrdinalIgnoreCase);

    private static RgbRouteAssessment BuildDirectInventoryRoute(
        HardwareDevice device,
        CapabilityDescriptor[] capabilities,
        string[] owners)
    {
        string manufacturer = string.IsNullOrWhiteSpace(device.Manufacturer) ? "Unknown manufacturer" : device.Manufacturer;
        string family = device.Properties.TryGetValue("compatibilityLabel", out string? label)
            ? label
            : device.Properties.TryGetValue("compatibilityFamily", out string? familyId)
                ? familyId
                : "Observed RGB-class controller";
        if (device.Properties.TryGetValue("boardPartnerLabel", out string? boardPartner)
            && !string.IsNullOrWhiteSpace(boardPartner))
        {
            family = $"{family} · {boardPartner}";
        }
        CapabilityDescriptor? capability = capabilities
            .OrderBy(capability => CapabilityRank(capability.State))
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (owners.Length > 0 && capability?.State is CapabilityAccessState.Verified or CapabilityAccessState.Experimental)
        {
            return new RgbRouteAssessment(
                $"direct:{device.Id}",
                device.Name,
                manufacturer,
                RgbRouteKind.BlockedByOwner,
                RgbRouteState.Blocked,
                0,
                $"{family}. {OwnerSummary(owners)}",
                "Release the conflicting owner and verify a firmware-default reset before attempting this qualified adapter.");
        }

        if (capability?.State == CapabilityAccessState.Verified)
        {
            return new RgbRouteAssessment(
                $"direct:{device.Id}",
                device.Name,
                manufacturer,
                RgbRouteKind.QualifiedAdapter,
                RgbRouteState.Ready,
                0,
                $"{family}. This exact controller has a verified built-in lighting capability with read-back and reset evidence.",
                "Use the qualified adapter control exposed for this device; preserve its ownership lease.");
        }

        if (capability?.State is CapabilityAccessState.Blocked or CapabilityAccessState.Faulted)
        {
            return new RgbRouteAssessment(
                $"direct:{device.Id}",
                device.Name,
                manufacturer,
                RgbRouteKind.BlockedByOwner,
                RgbRouteState.Blocked,
                0,
                $"{family}. {capability.Reason}",
                capability.ConflictOwner is null
                    ? "Review the capability decision and recover firmware-default control before retrying."
                    : $"Resolve ownership held by {capability.ConflictOwner}, then re-probe the controller." );
        }

        if (capability?.State == CapabilityAccessState.Unsupported)
        {
            return new RgbRouteAssessment(
                $"direct:{device.Id}",
                device.Name,
                manufacturer,
                RgbRouteKind.Unsupported,
                RgbRouteState.Unsupported,
                0,
                $"{family}. The installed adapter reports that this exact controller cannot be controlled safely.",
                "Use Windows Dynamic Lighting or a user-installed OpenRGB server if it exposes this device; otherwise collect a read-only qualification trace." );
        }

        string detail = capability is null
            ? "No direct-device lighting capability has been qualified."
            : capability.State == CapabilityAccessState.Experimental
                ? $"The available capability is Experimental: {capability.Reason}"
                : capability.Reason;
        return new RgbRouteAssessment(
            $"direct:{device.Id}",
            device.Name,
            manufacturer,
            RgbRouteKind.DirectQualification,
            RgbRouteState.ReadOnly,
            0,
            $"{family}. {detail} A vendor-name match is inventory evidence only and does not enable a raw USB/HID write.",
            "Prefer Windows Dynamic Lighting or the local OpenRGB bridge. For direct support, collect static-scene apply, read-back, reset, timeout, and ownership evidence for this exact controller." );
    }

    private static string[] OwnerNames(IEnumerable<ConflictDescriptor> conflicts) => conflicts
        .Select(conflict => conflict.DisplayName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(owner => owner, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string[] AddOpenRgbOwner(string[] owners, bool addOpenRgbOwner) => addOpenRgbOwner
        ? owners.Append("the connected local OpenRGB bridge").ToArray()
        : owners;

    private static string OwnerSummary(string[] owners) =>
        $"Lighting ownership is currently held by {string.Join(", ", owners)}. RigPilot will not send overlapping output.";

    private static int RouteRank(RgbRouteKind route) => route switch
    {
        RgbRouteKind.WindowsDynamicLighting => 0,
        RgbRouteKind.OpenRgbBridge => 1,
        RgbRouteKind.QualifiedAdapter => 2,
        RgbRouteKind.DirectQualification => 3,
        RgbRouteKind.BlockedByOwner => 4,
        _ => 5
    };

    private static int CapabilityRank(CapabilityAccessState state) => state switch
    {
        CapabilityAccessState.Verified => 0,
        CapabilityAccessState.Experimental => 1,
        CapabilityAccessState.ReadOnly => 2,
        CapabilityAccessState.Blocked => 3,
        CapabilityAccessState.Faulted => 4,
        _ => 5
    };
}
