using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Evaluates the published 1.0 hardware-in-loop release gate. This class is
/// intentionally evidence-only: it never infers support from a device family,
/// a single report, or a UI capability.
/// </summary>
public static class QualificationMatrix
{
    private const int MinimumPhysicalSystems = 18;
    private const int MinimumIndependentControllerReports = 2;

    private static readonly ProcessorQualificationFamily[] RequiredProcessors =
    [
        ProcessorQualificationFamily.RyzenZen3,
        ProcessorQualificationFamily.RyzenZen4,
        ProcessorQualificationFamily.RyzenZen5,
        ProcessorQualificationFamily.Intel12th,
        ProcessorQualificationFamily.Intel13th14th,
        ProcessorQualificationFamily.IntelCoreUltra200
    ];

    private static readonly GraphicsQualificationFamily[] RequiredGraphics =
    [
        GraphicsQualificationFamily.Rtx30,
        GraphicsQualificationFamily.Rtx40,
        GraphicsQualificationFamily.Rtx50,
        GraphicsQualificationFamily.Rx6000,
        GraphicsQualificationFamily.Rx7000,
        GraphicsQualificationFamily.Rx9000,
        GraphicsQualificationFamily.ArcA,
        GraphicsQualificationFamily.ArcB
    ];

    private static readonly string[] RequiredBoardVendors = ["ASUS", "MSI", "Gigabyte", "ASRock"];

    public static QualificationMatrixStatusV1 Evaluate(IEnumerable<HardwareQualificationRecordV1> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        HardwareQualificationRecordV1[] records = source.ToArray();
        List<QualificationRequirementStatusV1> requirements = [];
        List<string> defects = [];

        foreach (HardwareQualificationRecordV1 record in records)
        {
            ValidateRecord(record, defects);
            if (!record.NoBsodOrUnexpectedReboot)
            {
                defects.Add($"{record.ReportId}: BSOD or unexpected reboot was reported.");
            }
            if (!record.NoStuckFan)
            {
                defects.Add($"{record.ReportId}: a stuck-fan condition was reported.");
            }
            if (!record.NoUnauthorisedWrite)
            {
                defects.Add($"{record.ReportId}: an unauthorised hardware write was reported.");
            }
            if (!record.RollbackPassed)
            {
                defects.Add($"{record.ReportId}: rollback or default recovery did not pass.");
            }
        }

        HardwareQualificationRecordV1[] signedRecords = records
            .Where(record => record.SignedProductionBuild)
            .ToArray();
        int signedSystems = signedRecords
            .Select(record => record.SystemId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Count();
        requirements.Add(Status(
            "Independent signed physical systems",
            signedSystems,
            MinimumPhysicalSystems,
            "Version 1.0 requires at least 18 distinct physical systems exercised with a signed production build."));

        AddFamilyRequirements(
            requirements,
            signedRecords,
            RequiredProcessors,
            record => record.ProcessorFamily,
            "CPU family");
        AddFamilyRequirements(
            requirements,
            signedRecords,
            RequiredGraphics,
            record => record.GraphicsFamily,
            "GPU family");

        foreach (string vendor in RequiredBoardVendors)
        {
            foreach (PlatformQualificationFamily platform in Enum.GetValues<PlatformQualificationFamily>())
            {
                int observed = signedRecords
                    .Where(record => record.PlatformFamily == platform
                        && string.Equals(record.MotherboardVendor, vendor, StringComparison.OrdinalIgnoreCase))
                    .Select(record => record.SystemId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                requirements.Add(Status(
                    $"{vendor} motherboard on {platform} platform",
                    observed,
                    1,
                    "Each listed motherboard vendor needs exact-device evidence on both AMD and Intel platforms."));
            }
        }

        foreach (IGrouping<string, (HardwareQualificationRecordV1 Record, ControllerQualificationEvidenceV1 Evidence)> family in records
            .SelectMany(record => record.ControllerEvidence.Select(evidence => (record, evidence)))
            .Where(item => item.evidence.ClaimedWriteCapability)
            .GroupBy(item => item.evidence.ControllerFamily, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            int observed = family
                .Where(item => item.Record.SignedProductionBuild
                    && item.Record.NoBsodOrUnexpectedReboot
                    && item.Record.NoStuckFan
                    && item.Record.NoUnauthorisedWrite
                    && item.Record.RollbackPassed
                    && item.Evidence.ApplyReadBackResetPassed)
                .Select(item => item.Record.SystemId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            requirements.Add(Status(
                $"Write controller family: {family.Key}",
                observed,
                MinimumIndependentControllerReports,
                "Every claimed write-capable controller family needs two successful reports from different physical systems."));
        }

        bool releaseReady = defects.Count == 0 && requirements.All(requirement => requirement.Passed);
        return new QualificationMatrixStatusV1(
            QualificationMatrixStatusV1.CurrentSchemaVersion,
            records
                .Select(record => record.SystemId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Count(),
            releaseReady,
            requirements,
            defects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static QualificationRequirementStatusV1 Status(
        string requirement,
        int observed,
        int required,
        string message) => new(requirement, observed >= required, observed, required, message);

    private static void AddFamilyRequirements<T>(
        List<QualificationRequirementStatusV1> requirements,
        IEnumerable<HardwareQualificationRecordV1> records,
        IEnumerable<T> families,
        Func<HardwareQualificationRecordV1, T> selector,
        string label)
        where T : struct, Enum
    {
        foreach (T family in families)
        {
            int observed = records
                .Where(record => EqualityComparer<T>.Default.Equals(selector(record), family))
                .Select(record => record.SystemId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            requirements.Add(Status(
                $"{label}: {family}",
                observed,
                1,
                "A family is covered only by an exact, signed physical-system record."));
        }
    }

    private static void ValidateRecord(HardwareQualificationRecordV1 record, List<string> defects)
    {
        if (record.SchemaVersion != HardwareQualificationRecordV1.CurrentSchemaVersion)
        {
            defects.Add($"{record.ReportId}: unsupported qualification record schema.");
        }
        if (string.IsNullOrWhiteSpace(record.ReportId))
        {
            defects.Add("A qualification record has no report ID.");
        }
        if (string.IsNullOrWhiteSpace(record.SystemId))
        {
            defects.Add($"{record.ReportId}: a privacy-preserving system ID is required.");
        }
        if (string.IsNullOrWhiteSpace(record.MotherboardVendor))
        {
            defects.Add($"{record.ReportId}: motherboard vendor is required.");
        }
        foreach (ControllerQualificationEvidenceV1 evidence in record.ControllerEvidence)
        {
            if (string.IsNullOrWhiteSpace(evidence.ControllerFamily)
                || string.IsNullOrWhiteSpace(evidence.ExactDeviceId)
                || string.IsNullOrWhiteSpace(evidence.FirmwareVersion)
                || string.IsNullOrWhiteSpace(evidence.DriverVersion))
            {
                defects.Add($"{record.ReportId}: controller evidence needs family, exact device ID, firmware, and driver version.");
            }
        }
    }
}
