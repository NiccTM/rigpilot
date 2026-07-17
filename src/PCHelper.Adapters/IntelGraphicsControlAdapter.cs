using System.Runtime.InteropServices;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only Intel Graphics Control Library (IGCL) feasibility detector. IGCL
/// ships as <c>ControlLib.dll</c> with the Intel graphics driver and is the
/// vendor path for Intel Arc telemetry and tuning. This adapter only detects
/// whether that library and its <c>ctlInit</c> entry point are present; it never
/// calls into IGCL's tuning API and exposes no write. A qualified read/telemetry
/// or (separately) tuning adapter would be built on this evidence later. See
/// docs/qualification/cpu-tuning-and-intel-arc.md.
/// </summary>
public sealed class IntelGraphicsControlAdapter : IHardwareAdapter
{
    private const string AdapterId = "intel.igcl";
    private const string ControlLibraryName = "ControlLib.dll";
    private const string InitEntryPoint = "ctlInit";

    public AdapterManifest Manifest { get; } = new(
        AdapterId,
        "Intel Graphics Control Library feasibility",
        "0.5.0-alpha",
        "Intel graphics driver (ControlLib.dll)",
        "Intel graphics driver",
        AdapterExecutionContext.SystemService,
        ["Intel Arc / Xe graphics with the Intel driver installed"],
        ["IntelGraphicsControlFeasibility", "GpuWritesSafetyLocked"]);

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        (bool libraryPresent, bool initPresent) = DetectControlLibrary();
        List<CapabilityDescriptor> capabilities = [];

        // Only surface a card when the Intel control library is actually present, so
        // this stays quiet on non-Intel systems. It is read-only inventory evidence.
        if (libraryPresent)
        {
            string reason = initPresent
                ? $"Intel IGCL ({ControlLibraryName}) and its {InitEntryPoint} entry point are present, so an Intel Arc telemetry/tuning transport is feasible on this driver. It remains ReadOnly: no telemetry read-back, tuning bounds, apply, default reset, or physical qualification is implemented yet."
                : $"Intel IGCL ({ControlLibraryName}) was found but the {InitEntryPoint} entry point is missing; the installed driver does not expose a usable control interface.";
            capabilities.Add(new CapabilityDescriptor(
                $"igcl.feasibility:{AdapterId}",
                AdapterId,
                AdapterId,
                "Intel graphics control feasibility",
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
        throw new NotSupportedException("Intel IGCL support is read-only feasibility detection.");

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Intel IGCL support is read-only feasibility detection.");

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Intel IGCL support is read-only feasibility detection.");

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Intel IGCL support exposes no resettable control.");

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        (bool libraryPresent, _) = DetectControlLibrary();
        return Task.FromResult(new AdapterHealth(
            AdapterId,
            true,
            DateTimeOffset.UtcNow,
            libraryPresent ? "Intel IGCL detected." : "Intel IGCL not present; nothing to control.",
            []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Detects the presence of ControlLib.dll and its ctlInit export without
    /// initialising IGCL. Presence-only: no IGCL handle is created and no control
    /// call is made, so this cannot read or write Intel hardware.
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
