using System.Reflection;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only Ryzen SMU feasibility pass — step 1 of the PBO qualification gate
/// (docs/qualification/cpu-tuning-and-intel-arc.md). It loads the RyzenSMU
/// PawnIO module that LibreHardwareMonitorLib 0.9.6 already embeds and ships
/// (extracted from the referenced assembly at runtime, so RigPilot adds no new
/// module distribution), then calls only read-class module functions:
/// <c>ioctl_get_code_name</c>, <c>ioctl_get_smu_version</c>,
/// <c>ioctl_resolve_pm_table</c>, <c>ioctl_update_pm_table</c> (the same
/// PM-table refresh LibreHardwareMonitor issues every poll), and
/// <c>ioctl_read_pm_table</c>. No tuning, limit-set, or register-write module
/// function is referenced anywhere in this type. PPT/TDC/THM/EDC limit/value
/// pairs are decoded only for PM-table versions on the audited Zen 2/3 layout
/// allowlist (cross-checked against ryzen_monitor's pm_tables mapping);
/// anything else reports raw evidence without invented numbers.
/// </summary>
public static class RyzenSmuFeasibilityReader
{
    private const string ModuleResourceName = "LibreHardwareMonitor.Resources.PawnIo.RyzenSMU.bin";
    private const int MaximumPmTableQwords = 512;
    private const int UpdateRetryDelayMilliseconds = 150;

    /// <summary>
    /// PM-table versions whose first floats are PPT_LIMIT, PPT_VALUE, TDC_LIMIT,
    /// TDC_VALUE, THM_LIMIT, THM_VALUE, (FIT pair,) EDC_LIMIT, EDC_VALUE —
    /// Matisse (Zen 2) and Vermeer (Zen 3, incl. the reference 5800X).
    /// </summary>
    internal static readonly IReadOnlySet<uint> AuditedPmTableVersions = new HashSet<uint>
    {
        0x240803, 0x240903,             // Matisse
        0x380804, 0x380805, 0x380904, 0x380905, // Vermeer
    };

    public static RyzenSmuFeasibilityV1 Read()
    {
        PawnIoRuntimeStatus runtime = PawnIoRuntimeProbe.Detect();
        if (!runtime.Available || runtime.LibraryPath is null)
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.PawnIoUnavailable,
                $"Ryzen SMU feasibility needs the signed PawnIO runtime: {runtime.Describe()}.");
        }

        byte[] module;
        try
        {
            module = LoadEmbeddedRyzenSmuModule();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.Failed,
                $"The embedded RyzenSMU module could not be extracted: {exception.GetType().Name}.");
        }

        if (!PawnIoModuleSession.TryOpen(runtime.LibraryPath, module, out PawnIoModuleSession session, out string openMessage))
        {
            return RyzenSmuFeasibilityV1.Unavailable(RyzenSmuFeasibilityOutcome.PawnIoUnavailable, openMessage);
        }

        using (session)
        {
            return ReadWithSession(session);
        }
    }

    /// <summary>Session-driven core, separated so tests can supply a fake module session.</summary>
    public static RyzenSmuFeasibilityV1 ReadWithSession(IPawnIoModuleSession session)
    {
        try
        {
            ulong[] codeName = session.Execute("ioctl_get_code_name", [], 1);
            ulong[] smuVersion = session.Execute("ioctl_get_smu_version", [], 1);
            ulong[] resolved = session.Execute("ioctl_resolve_pm_table", [], 2);
            if (resolved.Length < 1)
            {
                return RyzenSmuFeasibilityV1.Unavailable(
                    RyzenSmuFeasibilityOutcome.Failed,
                    "The RyzenSMU module resolved no PM table on this processor.");
            }
            // The service's own LibreHardwareMonitor polling refreshes this same
            // PM table through PawnIO, so a concurrent mailbox command can fail
            // transiently (observed live: 0x8007054F ERROR_INTERNAL_ERROR with an
            // immediately successful retry). One bounded retry after a short
            // pause; a second failure is reported, never retried again.
            try
            {
                session.Execute("ioctl_update_pm_table", [], 0);
            }
            catch (PawnIoException)
            {
                Thread.Sleep(UpdateRetryDelayMilliseconds);
                session.Execute("ioctl_update_pm_table", [], 0);
            }
            ulong[] table = session.Execute("ioctl_read_pm_table", [], MaximumPmTableQwords);

            return Parse(
                codeName.Length > 0 ? (long)codeName[0] : 0,
                smuVersion.Length > 0 ? (uint)smuVersion[0] : 0,
                (uint)resolved[0],
                table);
        }
        catch (PawnIoException exception)
        {
            return RyzenSmuFeasibilityV1.Unavailable(RyzenSmuFeasibilityOutcome.Failed, exception.Message);
        }
    }

    /// <summary>Pure decode over already-read values, exposed for deterministic tests.</summary>
    public static RyzenSmuFeasibilityV1 Parse(long codeNameId, uint smuVersion, uint pmTableVersion, ReadOnlySpan<ulong> pmTableQwords)
    {
        string smuFirmware = $"{(smuVersion >> 16) & 0xFF}.{(smuVersion >> 8) & 0xFF}.{smuVersion & 0xFF}";
        string tableVersion = $"0x{pmTableVersion:X6}";
        if (!AuditedPmTableVersions.Contains(pmTableVersion))
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.UnrecognisedPmTable,
                $"SMU answered (firmware {smuFirmware}, PM table {tableVersion}), but this table layout is not on the audited Zen 2/3 allowlist; no limit values are decoded.") with
            {
                CodeNameId = codeNameId,
                SmuFirmwareVersion = smuFirmware,
                PmTableVersion = tableVersion,
            };
        }

        // pm_element(i) is the i-th float32 of the table; each qword carries two.
        static float PmElement(ReadOnlySpan<ulong> qwords, int index)
        {
            ulong qword = qwords[index / 2];
            uint half = index % 2 == 0 ? (uint)qword : (uint)(qword >> 32);
            return BitConverter.UInt32BitsToSingle(half);
        }

        if (pmTableQwords.Length < 5)
        {
            return RyzenSmuFeasibilityV1.Unavailable(
                RyzenSmuFeasibilityOutcome.Failed,
                $"The PM table read returned only {pmTableQwords.Length} qwords; the audited layout needs at least the first ten floats.");
        }

        return new RyzenSmuFeasibilityV1(
            RyzenSmuFeasibilityV1.CurrentSchemaVersion,
            RyzenSmuFeasibilityOutcome.Succeeded,
            codeNameId,
            smuFirmware,
            tableVersion,
            Round(PmElement(pmTableQwords, 0)),
            Round(PmElement(pmTableQwords, 1)),
            Round(PmElement(pmTableQwords, 2)),
            Round(PmElement(pmTableQwords, 3)),
            Round(PmElement(pmTableQwords, 4)),
            Round(PmElement(pmTableQwords, 5)),
            Round(PmElement(pmTableQwords, 8)),
            Round(PmElement(pmTableQwords, 9)),
            $"SMU firmware {smuFirmware}, PM table {tableVersion}: PPT/TDC/THM/EDC limit and actual pairs decoded read-only. This is qualification evidence only; CPU/SMU writes remain Blocked.");
    }

    private static double Round(float value) => Math.Round(value, 2);

    private static byte[] LoadEmbeddedRyzenSmuModule()
    {
        Assembly assembly = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ModuleResourceName)
            ?? throw new InvalidOperationException($"Resource '{ModuleResourceName}' is missing from LibreHardwareMonitorLib.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
