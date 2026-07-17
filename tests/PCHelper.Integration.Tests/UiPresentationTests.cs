using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

public sealed class UiPresentationTests
{
    [Theory]
    [InlineData(638, 230, 2, 4, 2)]
    [InlineData(932, 230, 2, 4, 4)]
    [InlineData(610, 210, 2, 4, 2)]
    [InlineData(875, 210, 2, 4, 4)]
    [InlineData(300, 390, 1, 2, 1)]
    public void AdaptiveGridChoosesReadableColumnCount(
        double width,
        double minimumItemWidth,
        int minimumColumns,
        int maximumColumns,
        int expected)
    {
        Assert.Equal(
            expected,
            AdaptiveUniformGrid.CalculateColumnCount(width, minimumItemWidth, minimumColumns, maximumColumns));
    }

    [Fact]
    public void BuiltInProfileCardDoesNotImplyHardwareWrites()
    {
        ProfileV1 profile = new(
            ProfileV1.CurrentSchemaVersion,
            "balanced",
            "Balanced",
            "Default stock-safe policy.",
            [],
            new SafetyLimits(),
            [],
            IsBuiltIn: true,
            IsExperimental: false);

        ProfileCardDisplay card = ProfileCardDisplay.From(profile, active: false);

        Assert.Equal("Stock-safe", card.StatusLabel);
        Assert.Equal("No hardware writes in this build", card.ActionSummary);
        Assert.False(card.IsActive);
    }

