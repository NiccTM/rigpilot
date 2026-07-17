using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Bounded, Experimental GPU power-limit control on the proven
/// prepare/apply/verify/rollback/reset shape. The capability value is watts; the
/// transport works in milliwatts. Every request is clamped to the vendor-reported
/// constraint range discovered read-only before any write, rollback restores the
/// exact prior limit, and reset returns the vendor default limit. Power limits are
/// Manual Only: never applied automatically or at startup.
///
/// Like the GPU-fan adapter, a live write is possible only while the capability is
/// armed via an acknowledged operator action; the default registration is a
/// read-only card, so wiring the adapter in cannot by itself un-gate a write.
/// </summary>
public sealed class NvidiaGpuPowerLimitAdapter : IHardwareAdapter
{
    public const string AdapterId = "nvidia.gpupower";
    public const string CapabilityPrefix = "gpupower.limit:";

    private const int VerifyToleranceMilliwatts = 1000;

    private readonly IGpuPowerLimitTransport _transport;
    private readonly string _deviceId;
    private readonly string _channelId;
    private readonly Func<bool> _isArmed;
    private readonly Func<CancellationToken, Task<bool>> _isConflicted;

    public NvidiaGpuPowerLimitAdapter(
        IGpuPowerLimitTransport transport,
        string deviceId,
        string channelId,
        bool enableWrites = false,
        Func<CancellationToken, Task<bool>>? isConflicted = null)
        : this(transport, deviceId, channelId, () => enableWrites, isConflicted)
    {
    }

    public NvidiaGpuPowerLimitAdapter(
        IGpuPowerLimitTransport transport,
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
        "NVIDIA GPU power limit (Experimental)",
        "0.5.0-alpha",
        "NVIDIA display driver",
        "NVIDIA display driver exposing power-management limit constraints",
        AdapterExecutionContext.SystemService,
        ["NVIDIA GPU with driver-reported power-limit constraints"],
        ["GpuPowerLimitExperimental"]);

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        GpuPowerLimitBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
        bool conflicted = await _isConflicted(cancellationToken).ConfigureAwait(false);
        List<CapabilityDescriptor> capabilities = [];

        if (bounds is { IsValid: true } valid)
        {
            bool armed = _isArmed();
            CapabilityAccessState state = conflicted
                ? CapabilityAccessState.Blocked
                : armed
                    ? CapabilityAccessState.Experimental
                    : CapabilityAccessState.ReadOnly;
            int minWatts = ToWatts(valid.MinimumMilliwatts);
            int maxWatts = ToWatts(valid.MaximumMilliwatts);
            int defaultWatts = ToWatts(valid.DefaultMilliwatts);
            string reason = conflicted
                ? "A competing GPU tuning writer is active; this control is blocked until it releases the GPU."
                : armed
                    ? $"Manual GPU power limit is clamped to the driver constraint range [{minWatts}, {maxWatts}] W (default {defaultWatts} W). Manual Only: never applied automatically or at startup; reset returns the vendor default."
                    : $"A GPU power limit within [{minWatts}, {maxWatts}] W is feasible (default {defaultWatts} W), but remains read-only until it is explicitly armed with an Experimental acknowledgement for this device.";

            capabilities.Add(new CapabilityDescriptor(
                CapabilityId,
                AdapterId,
                _deviceId,
                "GPU power limit",
                state,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(minWatts, maxWatts, 1, defaultWatts),
                "W",
                RiskLevel.Experimental,
                EvidenceLevel.Detected,
                conflicted ? "Competing GPU tuning writer" : null,
                reason,
                CanResetToDefault: true,
                Domain: ControlDomain.Gpu));
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

        uint requested = RequireMilliwatts(action.Value);
        GpuPowerLimitBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (requested < bounds.MinimumMilliwatts || requested > bounds.MaximumMilliwatts)
        {
            throw new GpuPowerSafetyException(
                $"Requested GPU power limit {ToWatts(requested)} W is outside the driver constraint range [{ToWatts(bounds.MinimumMilliwatts)}, {ToWatts(bounds.MaximumMilliwatts)}] W.");
        }

        GpuPowerLimitState previous = await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        string rollback = JsonSerializer.Serialize(previous);
        ControlValue? previousValue = previous.CurrentMilliwatts is uint prior
            ? ControlValue.FromNumeric(ToWatts(prior))
            : null;
        return new PreparedAction(
            action with { Value = ControlValue.FromNumeric(ToWatts(requested)) },
            previousValue,
            DateTimeOffset.UtcNow,
            rollback);
    }

