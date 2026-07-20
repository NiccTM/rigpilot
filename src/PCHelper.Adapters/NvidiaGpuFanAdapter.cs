using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Bounded, Experimental GPU-fan-duty control built on the proven
/// prepare/apply/verify/rollback/reset shape. All safety logic — floor
/// enforcement, manual-only application, read-back verification, restore on
/// rollback, and reset to the driver automatic curve — lives here and is exercised
/// against an injectable <see cref="IGpuFanCoolerTransport"/>. This adapter never
/// issues an automatic or at-startup write; fan duty is Manual Only.
///
/// It exposes a live write ONLY when constructed with <c>enableWrites: true</c>.
/// The default is a read-only capability card, so wiring the adapter in cannot by
/// itself un-gate a hardware write. See docs/qualification/rtx3090-fan-write-path.md.
/// </summary>
public sealed class NvidiaGpuFanAdapter : IHardwareAdapter, IHardwareStateVerifier
{
    public const string AdapterId = "nvidia.gpufan";
    public const string CapabilityPrefix = "gpufan.duty:";

    private const int VerifyTolerancePercent = 5;

    // A commanded GPU fan does not reach its setpoint instantly — it ramps over a
    // second or two, and the NVAPI cooler read-back reports the live level, not the
    // setpoint. Verifying immediately caught the fan mid-ramp (41% while climbing to
    // a commanded 50%, on the reference rig) and rejected a write that had in fact
    // taken. The manual policy flips at once, so the level is polled toward the
    // target across a bounded settle window before the verdict. A cold start — the
    // fan physically stopped (idled at native automatic 0%) rather than already
    // spinning — needs longer still: a live GPU-fan mode switch commanded 30% from a
    // dead stop and read back 0% after an earlier, denser 2-second budget.
    //
    // A denser poll does not just wait longer, it also asks the driver more often:
    // widening the window by shortening the interval (more reads in the same span)
    // made the *next* call — the safety-critical restore-to-default below — more
    // likely to hit NVAPI_INVALID_USER_PRIVILEGE, reproduced live even with a
    // widened window and a retry. The settle poll and the driver-session recovery
    // are the same resource: every read here is one more NVAPI call in the same
    // burst the reset has to survive. The window is sized for the cold-start case
    // by covering more wall-clock time with a longer interval, not more calls.
    private static readonly TimeSpan FanSettleInterval = TimeSpan.FromMilliseconds(500);
    private const int FanSettleAttempts = 12;

    // Restoring firmware default is the one call that must not fail permanently —
    // every caller (rollback, explicit reset, cooling-graph recovery) escalates an
    // unrecovered failure straight to a full hardware write lock. A short pause
    // before the first attempt lets the driver session settle after the preceding
    // settle-poll burst, rather than immediately adding another call on top of it.
    private static readonly TimeSpan ResetCooldownInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResetRetryInterval = TimeSpan.FromMilliseconds(750);
    private const int ResetRetryAttempts = 6;

    private readonly IGpuFanCoolerTransport _transport;
    private readonly string _deviceId;
    private readonly string _channelId;
    private readonly Func<bool> _isArmed;
    private readonly Func<CancellationToken, Task<bool>> _isConflicted;
    private readonly Func<TimeSpan, CancellationToken, Task> _settleDelay;

    public NvidiaGpuFanAdapter(
        IGpuFanCoolerTransport transport,
        string deviceId,
        string channelId,
        bool enableWrites = false,
        Func<CancellationToken, Task<bool>>? isConflicted = null)
        : this(transport, deviceId, channelId, () => enableWrites, isConflicted)
    {
    }

    public NvidiaGpuFanAdapter(
        IGpuFanCoolerTransport transport,
        string deviceId,
        string channelId,
        Func<bool> isArmed,
        Func<CancellationToken, Task<bool>>? isConflicted = null,
        Func<TimeSpan, CancellationToken, Task>? settleDelay = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        _deviceId = deviceId;
        _channelId = channelId;
        _isArmed = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
        _isConflicted = isConflicted ?? (_ => Task.FromResult(false));
        _settleDelay = settleDelay ?? (static (delay, token) => Task.Delay(delay, token));
    }

    public string CapabilityId => $"{CapabilityPrefix}{_channelId}";

