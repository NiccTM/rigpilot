namespace PCHelper.App;

/// <summary>
/// Decides whether the Performance page is actually showing an overclock worth saving
/// for startup reapplication.
///
/// <para>This exists because the sliders are the only source for the values and they do
/// not read back what is already applied: clock offsets start at zero every time the
/// dashboard launches. Saving straight from a freshly opened page would re-apply a stock
/// setting — quietly undoing a live overclock — and then persist that nothing as though
/// it were one. Requiring a control to differ from its own default is what separates
/// "the user set this" from "the page just opened".</para>
/// </summary>
public static class GpuOcStartupSelection
{
    /// <summary>Tolerance for the comparison; every saved value is rounded to a whole unit.</summary>
    private const double Epsilon = 0.001;

    /// <summary>
    /// True when a control has been moved off its default. When the adapter reported no
    /// default, zero is assumed: that is correct for a clock offset, where zero means
    /// stock, and it is the conservative answer everywhere else because it refuses to
    /// save rather than guessing that an unknown value was deliberate.
    /// </summary>
    public static bool IsDeliberateValue(double value, double? defaultValue) =>
        defaultValue is double known
            ? Math.Abs(value - known) > Epsilon
            : Math.Abs(value) > Epsilon;
}
