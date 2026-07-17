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
public sealed class NvidiaGpuFanAdapter : IHardwareAdapter
{
    public const string AdapterId = "nvidia.gpufan";
    public const string CapabilityPrefix = "gpufan.duty:";

    private const int VerifyTolerancePercent = 5;

    private readonly IGpuFanCoolerTransport _transport;
    private readonly string _deviceId;
    private readonly string _channelId;
    private readonly Func<bool> _isArmed;
    private readonly Func<CancellationToken, Task<bool>> _isConflicted;

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
        Func<CancellationToken, Task<bool>>? isConflicted = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        _deviceId = deviceId;
        _channelId = channelId;
        _isArmed = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
        _isConflicted = isConflicted ?? (_ => Task.FromResult(false));
    }

    public string CapabilityId => $"{CapabilityPrefix}{_channelId}";

    public AdapterManifest Manifest { get; } = new(
        AdapterId,
        "NVIDIA GPU fan control (Experimental)",
        "0.5.0-alpha",
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
        GpuFanChannelState state = await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        int? achieved = state.MeasuredDutyPercent ?? state.CommandedDutyPercent;
        bool success = state.Policy == GpuFanControlPolicy.Manual
            && achieved is int value
            && Math.Abs(value - requested) <= VerifyTolerancePercent;
        return new ActionVerification(
            action.Action.Id,
            success,
            achieved is int observed ? ControlValue.FromNumeric(observed) : null,
            success
                ? $"GPU fan duty verified within {VerifyTolerancePercent}% of {requested}%."
                : $"GPU fan duty read-back {achieved?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}% did not match requested {requested}%.");
    }

    public async Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        GpuFanChannelState? previous = DeserializeRollback(action.AdapterToken);
        if (previous is null || previous.Policy == GpuFanControlPolicy.Automatic)
        {
            // No captured manual state, or it was automatic before: return to the safe
            // automatic curve rather than leaving an unknown manual duty in place.
            await _transport.RestoreAutomaticAsync(_channelId, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (previous.CommandedDutyPercent is int prior)
        {
            await _transport.SetManualDutyAsync(_channelId, prior, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _transport.RestoreAutomaticAsync(_channelId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(capabilityId);
        await _transport.RestoreAutomaticAsync(_channelId, cancellationToken).ConfigureAwait(false);
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