    public AdapterManifest Manifest { get; } = new(
        AdapterId,
        "NVIDIA GPU fan control (Experimental)",
        "0.6.0-beta.1",
        "NVIDIA display driver",
        "NVIDIA display driver with a usable manual-fan transport",
        AdapterExecutionContext.SystemService,
        ["NVIDIA GPU with a qualified manual-fan transport"],
        ["GpuFanDutyExperimental"]);

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        GpuFanBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
        bool conflicted = await _isConflicted(cancellationToken).ConfigureAwait(false);
        List<CapabilityDescriptor> capabilities = [];

        if (bounds is { IsValid: true } valid)
        {
            // A running competing writer converts the control to Blocked (never takeover).
            // Writes are exposed only when the capability is armed via an acknowledged
            // operator action; otherwise the card is ReadOnly.
            bool armed = _isArmed();
            CapabilityAccessState state = conflicted
                ? CapabilityAccessState.Blocked
                : armed
                    ? CapabilityAccessState.Experimental
                    : CapabilityAccessState.ReadOnly;
            string reason = conflicted
                ? "A competing GPU fan writer is active; this control is blocked until it releases the fan."
                : armed
                    ? $"Manual GPU fan duty is clamped to [{valid.FloorPercent}, {valid.CeilingPercent}]%. Manual Only: never applied automatically or at startup, and reset returns the driver automatic curve."
                    : $"Manual GPU fan duty is feasible within [{valid.FloorPercent}, {valid.CeilingPercent}]%, but remains read-only until it is explicitly armed with an Experimental acknowledgement for this device.";

            capabilities.Add(new CapabilityDescriptor(
                CapabilityId,
                AdapterId,
                _deviceId,
                "GPU fan duty",
                state,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(valid.FloorPercent, valid.CeilingPercent, 1),
                "%",
                RiskLevel.Experimental,
                EvidenceLevel.Detected,
                conflicted ? "Competing GPU fan writer" : null,
                reason,
                CanResetToDefault: true,
                Domain: ControlDomain.Cooling));
        }

        return new AdapterProbeResult(Manifest, [], capabilities, []);
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SensorSample>>([]);

    public async Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        EnsureOwnedCapability(action.CapabilityId);
        await EnsureNotConflictedAsync(cancellationToken).ConfigureAwait(false);

        int requested = RequireDuty(action.Value);
        GpuFanBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (requested < bounds.FloorPercent || requested > bounds.CeilingPercent)
        {
            throw new GpuFanSafetyException(
                $"Requested GPU fan duty {requested}% is outside the safe range [{bounds.FloorPercent}, {bounds.CeilingPercent}]%.");
        }

