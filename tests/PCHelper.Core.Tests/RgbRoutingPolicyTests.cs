using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class RgbRoutingPolicyTests
{
    [Fact]
    public void EnabledLampArrayUsesWindowsDynamicLightingBeforeAnyDirectPack()
    {
        IReadOnlyList<RgbRouteAssessment> routes = RgbRoutingPolicy.Assess(
            Snapshot(),
            [new RgbBridgeEndpoint("lamp-1", "Logitech G keyboard", "Logitech", 104, true, "Keyboard")],
            [],
            openRgbEnabled: false,
            openRgbConnected: false);

        RgbRouteAssessment route = Assert.Single(routes, item => item.Id == "dynamic:lamp-1");

        Assert.Equal(RgbRouteKind.WindowsDynamicLighting, route.Route);
        Assert.Equal(RgbRouteState.Ready, route.State);
        Assert.True(route.CanApply);
        Assert.Contains("priority", route.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows-owned", route.EvidenceBoundary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalOpenRgbControllerIsReadyOnlyAfterLocalBridgeNegotiation()
    {
        RgbBridgeEndpoint controller = new("0", "ASUS Aura Controller", "ASUS", 48, true, "Protocol 4");

        RgbRouteAssessment route = RgbRoutingPolicy.Assess(
                Snapshot(),
                [],
                [controller],
                openRgbEnabled: true,
                openRgbConnected: true)
            .Single(item => item.Id == "openrgb:0");

        Assert.Equal(RgbRouteKind.OpenRgbBridge, route.Route);
        Assert.Equal(RgbRouteState.Ready, route.State);
        Assert.True(route.CanApply);
    }

    [Fact]
    public void ConnectedOpenRgbServerWithoutControllersStillNeedsSetup()
    {
        RgbRouteAssessment route = RgbRoutingPolicy.Assess(
                Snapshot(),
                [],
                [],
                openRgbEnabled: true,
                openRgbConnected: true)
            .Single(item => item.Id == "rgb:openrgb-local");

        Assert.Equal(RgbRouteKind.OpenRgbBridge, route.Route);
        Assert.Equal(RgbRouteState.SetupRequired, route.State);
        Assert.False(route.CanApply);
    }

    [Fact]
    public void CompetingLightingWriterBlocksStandardRgbRoutes()
    {
        HardwareSnapshot snapshot = Snapshot(conflicts:
        [
            new ConflictDescriptor("signalrgb", "SignalRGB", "SignalRgb", ["Lighting"], true, "Close the overlapping writer.")
        ]);

        IReadOnlyList<RgbRouteAssessment> routes = RgbRoutingPolicy.Assess(
            snapshot,
            [new RgbBridgeEndpoint("lamp-1", "RGB mouse", "Razer", 8, true, "Mouse")],
            [new RgbBridgeEndpoint("0", "OpenRGB controller", null, 12, true, "Protocol 4")],
            openRgbEnabled: true,
            openRgbConnected: true);

        Assert.All(
            routes.Where(route => route.Id is "dynamic:lamp-1" or "openrgb:0"),
            route =>
            {
                Assert.Equal(RgbRouteKind.BlockedByOwner, route.Route);
                Assert.Equal(RgbRouteState.Blocked, route.State);
                Assert.False(route.CanApply);
                Assert.Contains("SignalRGB", route.Summary, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void VendorOwnerBlocksOnlyItsMatchingEndpointFamily()
    {
        HardwareSnapshot snapshot = Snapshot(conflicts:
        [
            new ConflictDescriptor(
                "nzxt-cam",
                "NZXT CAM",
                "NZXT CAM",
                ["Aio", "Lighting"],
                true,
                "Use one owner for the Kraken.")
        ]);

        IReadOnlyList<RgbRouteAssessment> routes = RgbRoutingPolicy.Assess(
            snapshot,
            [],
            [
                new RgbBridgeEndpoint("0", "NZXT Kraken X63", "NZXT", 16, true, "OpenRGB"),
                new RgbBridgeEndpoint("1", "ASUS Aura LED Controller", "ASUS", 48, true, "OpenRGB")
            ],
            openRgbEnabled: true,
            openRgbConnected: true);

        RgbRouteAssessment kraken = routes.Single(route => route.Id == "openrgb:0");
        RgbRouteAssessment aura = routes.Single(route => route.Id == "openrgb:1");
        Assert.Equal(RgbRouteState.Blocked, kraken.State);
        Assert.Contains("NZXT CAM", kraken.Summary, StringComparison.Ordinal);
        Assert.Equal(RgbRouteState.Ready, aura.State);
    }

    [Fact]
    public void ConnectedLocalOpenRgbBridgePausesDynamicLightingToAvoidUnknownOverlap()
    {
        IReadOnlyList<RgbRouteAssessment> routes = RgbRoutingPolicy.Assess(
            Snapshot(),
            [new RgbBridgeEndpoint("lamp-1", "LampArray keyboard", null, 24, true, "Keyboard")],
            [new RgbBridgeEndpoint("0", "OpenRGB controller", null, 24, true, "Protocol 4")],
            openRgbEnabled: true,
            openRgbConnected: true);

        RgbRouteAssessment dynamicRoute = routes.Single(item => item.Id == "dynamic:lamp-1");
        RgbRouteAssessment openRgbRoute = routes.Single(item => item.Id == "openrgb:0");

        Assert.Equal(RgbRouteState.Blocked, dynamicRoute.State);
        Assert.Contains("local OpenRGB bridge", dynamicRoute.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RgbRouteState.Ready, openRgbRoute.State);
    }

    [Fact]
    public void VendorInventoryMatchStaysReadOnlyUntilExactAdapterEvidenceExists()
    {
        HardwareDevice device = new(
            "lighting:asus",
            "ASUS Aura controller",
            DeviceKind.Lighting,
            "ASUS",
            "Aura",
            "USB\\VID_0B05&PID_18F3",
            new Dictionary<string, string>
            {
                ["compatibilityFamily"] = "asus-aura-controller",
                ["compatibilityLabel"] = "ASUS Aura controller"
            });
        CapabilityDescriptor capability = new(
            "peripheral.readonly:lighting:asus",
            "peripheral",
            device.Id,
            "ASUS Aura direct control",
            CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.AdapterHost,
            ControlValueKind.Colour,
            null,
            null,
            RiskLevel.Guarded,
            EvidenceLevel.Detected,
            null,
            "Direct USB protocol is unqualified.",
            CanResetToDefault: false,
            ControlDomain.Lighting);

        RgbRouteAssessment route = RgbRoutingPolicy.Assess(
                Snapshot([device], [capability]),
                [],
                [],
                openRgbEnabled: false,
                openRgbConnected: false)
            .Single(item => item.Id == "direct:lighting:asus");

        Assert.Equal(RgbRouteKind.DirectQualification, route.Route);
        Assert.Equal(RgbRouteState.ReadOnly, route.State);
        Assert.False(route.CanApply);
        Assert.Contains("does not enable", route.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct USB/HID output is disabled", route.EvidenceBoundary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifiedDirectAdapterIsShownAsTheOnlyNativeRoute()
    {
        HardwareDevice device = new(
            "lighting:verified",
            "Verified RGB controller",
            DeviceKind.Lighting,
            "Example",
            "Controller",
            null,
            new Dictionary<string, string>());
        CapabilityDescriptor capability = new(
            "lighting.verified",
            "verified.adapter",
            device.Id,
            "Static RGB",
            CapabilityAccessState.Verified,
            AdapterExecutionContext.AdapterHost,
            ControlValueKind.Colour,
            null,
            null,
            RiskLevel.Guarded,
            EvidenceLevel.TwoSystemCertified,
            null,
            "Apply/read-back/reset passed.",
            CanResetToDefault: true,
            ControlDomain.Lighting);

        RgbRouteAssessment route = RgbRoutingPolicy.Assess(
                Snapshot([device], [capability]),
                [],
                [],
                openRgbEnabled: false,
                openRgbConnected: false)
            .Single(item => item.Id == "direct:lighting:verified");

        Assert.Equal(RgbRouteKind.QualifiedAdapter, route.Route);
        Assert.Equal(RgbRouteState.Ready, route.State);
        Assert.True(route.CanApply);
    }

    [Fact]
    public void RecognizedGraphicsBoardGetsAnRgbQualificationRouteWithoutNativeWrites()
    {
        HardwareDevice gpu = new(
            "gpu:zotac-3090",
            "NVIDIA GeForce RTX 3090",
            DeviceKind.Gpu,
            "NVIDIA",
            "NVIDIA GeForce RTX 3090",
            "PCI\\VEN_10DE&DEV_2204&SUBSYS_161319DA",
            new Dictionary<string, string>
            {
                ["compatibilityLabel"] = "NVIDIA GeForce RTX 30 series",
                ["boardPartnerLabel"] = "ZOTAC graphics board"
            });
        CapabilityDescriptor eligibility = new(
            "gpu.rgb.eligibility:zotac-gpu-board:gpu:zotac-3090",
            "vendor.control-eligibility",
            gpu.Id,
            "ZOTAC graphics board RGB eligibility",
            CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.UserSession,
            ControlValueKind.Colour,
            null,
            null,
            RiskLevel.Guarded,
            EvidenceLevel.Detected,
            null,
            "Native protocol is unqualified.",
            CanResetToDefault: false,
            ControlDomain.Lighting);

        RgbRouteAssessment route = RgbRoutingPolicy.Assess(
                Snapshot([gpu], [eligibility]),
                [],
                [],
                openRgbEnabled: false,
                openRgbConnected: false)
            .Single(item => item.Id == "direct:gpu:zotac-3090");

        Assert.Equal(RgbRouteState.ReadOnly, route.State);
        Assert.Equal(RgbRouteKind.DirectQualification, route.Route);
        Assert.Contains("ZOTAC graphics board", route.Summary, StringComparison.Ordinal);
        Assert.False(route.CanApply);
    }

    private static HardwareSnapshot Snapshot(
        IReadOnlyList<HardwareDevice>? devices = null,
        IReadOnlyList<CapabilityDescriptor>? capabilities = null,
        IReadOnlyList<ConflictDescriptor>? conflicts = null) => new(
        DateTimeOffset.UtcNow,
        devices ?? [],
        capabilities ?? [],
        [],
        conflicts ?? [],
        [],
        []);
}
