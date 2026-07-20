namespace PCHelper.Contracts;

/// <summary>
/// The outcome of a single RGB write, unified across every device family.
///
/// The serialised per-device results (<see cref="KrakenLightingResultV1"/>,
/// <see cref="AuraLightingResultV1"/>, <see cref="RazerRgbResultV1"/>,
/// <see cref="DimmRgbResultV1"/>) stay on the wire unchanged for handshake
/// compatibility, but three of them are the same record under different names
/// keyed on the Kraken-named <see cref="KrakenLightingOutcome"/> enum, while DIMM
/// diverges with a string outcome. Callers had to special-case each. This enum is
/// the one internal vocabulary they normalise into.
/// </summary>
public enum RgbWriteOutcome
{
    /// <summary>Lighting reports were written. No firmware read-back; confirmation is visual.</summary>
    WriteIssued,

    /// <summary>No supported device of this family is present.</summary>
    DeviceNotFound,

    /// <summary>The device exists but could not be opened (a competing writer may own it).</summary>
    AccessDenied,

    /// <summary>The write failed for another contained reason.</summary>
    Failed,
}

/// <summary>
/// One RGB write result, normalised from any device family's wire result. This is an
/// in-memory working type, never itself serialised; the per-device <c>*ResultV1</c>
/// records remain the wire contract. Consumers (sync-all, per-device apply) read
/// <see cref="WriteIssued"/> and <see cref="Message"/> uniformly instead of each
/// device's bespoke success predicate.
/// </summary>
public sealed record RgbWriteResult(RgbWriteOutcome Outcome, string? ProductName, string Message)
{
    /// <summary>True only when the write was issued to hardware.</summary>
    public bool WriteIssued => Outcome == RgbWriteOutcome.WriteIssued;

    private static RgbWriteOutcome Map(KrakenLightingOutcome outcome) => outcome switch
    {
        KrakenLightingOutcome.WriteIssued => RgbWriteOutcome.WriteIssued,
        KrakenLightingOutcome.DeviceNotFound => RgbWriteOutcome.DeviceNotFound,
        KrakenLightingOutcome.AccessDenied => RgbWriteOutcome.AccessDenied,
        _ => RgbWriteOutcome.Failed,
    };

    internal static RgbWriteResult From(KrakenLightingOutcome outcome, string? productName, string message) =>
        new(Map(outcome), productName, message);
}

/// <summary>
/// Normalises each device family's wire result into the single <see cref="RgbWriteResult"/>.
/// The one place the per-family shapes (enum vs string outcome, present vs absent product
/// name) are reconciled, so no consumer special-cases a family again.
/// </summary>
public static class RgbWriteResultMappings
{
    public static RgbWriteResult ToRgbWriteResult(this KrakenLightingResultV1 result) =>
        RgbWriteResult.From(result.Outcome, result.ProductName, result.Message);

    public static RgbWriteResult ToRgbWriteResult(this AuraLightingResultV1 result) =>
        RgbWriteResult.From(result.Outcome, result.ProductName, result.Message);

    public static RgbWriteResult ToRgbWriteResult(this RazerRgbResultV1 result) =>
        RgbWriteResult.From(result.Outcome, result.ProductName, result.Message);

    public static RgbWriteResult ToRgbWriteResult(this DimmRgbResultV1 result) =>
        new(result.WriteIssued ? RgbWriteOutcome.WriteIssued : RgbWriteOutcome.Failed, null, result.Message);
}
