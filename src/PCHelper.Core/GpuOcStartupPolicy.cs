using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// The gate for saving a GPU overclock for automatic reapplication at startup. This is the
/// riskiest opt-in in the suite, so the rules that must all hold before anything is persisted
/// are kept here, pure and testable: an explicit restart-risk acceptance, an exact-device
/// confirmation, and outputs that are only ever the documented GPU OC controls (clock offsets
/// and the power limit) for the confirmed device. Voltage is not a GPU OC control and can
/// never appear here.
/// </summary>
public static class GpuOcStartupPolicy
{
    private static readonly string[] AllowedCapabilityPrefixes =
    [
        "gpuclock.core:",
        "gpuclock.memory:",
        "gpupower.limit:",
    ];

    /// <summary>
    /// True when a control may be saved for startup reapplication: the documented GPU OC
    /// controls only, which is the clock offsets and the power limit.
    ///
    /// <para>The UI needs the same answer as the service — a dashboard that offered to save a
    /// fan duty or a voltage would build a request the service is bound to reject. Exposing the
    /// decision here rather than restating the prefixes at the call site keeps the two ends
    /// agreeing by construction instead of by coincidence.</para>
    /// </summary>
    public static bool IsPersistableCapability(string? capabilityId) =>
        !string.IsNullOrWhiteSpace(capabilityId)
        && AllowedCapabilityPrefixes.Any(prefix => capabilityId.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Validates an enable request. Returns null when persistence may proceed, or a specific
    /// reason to refuse. Disable requests are always allowed and do not come through here.
    /// </summary>
    public static string? ValidateEnable(
        string deviceId,
        IReadOnlyList<GpuOcStartupOutputV1> outputs,
        IReadOnlyList<string> confirmedDeviceIds,
        bool confirmRestartRisk)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return "A device must be specified to save its overclock.";
        }

        if (!confirmRestartRisk)
        {
            return "Saving an overclock for startup requires accepting the restart risk: a bad overclock can prevent a clean boot until it is reverted.";
        }

        if (confirmedDeviceIds is null || !confirmedDeviceIds.Contains(deviceId, StringComparer.Ordinal))
        {
            return $"Saving the overclock for '{deviceId}' requires an exact-device confirmation of that device.";
        }

        if (outputs is null || outputs.Count == 0)
        {
            return "There is no applied overclock to save.";
        }

        foreach (GpuOcStartupOutputV1 output in outputs)
        {
            if (!IsPersistableCapability(output.CapabilityId))
            {
                return $"Only GPU clock offsets and the power limit can be saved; '{output.CapabilityId}' is not one of them.";
            }

            if (!double.IsFinite(output.Value))
            {
                return $"The saved value for '{output.CapabilityId}' is not a finite number.";
            }
        }

        return null;
    }
}
