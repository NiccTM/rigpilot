using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The draft flow exists so community contributors can prepare evidence, but it
/// must never be able to satisfy the 1.0 gate: drafts are hard-coded unsigned,
/// require all four witnessed attestations, and refuse unclassifiable hardware.
/// </summary>
public sealed class QualificationRecordDraftTests
{
    private static QualificationSystemIdentity ReferenceRig => new(
        "AMD Ryzen 7 5800X 8-Core Processor",
        "NVIDIA GeForce RTX 3090",
        "ASUSTeK COMPUTER INC.",
        "ROG STRIX X570-E GAMING");

    private static QualificationAttestations AllAttested => new(true, true, true, true);

    [Fact]
    public void BuildClassifiesTheReferenceRigAndForcesTheUnsignedDraftMarkers()
    {
        HardwareQualificationRecordV1 draft = QualificationRecordDraftBuilder.Build(
            ReferenceRig, AllAttested, DateTimeOffset.UtcNow, "24h soak completed.");

        Assert.Equal(ProcessorQualificationFamily.RyzenZen3, draft.ProcessorFamily);
        Assert.Equal(GraphicsQualificationFamily.Rtx30, draft.GraphicsFamily);
        Assert.Equal(PlatformQualificationFamily.Amd, draft.PlatformFamily);
        Assert.Equal("ASUS", draft.MotherboardVendor);
        Assert.False(draft.SignedProductionBuild);
        Assert.StartsWith("draft-", draft.ReportId, StringComparison.Ordinal);
        Assert.StartsWith(QualificationRecordDraftBuilder.DraftNotePrefix, draft.Notes, StringComparison.Ordinal);
        Assert.Contains("24h soak completed.", draft.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void ADraftCanNeverAdvanceTheSignedSystemRequirement()
    {
        HardwareQualificationRecordV1 draft = QualificationRecordDraftBuilder.Build(
            ReferenceRig, AllAttested, DateTimeOffset.UtcNow);

        QualificationMatrixStatusV1 status = QualificationMatrix.Evaluate([draft]);

        QualificationRequirementStatusV1 signedSystems = Assert.Single(
            status.Requirements, requirement => requirement.Requirement == "Independent signed physical systems");
        Assert.Equal(0, signedSystems.Observed);
        Assert.False(status.CanReleaseV1);
        Assert.Empty(status.BlockingDefects); // an honest draft is not a defect, just not evidence
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void BuildRefusesWhenAnyWitnessedAttestationIsMissing(bool noBsod, bool noStuckFan, bool noUnauthorised, bool rollback)
    {
        QualificationAttestations partial = new(noBsod, noStuckFan, noUnauthorised, rollback);

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => QualificationRecordDraftBuilder.Build(ReferenceRig, partial, DateTimeOffset.UtcNow));
        Assert.Contains("fabricate", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRefusesUnclassifiableHardwareInsteadOfGuessing()
    {
        Assert.Throws<InvalidOperationException>(() => QualificationRecordDraftBuilder.Build(
            ReferenceRig with { CpuName = "AMD Ryzen 5 3600" }, AllAttested, DateTimeOffset.UtcNow)); // Zen 2: not in the matrix
        Assert.Throws<InvalidOperationException>(() => QualificationRecordDraftBuilder.Build(
            ReferenceRig with { GpuName = "NVIDIA GeForce GTX 1080 Ti" }, AllAttested, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => QualificationRecordDraftBuilder.Build(
            ReferenceRig with { BoardVendor = " " }, AllAttested, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", ProcessorQualificationFamily.RyzenZen3)]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", ProcessorQualificationFamily.RyzenZen4)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", ProcessorQualificationFamily.RyzenZen5)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", ProcessorQualificationFamily.Intel12th)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", ProcessorQualificationFamily.Intel13th14th)]
    [InlineData("Intel(R) Core(TM) i9-14900KS", ProcessorQualificationFamily.Intel13th14th)]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K", ProcessorQualificationFamily.IntelCoreUltra200)]
    public void ProcessorClassificationCoversEveryMatrixFamily(string name, ProcessorQualificationFamily expected) =>
        Assert.Equal(expected, QualificationRecordDraftBuilder.TryClassifyProcessor(name));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3090", GraphicsQualificationFamily.Rtx30)]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", GraphicsQualificationFamily.Rtx40)]
    [InlineData("NVIDIA GeForce RTX 5080", GraphicsQualificationFamily.Rtx50)]
    [InlineData("AMD Radeon RX 6800 XT", GraphicsQualificationFamily.Rx6000)]
    [InlineData("AMD Radeon RX 7900 XTX", GraphicsQualificationFamily.Rx7000)]
    [InlineData("AMD Radeon RX 9070 XT", GraphicsQualificationFamily.Rx9000)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GraphicsQualificationFamily.ArcA)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", GraphicsQualificationFamily.ArcB)]
    public void GraphicsClassificationCoversEveryMatrixFamily(string name, GraphicsQualificationFamily expected) =>
        Assert.Equal(expected, QualificationRecordDraftBuilder.TryClassifyGraphics(name));

    [Fact]
    public void SystemIdIsStablePrivacyPreservingAndDistinctPerRig()
    {
        string first = QualificationRecordDraftBuilder.CreateSystemId(ReferenceRig);
        string again = QualificationRecordDraftBuilder.CreateSystemId(ReferenceRig);
        string other = QualificationRecordDraftBuilder.CreateSystemId(
            ReferenceRig with { GpuName = "AMD Radeon RX 7900 XTX" });

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Matches("^system-[0-9a-f]{16}$", first);
        Assert.DoesNotContain(Environment.MachineName.ToUpperInvariant(), first.ToUpperInvariant(), StringComparison.Ordinal);
    }
}