    public async Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        EnsureOwnedCapability(action.Action.CapabilityId);
        uint milliwatts = RequireMilliwatts(action.Action.Value);
        GpuPowerLimitBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (milliwatts < bounds.MinimumMilliwatts || milliwatts > bounds.MaximumMilliwatts)
        {
            // Defence in depth: never let a value that skipped Prepare reach the hardware.
            throw new GpuPowerSafetyException(
                $"Refusing to apply GPU power limit {ToWatts(milliwatts)} W outside the driver constraint range [{ToWatts(bounds.MinimumMilliwatts)}, {ToWatts(bounds.MaximumMilliwatts)}] W.");
        }

        await _transport.SetPowerLimitAsync(_channelId, milliwatts, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        uint requested = RequireMilliwatts(action.Action.Value);
        GpuPowerLimitState state = await _transport.ReadStateAsync(_channelId, cancellationToken).ConfigureAwait(false);
        bool success = state.CurrentMilliwatts is uint achieved
            && Math.Abs((long)achieved - requested) <= VerifyToleranceMilliwatts;
        return new ActionVerification(
            action.Action.Id,
            success,
            state.CurrentMilliwatts is uint observed ? ControlValue.FromNumeric(ToWatts(observed)) : null,
            success
                ? $"GPU power limit verified at {ToWatts(requested)} W."
                : $"GPU power limit read-back {(state.CurrentMilliwatts is uint value ? ToWatts(value).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable")} W did not match requested {ToWatts(requested)} W.");
    }

    public async Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        GpuPowerLimitState? previous = DeserializeRollback(action.AdapterToken);
        GpuPowerLimitBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (previous?.CurrentMilliwatts is uint prior
            && prior >= bounds.MinimumMilliwatts
            && prior <= bounds.MaximumMilliwatts)
        {
            await _transport.SetPowerLimitAsync(_channelId, prior, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No captured prior limit (or it is no longer within the constraints):
        // return to the vendor default rather than leaving an unknown limit in place.
        await _transport.SetPowerLimitAsync(_channelId, bounds.DefaultMilliwatts, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(capabilityId);
        GpuPowerLimitBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        await _transport.SetPowerLimitAsync(_channelId, bounds.DefaultMilliwatts, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            GpuPowerLimitBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
            bool healthy = bounds is { IsValid: true };
            return new AdapterHealth(
                AdapterId,
                healthy,
                DateTimeOffset.UtcNow,
                healthy ? "GPU power-limit transport reported valid constraints." : "GPU power-limit constraints are unavailable or invalid.",
                healthy ? [] : ["GPU power-limit constraints unavailable."]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AdapterHealth(AdapterId, false, DateTimeOffset.UtcNow, "GPU power-limit transport is unavailable.", [exception.Message]);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void EnsureWritesEnabled()
    {
        if (!_isArmed())
        {
            throw new NotSupportedException(
                "GPU power-limit writes are not armed. Arm the capability with an Experimental acknowledgement for this device first.");
        }
    }

    private void EnsureOwnedCapability(string capabilityId)
    {
        if (!string.Equals(capabilityId, CapabilityId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Capability '{capabilityId}' is not owned by the GPU power-limit adapter.", nameof(capabilityId));
        }
    }

    private async Task EnsureNotConflictedAsync(CancellationToken cancellationToken)
    {
        if (await _isConflicted(cancellationToken).ConfigureAwait(false))
        {
            throw new GpuPowerSafetyException("A competing GPU tuning writer is active; refusing to prepare a write.");
        }
    }

    private async Task<GpuPowerLimitBounds> RequireBoundsAsync(CancellationToken cancellationToken)
    {
        GpuPowerLimitBounds? bounds = await _transport.ReadBoundsAsync(_channelId, cancellationToken).ConfigureAwait(false);
        if (bounds is not { IsValid: true } valid)
        {
            throw new GpuPowerSafetyException("GPU power-limit constraints are unavailable; refusing to write.");
        }

        return valid;
    }

    private static uint RequireMilliwatts(ControlValue value)
    {
        if (value.Kind != ControlValueKind.Numeric || value.Numeric is not double numeric || !double.IsFinite(numeric) || numeric <= 0)
        {
            throw new ArgumentException("A finite positive numeric GPU power limit in watts is required.", nameof(value));
        }

        return (uint)Math.Round(numeric * 1000d);
    }

    private static int ToWatts(uint milliwatts) => (int)Math.Round(milliwatts / 1000d);

    private static GpuPowerLimitState? DeserializeRollback(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GpuPowerLimitState>(token);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
