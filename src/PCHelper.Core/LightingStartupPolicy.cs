using System.Globalization;

namespace PCHelper.Core;

/// <summary>
/// The gate for saving a static lighting colour to be re-driven at service start.
///
/// <para>Lighting is the one write family where an unattended boot reapply is defensible on
/// its own terms. The manual-only boot guard exists because an unattended hardware write can
/// leave a machine in a state the operator is not present to correct — a thermal envelope, a
/// clock that will not POST. These routes write lighting registers only: no EEPROM, no
/// firmware or profile command class, no thermal or electrical envelope. The worst outcome of
/// a failed reapply is that a device stays at its firmware default colour, which is exactly
/// where it would have been had nothing run at all. The guard is therefore satisfied on the
/// merits rather than bypassed.</para>
///
/// <para>What is still enforced: a real six-digit colour, and routes drawn only from the
/// known native writers. Everything else about those writers — the Experimental flag, the
/// exact-device confirmation, the contained Adapter Host child — is unchanged, because the
/// startup path issues the very same requests the operator issued interactively.</para>
/// </summary>
public static class LightingStartupPolicy
{
    /// <summary>
    /// Whether a saved colour may be re-driven at this start, or the reason it may not.
    ///
    /// <para>Architecture invariant 6: "Experimental profiles are not automatically restored
    /// after an unclean shutdown." Every native RGB route is Experimental and exact-device
    /// confirmed, and the DIMM route reaches its controllers over the system SMBus through a
    /// signed PawnIO transport — a shared bus, not merely a private lighting register. After
    /// an unclean shutdown the machine's state is by definition unverified, and that is
    /// exactly the condition the invariant exists to refuse writing into.</para>
    ///
    /// <para>This is a narrower rule than the one the GPU overclock path uses. That path
    /// reapplies through a boot sentinel which reverts an overclock a boot did not survive.
    /// Lighting has no such journal because it has no failure to recover from, so the
    /// equivalent protection is simply to not write at all until a clean start proves the
    /// machine came up normally. The saved profile is kept either way: one bad shutdown must
    /// not silently discard what the operator chose.</para>
    /// </summary>
    public static string? BlockRestoreReason(bool previousShutdownWasClean) =>
        previousShutdownWasClean
            ? null
            : "the previous shutdown was unclean, so Experimental lighting is not restored automatically (architecture invariant 6). "
                + "The saved colour is kept and will be restored after the next clean start, or immediately if you apply it yourself.";

    /// <summary>The native RGB routes that can be re-driven without a signed-in user.</summary>
    public static readonly IReadOnlyList<string> KnownRouteIds =
    [
        "native:kraken",
        "native:aura",
        "native:dimm",
        "native:razer",
    ];

    public static bool IsKnownRoute(string? routeId) =>
        routeId is not null && KnownRouteIds.Contains(routeId, StringComparer.Ordinal);

    /// <summary>
    /// Returns the colour as six upper-case hex digits, or null when it is not one.
    /// A leading '#' is accepted because that is how the dashboard carries it.
    /// </summary>
    public static string? NormaliseColour(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour))
        {
            return null;
        }

        string trimmed = colour.Trim().TrimStart('#');
        if (trimmed.Length != 6)
        {
            return null;
        }

        foreach (char character in trimmed)
        {
            if (!Uri.IsHexDigit(character))
            {
                return null;
            }
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>
    /// Validates an enable request. Returns null when persistence may proceed, otherwise the
    /// exact reason to refuse. Disable requests are always allowed and do not come through here.
    /// </summary>
    public static string? ValidateEnable(string? colour, IReadOnlyList<string>? routeIds)
    {
        if (NormaliseColour(colour) is null)
        {
            return $"'{colour}' is not a six-digit RGB colour, so there is nothing to restore at startup.";
        }

        if (routeIds is null || routeIds.Count == 0)
        {
            return "No lighting route was named, so there is nothing to restore at startup.";
        }

        foreach (string routeId in routeIds)
        {
            if (!IsKnownRoute(routeId))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{routeId}' is not a native lighting route that can be re-driven at startup.");
            }
        }

        return null;
    }
}
