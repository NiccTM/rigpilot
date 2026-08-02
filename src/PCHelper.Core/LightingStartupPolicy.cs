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
