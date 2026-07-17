using System.Runtime.InteropServices;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only AMD Device Library eXtra (ADLX) feasibility detector. ADLX ships
/// as <c>amdadlx64.dll</c> with the AMD Radeon driver and is the documented
/// vendor path for Radeon telemetry and tuning. This adapter only detects
/// whether that library and its <c>ADLXInitialize</c> entry point are present;
/// it never initialises ADLX and exposes no write. A qualified telemetry or
/// (separately) tuning adapter would be built on this evidence on a real Radeon
/// system — shipping untested struct marshalling into the LocalSystem service
/// is worse than not shipping it. See docs/qualification/cpu-tuning-and-intel-arc.md
/// for the identical IGCL reasoning.
/// </summary>
public sealed class AmdGraphicsControlAdapter : IHardwareAdapter
{
    private const string AdapterId = "amd.adlx";
    private const string ControlLibraryName = "amdadlx64.dll";
    private const string InitEntryPoint = "ADLXInitialize";

    public AdapterManifest Manifest { get; } = new(
        AdapterId,
        "AMD ADLX feasibility",
        "0.5.0-alpha",
        "AMD Radeon driver (amdadlx64.dll)",
        "AMD Radeon graphics driver",
        AdapterExecutionContext.SystemService,
        ["AMD Radeon RX graphics with the AMD driver installed"],
        ["AmdGraphicsControlFeasibility", "GpuWritesSafetyLocked"]);

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        (bool libraryPresent, bool initPresent) = DetectControlLibrary();
        List<CapabilityDescriptor> capabilities = [];

        // Only surface a card when the AMD control library is actually present, so
        // this stays quiet on non-AMD systems. It is read-only inventory evidence.
        if (libraryPresent)
        {
            string reason = initPresent
                ? $"AMD ADLX ({ControlLibraryName}) and its {InitEntryPoint} entry point are present, so a Radeon telemetry/tuning transport is feasible on this driver. It remains ReadOnly: no telemetry read-back, tuning bounds, apply, default reset, or physical qualification is implemented yet."
                : $"AMD ADLX ({ControlLibraryName}) was found but the {InitEntryPoint} entry point is missing; the installed driver does not expose a usable control interface.";
            capabilities.Add(new CapabilityDescriptor(
                $"adlx.feasibility:{AdapterId}",
                AdapterId,
                AdapterId,
                "AMD graphics control feasibility",
                CapabilityAccessState.ReadOnly,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Boolean,
                null,
                null,
                RiskLevel.Critical,
                EvidenceLevel.Detected,
                null,
                reason,
                CanResetToDefault: false,
                Domain: ControlDomain.Gpu));
        }

        return Task.FromResult(new AdapterProbeResult(Manifest, [], capabilities, []));
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SensorSample>>([]);

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("AMD ADLX support is read-only feasibility detection.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("AMD ADLX support is read-only feasibility detection.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("AMD ADLX support is read-only feasibility detection.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("AMD ADLX support exposes no resettable control.");

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        (bool libraryPresent, _) = DetectControlLibrary();
        return Task.FromResult(new AdapterHealth(
            AdapterId,
            true,
            DateTimeOffset.UtcNow,
            libraryPresent ? "AMD ADLX detected." : "AMD ADLX not present; nothing to control.",
            []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Detects the presence of amdadlx64.dll and its ADLXInitialize export
    /// without initialising ADLX. Presence-only: no ADLX system handle is
    /// created and no control call is made, so this cannot read or write AMD
    /// hardware.
    /// </summary>
    private static (bool LibraryPresent, bool InitPresent) DetectControlLibrary()
    {
        if (!NativeLibrary.TryLoad(ControlLibraryName, out nint library))
        {
            return (false, false);
        }

        try
        {
            bool initPresent = NativeLibrary.TryGetExport(library, InitEntryPoint, out _);
            return (true, initPresent);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