    [Fact]
    public void ManualOnlyProfileCardRequiresAVisibleSessionAcknowledgement()
    {
        ProfileV2 imported = new(
            ProfileV2.CurrentSchemaVersion,
            "imported.afterburner.profile1",
            "Afterburner Profile1",
            "Imported tuning profile.",
            [new ProfileAction("voltage", "gpu", "gpu.voltage", ControlValue.FromNumeric(5), true, 0)],
            new SafetyLimits(),
            CoolingGraphId: null,
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: ["voltage"],
            AutomationReferences: [],
            IsBuiltIn: false,
            IsExperimental: true);

        ProfileCardDisplay card = ProfileCardDisplay.From(ProfileMigration.Downgrade(imported), active: false, suiteProfile: imported);

        Assert.True(card.RequiresManualAcknowledgement);
        Assert.Equal("Manual only", card.StatusLabel);
        Assert.Contains("Manual Only", card.ActionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void CoolingGraphOnlyProfileExplainsThatItMustBeAppliedManually()
    {
        ProfileV2 profile = new(
            ProfileV2.CurrentSchemaVersion,
            "adaptive.profile.case1",
            "CHA_FAN1 adaptive cooling",
            "Measured nonzero floor.",
            [],
            new SafetyLimits(),
            CoolingGraphId: "adaptive.cooling.case1",
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: [],
            AutomationReferences: [],
            IsBuiltIn: false,
            IsExperimental: true);

        ProfileCardDisplay card = ProfileCardDisplay.From(ProfileMigration.Downgrade(profile), active: false, suiteProfile: profile);

        Assert.Equal("Calibrated cooling graph; apply manually", card.ActionSummary);
        Assert.Equal("Experimental", card.StatusLabel);
    }

    [Fact]
    public void DeviceCardUsesReadableMetadataWithoutEncodingCorruption()
    {
        HardwareDevice device = new(
            "gpu:1",
            "Example GPU",
            DeviceKind.Gpu,
            "Example Vendor",
            "Model 42",
            null,
            new Dictionary<string, string>());

        DeviceDisplay display = DeviceDisplay.From(device);

        Assert.Equal("Example Vendor \u00B7 Model 42", display.Details);
        Assert.Contains("Example GPU", display.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("\u00C2", display.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceCardSurfacesReadOnlyCompatibilityClassification()
    {
        HardwareDevice device = new(
            "gpu:rtx-5090",
            "NVIDIA GeForce RTX 5090",
            DeviceKind.Gpu,
            "NVIDIA",
            "NVIDIA GeForce RTX 5090",
            null,
            new Dictionary<string, string>
            {
                ["compatibilityFamily"] = "nvidia-rtx-50",
                ["compatibilityLabel"] = "NVIDIA GeForce RTX 50 series",
                ["compatibilityEvidence"] = "Observed identity only; no write capability is inferred."
            });

        DeviceDisplay display = DeviceDisplay.From(device);

        Assert.Contains("NVIDIA GeForce RTX 50 series", display.Details, StringComparison.Ordinal);
        Assert.Contains("nvidia-rtx-50", display.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuDeviceCardSurfacesBoardPartnerIdentitySeparatelyFromGpuVendor()
    {
        HardwareDevice device = new(
            "gpu:zotac",
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

        DeviceDisplay display = DeviceDisplay.From(device);

        Assert.Contains("NVIDIA GeForce RTX 30 series", display.Details, StringComparison.Ordinal);
        Assert.Contains("ZOTAC graphics board", display.Details, StringComparison.Ordinal);
        Assert.Contains("ZOTAC graphics board", display.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityCardSurfacesRangeEvidenceAndConflictOwner()
    {
        CapabilityDescriptor capability = new(
            "fan:1",
            "adapter",
            "board",
            "Case fan",
            CapabilityAccessState.Blocked,
            AdapterExecutionContext.SystemService,
            ControlValueKind.Numeric,
            new NumericRange(20, 100, 5),
            "%",
            RiskLevel.Guarded,
            EvidenceLevel.Detected,
            "Fan Control",
            "Another writer owns this fan.",
            CanResetToDefault: true,
            ControlDomain.Cooling);

        CapabilityDisplay display = CapabilityDisplay.From(capability);

        Assert.Equal("20\u2013100 %", display.Range);
        Assert.Equal("Detected", display.Evidence);
        Assert.Equal("Blocked by Fan Control", display.Owner);
        Assert.Equal(CapabilityAccessState.Blocked, display.AccessState);
        Assert.Contains("ownership workflow", display.NextSafeStep, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Critical", display.StateTone);
    }

    [Fact]
    public void CapabilityDetailsExpansionSurvivesTheDisplayRecreationEveryRefreshCauses()
    {
        CapabilityDescriptor capability = new(
            $"fan:expander-{Guid.NewGuid():N}",
            "adapter",
            "board",
            "Case fan",
            CapabilityAccessState.ReadOnly,
            AdapterExecutionContext.SystemService,
            ControlValueKind.Numeric,
            new NumericRange(20, 100, 5),
            "%",
            RiskLevel.Guarded,
            EvidenceLevel.Detected,
            null,
            "Telemetry only.",
            CanResetToDefault: false,
            ControlDomain.Cooling);

        CapabilityDisplay first = CapabilityDisplay.From(capability);
        Assert.False(first.IsDetailsExpanded);

        first.IsDetailsExpanded = true;

        // The snapshot refresh rebuilds the list every second, producing a NEW
        // display record for the same capability; the open expander must survive.
        CapabilityDisplay recreated = CapabilityDisplay.From(capability);
        Assert.True(recreated.IsDetailsExpanded);

        recreated.IsDetailsExpanded = false;
        Assert.False(CapabilityDisplay.From(capability).IsDetailsExpanded);

        CapabilityDisplay other = CapabilityDisplay.From(capability with { Id = $"fan:other-{Guid.NewGuid():N}" });
        Assert.False(other.IsDetailsExpanded); // state is per capability, not global
    }

    [Fact]
    public void ExperimentalControlCenterRoutesOnlyBoundedNonProtectedChassisFans()
    {
        CapabilityDescriptor chassisFan = new(
            "board/control/1",
            "librehardwaremonitor",
            "board:nct6798d",
            "Fan #1",
            CapabilityAccessState.Experimental,
            AdapterExecutionContext.AdapterHost,
            ControlValueKind.Numeric,
            new NumericRange(20, 100, 1),
            "%",
            RiskLevel.Experimental,
            EvidenceLevel.SingleSystem,
            null,
            "Single-system fan-control evidence.",
            CanResetToDefault: true,
            ControlDomain.Cooling);

        ExperimentalControlDisplay display = ExperimentalControlDisplay.From(
            chassisFan,
            "Embedded Controller",
            assignment: null,
            serviceWritePathReady: true,
            sessionAcknowledged: false);

        Assert.True(display.CanOpenCoolingCommissioning);
        Assert.False(display.IsProtected);
        Assert.False(display.IsGpuCoolingControl);
        Assert.Equal("Header commissioning", display.Path);
        Assert.Contains("Session acknowledgement", display.Readiness, StringComparison.Ordinal);
        Assert.Contains("No hardware command", display.NextSafeStep, StringComparison.Ordinal);
    }

    [Fact]
    public void ExperimentalControlCenterKeepsPumpAndGpuPathsOutOfHeaderCommissioning()
    {
        CapabilityDescriptor pump = new(
            "board/control/5",
            "librehardwaremonitor",
            "board:nct6798d",
            "Fan #5",
            CapabilityAccessState.Experimental,
            AdapterExecutionContext.AdapterHost,
            ControlValueKind.Numeric,
            new NumericRange(20, 100, 1),
            "%",
            RiskLevel.Experimental,
            EvidenceLevel.SingleSystem,
            null,
            "Single-system fan-control evidence.",
            CanResetToDefault: true,
            ControlDomain.Cooling);
        CoolingOutputAssignmentV1 pumpRole = new(
            CoolingOutputAssignmentV1.CurrentSchemaVersion,
            pump.Id,
            pump.Id,
            pump.AdapterId,
            pump.DeviceId,
            null,
            "AIO_PUMP",
            CoolingOutputRole.Pump,
            DateTimeOffset.UtcNow,
            "User-confirmed pump role.");
        CapabilityDescriptor gpuFan = pump with
        {
            Id = "gpu/control/1",
            DeviceId = "gpu:rtx3090",
            Name = "GPU Fan #1"
        };

        ExperimentalControlDisplay protectedPump = ExperimentalControlDisplay.From(
            pump,
            "Embedded Controller",
            pumpRole,
            serviceWritePathReady: true,
            sessionAcknowledged: true);
        ExperimentalControlDisplay directGpu = ExperimentalControlDisplay.From(
            gpuFan,
            "NVIDIA GeForce RTX 3090",
            assignment: null,
            serviceWritePathReady: true,
            sessionAcknowledged: true);

        Assert.True(protectedPump.IsProtected);
        Assert.False(protectedPump.CanOpenCoolingCommissioning);
        Assert.Contains("pump", protectedPump.NextSafeStep, StringComparison.OrdinalIgnoreCase);
        Assert.True(directGpu.IsGpuCoolingControl);
        Assert.False(directGpu.CanOpenCoolingCommissioning);
        Assert.Contains("conservative GPU fan floor", directGpu.NextSafeStep, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConflictDiagnosticExplainsOverlapWithoutOfferingTermination()
    {
        ConflictDescriptor conflict = new(
            "fan-control",
            "Fan Control",
            "FanControl",
            ["MotherboardFan"],
            IsRunning: true,
            "Use one fan-control application at a time.");

        DiagnosticDisplay display = DiagnosticDisplay.From(conflict);

        Assert.Equal("Warning", display.Severity);
        Assert.Contains("MotherboardFan", display.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("terminate", display.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesktopOsdPresentationPreservesValidatedGridAndUnavailableValues()
    {
        OsdFrameV1 frame = new(
            OsdFrameV1.CurrentSchemaVersion,
            "osd.test",
            DateTimeOffset.UtcNow,
            [
                new OsdRenderedWidgetV1("cpu.temp", "CPU", "64.5 °C", 0, 1, "#FFB26B", SensorQuality.Good),
                new OsdRenderedWidgetV1("gpu.power", "GPU", "--", 1, 0, "#8EC5FF", SensorQuality.Unavailable)
            ]);

        IReadOnlyList<DesktopOsdCell> cells = DesktopOsdPresentation.Create(frame);

        Assert.Equal(2, cells.Count);
        Assert.Equal("CPU", cells[0].Label);
        Assert.Equal(0, cells[0].Row);
        Assert.Equal(1, cells[0].Column);
        Assert.Equal("--", cells[1].Text);
        Assert.Equal(SensorQuality.Unavailable, cells[1].Quality);
    }
}
