using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Narrows process-level lighting conflicts to the endpoint family they can
/// actually own. A broad lighting suite still blocks every endpoint, while a
/// vendor tool such as NZXT CAM blocks NZXT/Kraken endpoints without disabling
/// unrelated ASUS, Razer, or DIMM routes.
/// </summary>
public static class RgbConflictPolicy
{
    private static readonly Dictionary<string, string[]> ScopedOwnerTokens =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["nzxt-cam"] = ["nzxt", "kraken"],
            ["armoury-crate"] = ["asus", "aura", "rog"],
            ["icue"] = ["corsair"],
            ["l-connect"] = ["lian li", "lianli", "strimer", "uni fan", "o11"],
            ["rgb-fusion"] = ["gigabyte", "aorus"],
            ["mystic-light"] = ["msi", "mystic"]
        };

    private static readonly HashSet<string> BroadOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "signalrgb"
    };

    public static IReadOnlyList<ConflictDescriptor> FindBlockingOwners(
        IEnumerable<ConflictDescriptor>? conflicts,
        string? endpointName,
        string? manufacturer = null,
        string? model = null)
    {
        string identity = string.Join(' ', new[] { endpointName, manufacturer, model }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        return (conflicts ?? [])
            .Where(conflict => conflict.IsRunning
                && !string.Equals(conflict.Id, "openrgb", StringComparison.OrdinalIgnoreCase)
                && conflict.ResourceFamilies.Contains("Lighting", StringComparer.OrdinalIgnoreCase))
            .Where(conflict => Blocks(conflict.Id, identity))
            .GroupBy(conflict => conflict.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(conflict => conflict.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ConflictDescriptor> FindBroadBlockingOwners(
        IEnumerable<ConflictDescriptor>? conflicts) => (conflicts ?? [])
        .Where(conflict => conflict.IsRunning
            && !string.Equals(conflict.Id, "openrgb", StringComparison.OrdinalIgnoreCase)
            && conflict.ResourceFamilies.Contains("Lighting", StringComparer.OrdinalIgnoreCase))
        .Where(conflict => BroadOwners.Contains(conflict.Id)
            || !ScopedOwnerTokens.ContainsKey(conflict.Id))
        .GroupBy(conflict => conflict.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(conflict => conflict.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool Blocks(string ownerId, string identity)
    {
        if (BroadOwners.Contains(ownerId))
        {
            return true;
        }

        if (!ScopedOwnerTokens.TryGetValue(ownerId, out string[]? tokens))
        {
            // Unknown lighting writers remain conservative until their ownership
            // scope is explicitly classified.
            return true;
        }

        return tokens.Any(token => identity.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