        GpuFanChannelState previous = await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        string rollback = JsonSerializer.Serialize(previous);
        ControlValue? previousValue = previous.CommandedDutyPercent is int prior
            ? ControlValue.FromNumeric(prior)
            : null;
        return new PreparedAction(
            action with { Value = ControlValue.FromNumeric(requested) },
            previousValue,
            DateTimeOffset.UtcNow,
            rollback);
    }

    public async Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        EnsureOwnedCapability(action.Action.CapabilityId);
        int duty = RequireDuty(action.Action.Value);
        GpuFanBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (duty < bounds.FloorPercent || duty > bounds.CeilingPercent)
        {
            // Defence in depth: never let a value that skipped Prepare reach the hardware.
            throw new GpuFanSafetyException(
                $"Refusing to apply GPU fan duty {duty}% outside the safe range [{bounds.FloorPercent}, {bounds.CeilingPercent}]%.");
        }

        await _transport.SetManualDutyAsync(_channelId, duty, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        int requested = RequireDuty(action.Action.Value);
        GpuFanBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
        GpuFanChannelState state = await ReadSettledStateAsync(requested, cancellationToken).ConfigureAwait(false);
        int? achieved = state.MeasuredDutyPercent ?? state.CommandedDutyPercent;
        bool manual = state.Policy == GpuFanControlPolicy.Manual;
        bool atTarget = achieved is int value && Math.Abs(value - requested) <= VerifyTolerancePercent;
        bool zeroRpmIdle = manual && IsAcceptableZeroRpmIdle(achieved, requested, bounds);
        bool success = manual && (atTarget || zeroRpmIdle);
        string message = !success
            ? $"GPU fan duty read-back {achieved?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}% did not match requested {requested}%."
            : zeroRpmIdle
                ? $"GPU fan is in the card's zero-RPM idle (stopped); the manual {requested}% target applies once the card needs airflow."
                : $"GPU fan duty verified within {VerifyTolerancePercent}% of {requested}%.";
        return new ActionVerification(
            action.Action.Id,
            success,
            achieved is int observed ? ControlValue.FromNumeric(observed) : null,
            message);
    }

    /// <summary>
    /// A GPU with zero-RPM idle keeps its fan physically stopped (0%) at low
    /// temperatures even under manual control: the commanded duty is the target the
    /// firmware honours once the card needs airflow, not an unconditional spin
    /// command. On the reference RTX 3090 a manual 30% at ~35 °C reads back 0% and
    /// the fan simply stays off — the card's designed passive-cooling behaviour, not
    /// a control failure, and forcing it to spin a cool card would be worse for
    /// noise and wear. A stopped fan is therefore an acceptable realisation of any
    /// duty <em>below</em> the controller maximum. A maximum/emergency command (the
    /// only duty that exists purely for thermal safety) must still physically spin
    /// the fan, so a genuine "will not reach full speed" fault is still caught — and
    /// at emergency temperatures the card is far too hot for zero-RPM to engage, so
    /// this never suppresses real cooling. The card's own hardware thermal
    /// throttle/shutdown remains the actual safeguard; this control is supplementary.
    /// </summary>
    private static bool IsAcceptableZeroRpmIdle(int? achieved, int requestedDutyPercent, GpuFanBounds? bounds) =>
        achieved is 0
        && bounds is { IsValid: true } valid
        && requestedDutyPercent < valid.CeilingPercent;

    /// <summary>
    /// Reads the channel state, giving a ramping fan up to the settle window to
    /// converge on <paramref name="targetDutyPercent"/>. Polling stops as soon as
    /// the level is within tolerance, so a fan that is already there (or a fake in
    /// a test) returns immediately with no delay. Polling also stops the instant the
    /// manual policy is absent — if the write did not take there is nothing to wait
    /// for, and the caller's own policy check will fail it.
    /// </summary>
    private async Task<GpuFanChannelState> ReadSettledStateAsync(int targetDutyPercent, CancellationToken cancellationToken)
    {
        GpuFanChannelState state = await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < FanSettleAttempts; attempt++)
        {
            if (state.Policy != GpuFanControlPolicy.Manual || IsWithinTolerance(state, targetDutyPercent))
            {
                break;
            }

            await _settleDelay(FanSettleInterval, cancellationToken).ConfigureAwait(false);
            state = await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        }

        return state;
    }

    private static bool IsWithinTolerance(GpuFanChannelState state, int targetDutyPercent) =>
        (state.MeasuredDutyPercent ?? state.CommandedDutyPercent) is int level
        && Math.Abs(level - targetDutyPercent) <= VerifyTolerancePercent;

    public async Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        GpuFanChannelState? previous = DeserializeRollback(action.AdapterToken);
        if (previous is null || previous.Policy == GpuFanControlPolicy.Automatic)
        {
            // No captured manual state, or it was automatic before: return to the safe
            // automatic curve rather than leaving an unknown manual duty in place.
            await RestoreAutomaticWithRetryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (previous.CommandedDutyPercent is int prior)
        {
            await _transport.SetManualDutyAsync(_channelId, prior, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RestoreAutomaticWithRetryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(capabilityId);
        await RestoreAutomaticWithRetryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A live GPU-fan auto-mode switch reproduced the driver refusing the restore-to-
    /// default call itself (NVAPI_INVALID_USER_PRIVILEGE) moments after the preceding
    /// apply's settle-poll made a burst of rapid NVAPI reads and a write — the same
    /// rapid-call driver-session fragility this session already found for clock-offset
    /// writes, this time on the fan's own safety restore. Every caller of this restore
    /// (rollback, explicit reset, and the cooling-graph recovery path) escalates an
    /// unrecovered failure straight to a full hardware write lock, so the one operation
    /// that must not fail on a transient refusal is retried a few times with a short
    /// gap first.
    /// </summary>
    private async Task RestoreAutomaticWithRetryAsync(CancellationToken cancellationToken)
    {
        await _settleDelay(ResetCooldownInterval, cancellationToken).ConfigureAwait(false);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _transport.RestoreAutomaticAsync(_channelId, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt < ResetRetryAttempts)
            {
                await _settleDelay(ResetRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<HardwareStateVerification> VerifyDefaultStateAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(capabilityId);
        GpuFanChannelState state = await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        bool success = state.Policy == GpuFanControlPolicy.Automatic;
        return new HardwareStateVerification(
            Manifest.Id,
            capabilityId,
            success,
            state.MeasuredDutyPercent is int observed ? ControlValue.FromNumeric(observed) : null,
            success
                ? "GPU fan read-back confirmed the driver automatic policy."
                : $"GPU fan read-back remained in {state.Policy} policy.");
    }

    public async Task<HardwareStateVerification> VerifyRollbackStateAsync(
        PreparedAction action,
        CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        GpuFanChannelState? expected = DeserializeRollback(action.AdapterToken);
        GpuFanBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
        // Restoring a prior MANUAL duty ramps just like an apply, so let the level
        // settle toward it; restoring the automatic curve is a policy flip with no
        // ramp, so a single read is enough.
        GpuFanChannelState actual = expected is { Policy: GpuFanControlPolicy.Manual, CommandedDutyPercent: int target }
            ? await ReadSettledStateAsync(target, cancellationToken).ConfigureAwait(false)
            : await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        bool success = expected is null || expected.Policy == GpuFanControlPolicy.Automatic
            ? actual.Policy == GpuFanControlPolicy.Automatic
            : actual.Policy == GpuFanControlPolicy.Manual
                && expected.CommandedDutyPercent is int requested
                && (actual.MeasuredDutyPercent ?? actual.CommandedDutyPercent) is int observed
                && (Math.Abs(observed - requested) <= VerifyTolerancePercent
                    // The prior manual duty may itself have been a zero-RPM-idle state:
                    // restoring it can validly read back a stopped fan. Same rule as VerifyAsync.
                    || IsAcceptableZeroRpmIdle(observed, requested, bounds));
        return new HardwareStateVerification(
            Manifest.Id,
            action.Action.CapabilityId,
            success,
            actual.MeasuredDutyPercent is int value ? ControlValue.FromNumeric(value) : null,
            success ? "GPU fan rollback state was read back." : "GPU fan rollback read-back did not match the captured state.");
    }

    public async Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            GpuFanBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
            bool healthy = bounds is { IsValid: true };
            return new AdapterHealth(
                AdapterId,
                healthy,
                DateTimeOffset.UtcNow,
                healthy ? "GPU fan transport reported valid bounds." : "GPU fan bounds are unavailable or invalid.",
                healthy ? [] : ["GPU fan bounds unavailable."]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AdapterHealth(AdapterId, false, DateTimeOffset.UtcNow, "GPU fan transport is unavailable.", [exception.Message]);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void EnsureWritesEnabled()
    {
        if (!_isArmed())
        {
            throw new NotSupportedException(
                "GPU fan writes are not armed. Arm the capability with an Experimental acknowledgement for this device first.");
        }
    }

    private void EnsureOwnedCapability(string capabilityId)
    {
        if (!string.Equals(capabilityId, CapabilityId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Capability '{capabilityId}' is not owned by the GPU fan adapter.", nameof(capabilityId));
        }
    }

    private async Task EnsureNotConflictedAsync(CancellationToken cancellationToken)
    {
        if (await _isConflicted(cancellationToken).ConfigureAwait(false))
        {
            throw new GpuFanSafetyException("A competing GPU fan writer is active; refusing to prepare a write.");
        }
    }

    private async Task<GpuFanBounds> RequireBoundsAsync(CancellationToken cancellationToken)
    {
        GpuFanBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
        if (bounds is not { IsValid: true } valid)
        {
            throw new GpuFanSafetyException("GPU fan bounds are unavailable; refusing to write.");
        }

        return valid;
    }

    private static int RequireDuty(ControlValue value)
    {
        if (value.Kind != ControlValueKind.Numeric || value.Numeric is not double numeric || !double.IsFinite(numeric))
        {
            throw new ArgumentException("A finite numeric GPU fan duty is required.", nameof(value));
        }

        return (int)Math.Round(numeric);
    }

    private static GpuFanChannelState? DeserializeRollback(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GpuFanChannelState>(token);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
