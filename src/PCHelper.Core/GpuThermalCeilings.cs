namespace PCHelper.Core;

/// <summary>Which physical sensor a GPU temperature reading comes from.</summary>
public enum GpuTemperatureClass
{
    /// <summary>Edge / core die temperature — what an operator means by "GPU temperature".</summary>
    Edge,

    /// <summary>Hottest point on the die, structurally warmer than edge.</summary>
    HotSpot,

    /// <summary>GDDR6X memory junction, which runs far hotter than the core by design.</summary>
    MemoryJunction
}

/// <summary>
/// Per-sensor thermal ceilings for GPU screening.
/// </summary>
/// <remarks>
/// <para>
/// The screening monitor used to compare the MAXIMUM of every bound temperature
/// sensor against the single operator-supplied ceiling, and the sensor binding
/// deliberately includes hot spot and memory junction alongside the edge sensor.
/// Those run hotter than the core by design: on the reference 3090, GDDR6X memory
/// junction sits in the 80-100 °C band under sustained load while the core is in
/// the 70s. Judging them against an 83 °C core ceiling makes every sample fail as
/// soon as the workload genuinely loads the card — which is exactly what happened
/// once the workload host was fixed to saturate the GPU. The weak workload had
/// been masking this.
/// </para>
/// <para>
/// The edge ceiling stays exactly as the operator set it — this never relaxes the
/// limit an operator chose for the sensor they meant. The additional ceilings are
/// deliberately set BELOW the vendor throttle points (Ampere hot spot ~105 °C,
/// GDDR6X ~110 °C) so screening aborts before the hardware would protect itself,
/// and they are floored at the edge ceiling so raising the operator's limit above
/// them can never be silently ignored.
/// </para>
/// </remarks>
public static class GpuThermalCeilings
{
    /// <summary>Below the ~105 °C Ampere hot-spot throttle point.</summary>
    public const double HotSpotCeilingCelsius = 100;

    /// <summary>
    /// Below the ~110 °C GDDR6X throttle point, with margin — but ABOVE the band a healthy
    /// card occupies at stock, which 95 °C was not.
    /// </summary>
    /// <remarks>
    /// This was 95 °C, and 2026-07-27 showed why that is not a safety limit at all. On the
    /// reference RTX 3090 the memory stage failed its very first rung — the STOCK one, no
    /// offset applied — at 96.0 °C observed against 95.0 °C allowed. A ceiling that rejects
    /// the configuration the card ships in cannot protect anything: it makes the memory stage
    /// unreachable on the exact hardware class the feature exists for, and it trains an
    /// operator to read thermal rejections as noise.
    ///
    /// The contradiction was already written down. This file's own remarks note that GDDR6X
    /// junction "sits in the 80-100 °C band under sustained load", and the ceiling was set at
    /// 95 — inside that band, below its upper half. Double-sided 3090 memory routinely runs
    /// 95-105 °C under load and is specified to throttle near 110 °C.
    ///
    /// 102 °C keeps a genuine 8 °C margin to the throttle point, which is the number that
    /// matters for screening: once memory throttles, the throughput measurement the search
    /// depends on becomes meaningless, so the ceiling's job is to abort before that — not to
    /// enforce a temperature the hardware never agreed to.
    /// </remarks>
    public const double MemoryJunctionCeilingCelsius = 102;

    public static GpuTemperatureClass Classify(string sensorName)
    {
        // Order matters: the memory-junction and hot-spot sensors are themselves
        // named "GPU ...", so the specific classes must be tested before edge.
        if (sensorName.Contains("Memory Junction", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("Memory Temp", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
        {
            return GpuTemperatureClass.MemoryJunction;
        }

        return sensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)
            ? GpuTemperatureClass.HotSpot
            : GpuTemperatureClass.Edge;
    }

    /// <summary>
    /// Ceiling for a sensor class. Never below the operator's edge ceiling, so an
    /// operator who deliberately raises the limit is not overridden downward.
    /// </summary>
    public static double CeilingFor(GpuTemperatureClass temperatureClass, double edgeCeilingCelsius) =>
        temperatureClass switch
        {
            GpuTemperatureClass.HotSpot => Math.Max(edgeCeilingCelsius, HotSpotCeilingCelsius),
            GpuTemperatureClass.MemoryJunction => Math.Max(edgeCeilingCelsius, MemoryJunctionCeilingCelsius),
            _ => edgeCeilingCelsius
        };

    /// <summary>Ceiling that applies to a named sensor.</summary>
    public static double CeilingForSensor(string sensorName, double edgeCeilingCelsius) =>
        CeilingFor(Classify(sensorName), edgeCeilingCelsius);
}
