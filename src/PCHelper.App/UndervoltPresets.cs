namespace PCHelper.App;

/// <summary>
/// The guided GPU "undervolt" workflow, built strictly from documented public
/// APIs: it lowers the NVML power-management limit (and nothing else), which
/// drops voltage and heat at near-identical frame rates in most games. It is
/// deliberately NOT a voltage-frequency curve editor — per-point VF control on
/// NVIDIA requires undocumented NVAPI surfaces that RigPilot's
/// documented-vendor-APIs-only rule excludes. Copy in the UI must keep that
/// distinction honest.
/// </summary>
public static class UndervoltPresets
{
    public const string Quiet = "quiet";
    public const string Efficient = "efficient";
    public const string Stock = "stock";

    private const double QuietFactor = 0.75;
    private const double EfficientFactor = 0.85;

    /// <summary>
    /// Computes the preset's target power limit in watts from the driver's
    /// discovered constraint range. The vendor default anchors the presets;
    /// when the driver did not report one, the range maximum is the reference
    /// (the stock limit is the maximum on cards without a separate default).
    /// The result is always clamped into the driver range. Unknown presets
    /// return null rather than guessing.
    /// </summary>
    public static double? ComputeTargetWatts(double minimumWatts, double maximumWatts, double? vendorDefaultWatts, string preset)
    {
        if (minimumWatts <= 0 || maximumWatts <= minimumWatts)
        {
            return null;
        }

        double reference = vendorDefaultWatts is double defaultWatts && defaultWatts >= minimumWatts && defaultWatts <= maximumWatts
            ? defaultWatts
            : maximumWatts;

        double? target = preset switch
        {
            Quiet => reference * QuietFactor,
            Efficient => reference * EfficientFactor,
            Stock => reference,
            _ => null,
        };

        return target is double watts
            ? Math.Round(Math.Clamp(watts, minimumWatts, maximumWatts))
            : null;
    }

    /// <summary>Human label for the applied preset, for status text.</summary>
    public static string Describe(string preset) => preset switch
    {
        Quiet => "Quiet (−25% power target)",
        Efficient => "Efficient (−15% power target)",
        Stock => "Stock power target",
        _ => preset,
    };
}
