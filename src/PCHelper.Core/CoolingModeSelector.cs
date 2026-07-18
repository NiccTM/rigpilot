using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Picks the automatic cooling curve (Silent / Balanced / Cooling) from live
/// temperatures, with hysteresis so the mode does not flap on a boundary. Pure
/// and deterministic; the caller supplies the previous choice for stickiness.
/// The selection only chooses between existing conservative curves — every
/// curve keeps the 50% floor and full-maximum emergency headroom regardless.
/// </summary>
public static class CoolingModeSelector
{
    /// <summary>At or above the hottest-component threshold, run the Cooling curve.</summary>
    public const double HotCelsius = 75;

    /// <summary>At or below on every component, the Silent curve is enough.</summary>
    public const double CoolCelsius = 55;

    /// <summary>Degrees a boundary moves in favour of the current mode before switching away.</summary>
    public const double HysteresisCelsius = 4;

    public static CoolingCurveMode Choose(double? cpuCelsius, double? gpuCelsius, CoolingCurveMode? previous = null)
    {
        if (cpuCelsius is null && gpuCelsius is null)
        {
            return CoolingCurveMode.Balanced; // no telemetry — take the middle road
        }

        double hottest = Math.Max(cpuCelsius ?? double.MinValue, gpuCelsius ?? double.MinValue);
        double hot = previous == CoolingCurveMode.Cooling ? HotCelsius - HysteresisCelsius : HotCelsius;
        double cool = previous == CoolingCurveMode.Silent ? CoolCelsius + HysteresisCelsius : CoolCelsius;
        return hottest >= hot
            ? CoolingCurveMode.Cooling
            : hottest <= cool
                ? CoolingCurveMode.Silent
                : CoolingCurveMode.Balanced;
    }

    /// <summary>One sentence explaining a choice, for the user-facing notice.</summary>
    public static string Describe(CoolingCurveMode mode, double? cpuCelsius, double? gpuCelsius)
    {
        string temps = (cpuCelsius, gpuCelsius) switch
        {
            (double cpu, double gpu) => $"CPU {cpu:0}°C, GPU {gpu:0}°C",
            (double cpu, null) => $"CPU {cpu:0}°C",
            (null, double gpu) => $"GPU {gpu:0}°C",
            _ => "no temperature telemetry",
        };
        return mode switch
        {
            CoolingCurveMode.Cooling => $"{temps} — running the Cooling curve until things settle.",
            CoolingCurveMode.Silent => $"{temps} — everything is cool, so the Silent curve keeps noise down.",
            _ => $"{temps} — the Balanced curve fits.",
        };
    }
}
