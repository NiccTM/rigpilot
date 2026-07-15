using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class CompetitorImporterTests
{
    [Fact]
    public void AfterburnerPreviewMapsDetectedCapabilitiesAndKeepsVoltageManualOnly()
    {
        using TemporaryFile file = new("""
            [Profile1]
            PowerLimit=90
            CoreClkBoost=150000
            CoreVoltageBoost=25
            VFCurve=A0B1C2D3
            UnsupportedLegacyFlag=1
            """);
        CapabilityDescriptorV2[] capabilities =
        [
            Capability("gpu.power.limit", "GPU power limit", new NumericRange(50, 110, 1)),
            Capability("gpu.core.clock.offset", "GPU core clock offset", new NumericRange(-500, 500, 1)),
            Capability("gpu.core.voltage", "GPU core voltage", new NumericRange(0, 100, 1), HazardClass.Voltage),
            Capability("gpu.voltage.frequency.curve", "GPU voltage frequency curve", null, HazardClass.Voltage, ControlValueKind.Text)
        ];

        ProfileImportPreviewV1 preview = MsiAfterburnerProfileImporter.Preview(file.Path, "Profile1", capabilities);

        Assert.NotNull(preview.Profile);
        Assert.Equal(4, preview.Profile.HardwareActions.Count);
        Assert.Equal(2, preview.Profile.ManualOnlyActionIds.Count);
        Assert.Contains(preview.Settings, setting => setting.SourceKey == "PowerLimit" && setting.State == ImportMappingState.Mapped);
        Assert.Contains(preview.Settings, setting => setting.SourceKey == "CoreClkBoost" && setting.Value!.Numeric == 150);
        Assert.Contains(preview.Settings, setting => setting.SourceKey == "CoreVoltageBoost" && setting.State == ImportMappingState.ManualOnly);
        Assert.Contains(preview.Settings, setting => setting.SourceKey == "UnsupportedLegacyFlag" && setting.State == ImportMappingState.Unmapped);
    }

    [Fact]
    public void AfterburnerPreviewRejectsOutOfRangeMappedValue()
    {
        using TemporaryFile file = new("[Profile1]\nPowerLimit=150\n");

        ProfileImportPreviewV1 preview = MsiAfterburnerProfileImporter.Preview(
            file.Path,
            "Profile1",
            [Capability("gpu.power.limit", "GPU power limit", new NumericRange(50, 110, 1))]);

        Assert.Empty(preview.Profile!.HardwareActions);
        Assert.Equal(ImportMappingState.Invalid, Assert.Single(preview.Settings).State);
    }

    [Fact]
    public void FanControlPreviewImportsGraphCalibrationAndAvoidBands()
    {
        using TemporaryFile file = new("""
            {
              "__VERSION__": "207",
              "FanControl": {
                "FanCurves": [
                  {
                    "Name": "CPU Curve",
                    "SelectedTempSource": { "Identifier": "sensor.cpu" },
                    "Points": ["30,25", "60,55", "85,100"],
                    "HysteresisConfig": {
                      "ResponseTimeUp": 2,
                      "ResponseTimeDown": 5,
                      "HysteresisValueUp": 1,
                      "HysteresisValueDown": 3
                    }
                  }
                ],
                "Controls": [
                  {
                    "NickName": "CPU Fan",
                    "Identifier": "control.cpu",
                    "SelectedFanCurve": { "Name": "CPU Curve" },
                    "PairedFanSensor": { "Identifier": "fan.cpu.rpm" },
                    "MinimumPercent": 25,
                    "SelectedStart": 40,
                    "SelectedOffset": 2,
                    "SelectedCommandStepUp": 8,
                    "SelectedCommandStepDown": 4,
                    "Calibration": [
                      [20, 0, false],
                      [30, 800, true],
                      [60, 1400, false],
                      [100, 2100, false]
                    ]
                  }
                ]
              }
            }
            """);

        CoolingImportPreviewV1 preview = FanControlConfigurationImporter.Preview(
            file.Path,
            new Dictionary<string, string> { ["sensor.cpu"] = "cpu.package.temperature" },
            new Dictionary<string, string> { ["control.cpu"] = "mainboard.cpu-fan.duty" });

        Assert.NotNull(preview.Graph);
        Assert.Empty(CoolingGraphValidator.Validate(preview.Graph));
        CoolingGraphOutputV1 output = Assert.Single(preview.Graph.Outputs);
        Assert.Equal("mainboard.cpu-fan.duty", output.CapabilityId);
        Assert.Equal(2, output.Offset);
        FanCalibrationV2 calibration = Assert.Single(preview.Calibrations);
        Assert.Equal(30, calibration.RestartDutyPercent);
        Assert.Equal(35, calibration.MinimumDutyPercent);
        Assert.Equal(40, calibration.KickStartDutyPercent);
        Assert.Single(calibration.AvoidBands);
    }

    private static CapabilityDescriptorV2 Capability(
        string id,
        string name,
        NumericRange? range,
        HazardClass hazard = HazardClass.Performance,
        ControlValueKind valueKind = ControlValueKind.Numeric) => new(
            CapabilityDescriptorV2.CurrentSchemaVersion,
            new CapabilityDescriptor(
                id,
                "adapter.gpu",
                "gpu.reference",
                name,
                CapabilityAccessState.Verified,
                AdapterExecutionContext.AdapterHost,
                valueKind,
                range,
                valueKind == ControlValueKind.Numeric ? "%" : null,
                hazard == HazardClass.Voltage ? RiskLevel.Critical : RiskLevel.Guarded,
                EvidenceLevel.SingleSystem,
                null,
                "Qualified for importer test.",
                true,
                ControlDomain.Gpu),
            hazard,
            "Vendor-reported bounds",
            true,
            ResetGuarantee.ReadBackVerified,
            OwnershipState.Available,
            BootApplyPolicy.Never,
            null,
            [],
            null,
            null,
            null);

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string contents)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pchelper-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
