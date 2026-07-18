namespace PCHelper.App;

public enum RgbApplyState
{
    AppliedUnverified,
    AppliedVerified,
    Skipped,
    Blocked,
    Failed,
    Unknown
}

/// <summary>
/// Truthful result for one lighting endpoint or contained native route. Most
/// RGB protocols have no colour read-back, so a successful command is labelled
/// AppliedUnverified unless the endpoint supplies independent read-back.
/// </summary>
public sealed record RgbApplyOutcome(
    string RouteId,
    string Name,
    string Family,
    RgbApplyState State,
    string Message)
{
    public bool Applied => State is RgbApplyState.AppliedUnverified or RgbApplyState.AppliedVerified;

    public string StateLabel => State switch
    {
        RgbApplyState.AppliedVerified => "Applied · verified",
        RgbApplyState.AppliedUnverified => "Applied · confirm visually",
        RgbApplyState.Skipped => "Skipped",
        RgbApplyState.Blocked => "Blocked",
        RgbApplyState.Failed => "Failed before write",
        RgbApplyState.Unknown => "Unknown · inspect before retry",
        _ => State.ToString()
    };

    public string StateTone => State switch
    {
        RgbApplyState.AppliedVerified => "Safe",
        RgbApplyState.AppliedUnverified or RgbApplyState.Skipped => "Warning",
        RgbApplyState.Blocked or RgbApplyState.Failed or RgbApplyState.Unknown => "Critical",
        _ => "Neutral"
    };
}

internal static class RgbEndpointFamily
{
    public static string Resolve(string? name, string? manufacturer = null)
    {
        string identity = $"{name} {manufacturer}".ToLowerInvariant();
        if (ContainsAny(identity, "nzxt", "kraken"))
        {
            return "nzxt-kraken";
        }

        if (ContainsAny(identity, "asus", "aura", "rog"))
        {
            return "asus-aura";
        }

        if (ContainsAny(identity, "g.skill", "gskill", "trident", "dimm", "dram", "ene"))
        {
            return "dimm-rgb";
        }

        if (ContainsAny(identity, "razer", "lian li", "lianli", "o11"))
        {
            return "razer-lianli";
        }

        if (ContainsAny(identity, "zotac", "spectra"))
        {
            return "zotac-gpu";
        }

        if (ContainsAny(identity, "corsair", "icue"))
        {
            return "corsair";
        }

        return string.IsNullOrWhiteSpace(identity) ? "unknown" : $"endpoint:{identity.Trim()}";
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
