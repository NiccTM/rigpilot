using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class QualificationMatrixTests
{
    [Fact]
    public void ASingleUnsignedReferenceSystemCannotPassTheReleaseGate()
    {
        HardwareQualificationRecordV1 reference = Record(
            "reference",
            ProcessorQualificationFamily.RyzenZen3,
            GraphicsQualificationFamily.Rtx30,
            PlatformQualificationFamily.Amd,
            "ASUS",
            signed: false,
            controller: null);

        QualificationMatrixStatusV1 status = QualificationMatrix.Evaluate([reference]);

        Assert.False(status.CanReleaseV1);
        QualificationRequirementStatusV1 systems = Assert.Single(status.Requirements, requirement =>
            requirement.Requirement == "Independent signed physical systems");
        Assert.Equal(0, systems.Observed);
        Assert.Equal(18, systems.Required);
    }

    [Fact]
    public void AClaimedControllerRequiresTwoIndependentSuccessfulSystems()
    {
        ControllerQualificationEvidenceV1 controller = new(
            "nvidia-rtx3090-fans",
            "PCI\\VEN_10DE&DEV_2204&SUBSYS_161319DA",
            "94.02.42.00.9D",
            "32.0.16.1062",
            ApplyReadBackResetPassed: true,
            ClaimedWriteCapability: true,
            Notes: null);
        HardwareQualificationRecordV1 one = Record(
            "system-one",
            ProcessorQualificationFamily.RyzenZen3,
            GraphicsQualificationFamily.Rtx30,
            PlatformQualificationFamily.Amd,
            "ASUS",
            signed: true,
            controller);

        QualificationMatrixStatusV1 oneSystem = QualificationMatrix.Evaluate([one]);

        QualificationRequirementStatusV1 controllerRequirement = Assert.Single(oneSystem.Requirements, requirement =>
            requirement.Requirement == "Write controller family: nvidia-rtx3090-fans");
        Assert.False(controllerRequirement.Passed);
        Assert.Equal(1, controllerRequirement.Observed);

        HardwareQualificationRecordV1 two = one with
        {
            ReportId = "system-two.report",
            SystemId = "system-two"
        };
        QualificationMatrixStatusV1 twoSystems = QualificationMatrix.Evaluate([one, two]);
        controllerRequirement = Assert.Single(twoSystems.Requirements, requirement =>
            requirement.Requirement == "Write controller family: nvidia-rtx3090-fans");
        Assert.True(controllerRequirement.Passed);
        Assert.Equal(2, controllerRequirement.Observed);
    }

    [Fact]
    public void AResetFailureBlocksReleaseEvenWhenCoverageWouldOtherwisePass()
    {
        HardwareQualificationRecordV1 failing = Record(
            "failed-reset",
            ProcessorQualificationFamily.RyzenZen3,
            GraphicsQualificationFamily.Rtx30,
            PlatformQualificationFamily.Amd,
            "ASUS",
            signed: true,
            controller: null) with { RollbackPassed = false };

        QualificationMatrixStatusV1 status = QualificationMatrix.Evaluate([failing]);

        Assert.False(status.CanReleaseV1);
        Assert.Contains(status.BlockingDefects, defect => defect.Contains("rollback", StringComparison.OrdinalIgnoreCase));
    }

    private static HardwareQualificationRecordV1 Record(
        string system,
        ProcessorQualificationFamily processor,
        GraphicsQualificationFamily graphics,
        PlatformQualificationFamily platform,
        string boardVendor,
        bool signed,
        ControllerQualificationEvidenceV1? controller) => new(
            HardwareQualificationRecordV1.CurrentSchemaVersion,
            $"{system}.report",
            system,
            DateTimeOffset.UtcNow,
            processor,
            graphics,
            platform,
            boardVendor,
            signed,
            NoBsodOrUnexpectedReboot: true,
            NoStuckFan: true,
            NoUnauthorisedWrite: true,
            RollbackPassed: true,
            controller is null ? [] : [controller],
            Notes: null);
}
