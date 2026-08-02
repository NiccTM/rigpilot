namespace PCHelper.Contracts;

/// <summary>
/// The GPU overclock the card is enforcing right now, read back from the driver rather than
/// remembered by the caller.
///
/// <para>The Performance page's clock sliders start at zero on every dashboard launch because
/// nothing told them otherwise, so a machine running a saved +49/+126 MHz overclock displayed
/// "0 MHz" beside a ticked "reapply at startup" box. The values were applied and verified; the
/// dashboard simply had no way to ask what they were. This is that way.</para>
///
/// <para>Offsets are in megahertz to match what the sliders show. Null means the domain could
/// not be read — an unarmed or absent transport — which is deliberately distinct from zero,
/// since zero is a real offset meaning stock.</para>
/// </summary>
public sealed record GpuOcLiveStateV1(
    bool Available,
    int? CoreOffsetMegaHertz,
    int? MemoryOffsetMegaHertz,
    uint? PowerLimitMilliwatts,
    string Message);
