using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Bounded, Experimental GPU clock-offset control (one adapter instance per
/// domain: core or memory) on the proven prepare/apply/verify/rollback/reset
/// shape. The capability value is megahertz; the transport works in kilohertz.
/// The driver-reported delta range discovered read-only before any write is the
/// authoritative clamp, rollback restores the exact prior offset, and reset
/// returns to 0 kHz (stock clocks). Clock offsets are Manual Only: never
/// applied automatically or at startup, and no voltage parameter is ever
/// touched.
///
/// Like the fan and power-limit adapters, a live write is possible only while
/// the capability is armed via an acknowledged operator action; the default
/// registration is a read-only card, so wiring the adapter in cannot by itself
/// un-gate a write.
/// </summary>
public sealed class NvidiaGpuClockOffsetAdapter : IHardwareAdapter, IHardwareStateVerifier
{
    // One adapter instance per domain is registered, and the transaction engine
    // keys adapters by manifest id — so each domain MUST carry a distinct id.
    public const string CoreAdapterId = "nvidia.gpuclock.core";
    public const string MemoryAdapterId = "nvidia.gpuclock.memory";
    public const string CorePrefix = "gpuclock.core:";
    public const string MemoryPrefix = "gpuclock.memory:";

    private const int VerifyToleranceKiloHertz = 1000;

    private readonly IGpuClockOffsetTransport _transport;
    private readonly GpuClockOffsetDomain _domain;
    private readonly string _deviceId;
    private readonly string _channelId;
    private readonly Func<bool> _isArmed;
    private readonly Func<CancellationToken, Task<bool>> _isConflicted;

    public NvidiaGpuClockOffsetAdapter(
        IGpuClockOffsetTransport transport,
        GpuClockOffsetDomain domain,
        string deviceId,
        string channelId,
        Func<bool> isArmed,
        Func<CancellationToken, Task<bool>>? isConflicted = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        _domain = domain;
        _deviceId = deviceId;
        _channelId = channelId;
        _isArmed = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
        _isConflicted = isConflicted ?? (_ => Task.FromResult(false));
        Manifest = new AdapterManifest(
            domain == GpuClockOffsetDomain.Core ? CoreAdapterId : MemoryAdapterId,
            $"NVIDIA GPU {DomainLabel} clock offset (Experimental)",
            "0.5.0-alpha",
            "NVIDIA display driver (NVAPI performance states 2.0)",
            "NVIDIA display driver exposing editable P0 frequency deltas",
            AdapterExecutionContext.SystemService,
            ["NVIDIA GPU with editable NVAPI performance states"],
            ["GpuClockOffsetExperimental"]);
    }

    public string CapabilityId => $"{(_domain == GpuClockOffsetDomain.Core ? CorePrefix : MemoryPrefix)}{_channelId}";

    private string DomainLabel => _domain == GpuClockOffsetDomain.Core ? "core" : "memory";

    public AdapterManifest Manifest { get; }

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        GpuClockOffsetBounds? bounds = await _transport.ReadBoundsAsync(_domain, cancellationToken).ConfigureAwait(false);
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
            int minMhz = ToMegaHertz(valid.MinimumKiloHertz);
            int maxMhz = ToMegaHertz(valid.MaximumKiloHertz);
            string reason = conflicted
                ? "A competing GPU tuning writer is active; this control is blocked until it releases the GPU."
                : armed
                    ? $"Manual GPU {DomainLabel} clock offset is clamped to the driver delta range [{minMhz}, {maxMhz}] MHz. Manual Only: never applied automatically or at startup; reset returns to stock clocks (0 MHz). No voltage parameter is touched."
                    : $"A GPU {DomainLabel} clock offset within [{minMhz}, {maxMhz}] MHz is feasible, but remains read-only until it is explicitly armed with an Experimental acknowledgement for this device.";

            capabilities.Add(new CapabilityDescriptor(
                CapabilityId,
                Manifest.Id,
                _deviceId,
                $"GPU {DomainLabel} clock offset",
                state,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(minMhz, maxMhz, 1),
                "MHz",
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

        int requested = RequireKiloHertz(action.Value);
        GpuClockOffsetBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (requested < bounds.MinimumKiloHertz || requested > bounds.MaximumKiloHertz)
        {
            throw new GpuClockSafetyException(
                $"Requested GPU {DomainLabel} clock offset {ToMegaHertz(requested)} MHz is outside the driver delta range [{ToMegaHertz(bounds.MinimumKiloHertz)}, {ToMegaHertz(bounds.MaximumKiloHertz)}] MHz.");
        }

        GpuClockOffsetState previous = await _transport.ReadStateAsync(_domain, cancellationToken).ConfigureAwait(false);
        string rollback = JsonSerializer.Serialize(previous);
        ControlValue? previousValue = previous.CurrentKiloHertz is int prior
            ? ControlValue.FromNumeric(ToMegaHertz(prior))
            : null;
        return new PreparedAction(
            action with { Value = ControlValue.FromNumeric(ToMegaHertz(requested)) },
            previousValue,
            DateTimeOffset.UtcNow,
            rollback);
    }

    public async Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        EnsureOwnedCapability(action.Action.CapabilityId);
        int offset = RequireKiloHertz(action.Action.Value);
        GpuClockOffsetBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (offset < bounds.MinimumKiloHertz || offset > bounds.MaximumKiloHertz)
        {
            // Defence in depth: never let a value that skipped Prepare reach the hardware.
            throw new GpuClockSafetyException(
                $"Refusing to apply GPU {DomainLabel} clock offset {ToMegaHertz(offset)} MHz outside the driver delta range [{ToMegaHertz(bounds.MinimumKiloHertz)}, {ToMegaHertz(bounds.MaximumKiloHertz)}] MHz.");
        }

        await _transport.SetOffsetAsync(_domain, offset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        int requested = RequireKiloHertz(action.Action.Value);
        GpuClockOffsetState state = await _transport.ReadStateAsync(_domain, cancellationToken).ConfigureAwait(false);
        bool success = state.CurrentKiloHertz is int achieved
            && Math.Abs((long)achieved - requested) <= VerifyToleranceKiloHertz;
        return new ActionVerification(
            action.Action.Id,
            success,
            state.CurrentKiloHertz is int observed ? ControlValue.FromNumeric(ToMegaHertz(observed)) : null,
            success
                ? $"GPU {DomainLabel} clock offset verified at {ToMegaHertz(requested)} MHz."
                : $"GPU {DomainLabel} clock offset read-back {(state.CurrentKiloHertz is int value ? ToMegaHertz(value).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable")} MHz did not match requested {ToMegaHertz(requested)} MHz.");
    }

    public async Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        GpuClockOffsetState? previous = DeserializeRollback(action.AdapterToken);
        GpuClockOffsetBounds bounds = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (previous?.CurrentKiloHertz is int prior
            && prior >= bounds.MinimumKiloHertz
            && prior <= bounds.MaximumKiloHertz)
        {
            await _transport.SetOffsetAsync(_domain, prior, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No captured prior offset (or it is no longer within the driver range):
        // return to stock clocks rather than leaving an unknown offset in place.
        await _transport.SetOffsetAsync(_domain, GpuClockOffsetBounds.DefaultKiloHertz, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(capabilityId);
        _ = await RequireBoundsAsync(cancellationToken).ConfigureAwait(false);
        await _transport.SetOffsetAsync(_domain, GpuClockOffsetBounds.DefaultKiloHertz, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HardwareStateVerification> VerifyDefaultStateAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(capabilityId);
        GpuClockOffsetState state = await _transport.ReadStateAsync(_domain, cancellationToken).ConfigureAwait(false);
        bool success = state.CurrentKiloHertz is int observed
            && Math.Abs((long)observed - GpuClockOffsetBounds.DefaultKiloHertz) <= VerifyToleranceKiloHertz;
        return new HardwareStateVerification(
            Manifest.Id,
            capabilityId,
            success,
            state.CurrentKiloHertz is int value ? ControlValue.FromNumeric(ToMegaHertz(value)) : null,
            success ? $"GPU {DomainLabel} clock read-back confirmed the stock offset." : $"GPU {DomainLabel} clock read-back did not confirm the stock offset.");
    }

    public async Task<HardwareStateVerification> VerifyRollbackStateAsync(
        PreparedAction action,
        CancellationToken cancellationToken)
    {
        EnsureOwnedCapability(action.Action.CapabilityId);
        GpuClockOffsetState? prior = DeserializeRollback(action.AdapterToken);
        int expected = prior?.CurrentKiloHertz ?? GpuClockOffsetBounds.DefaultKiloHertz;
        GpuClockOffsetState actual = await _transport.ReadStateAsync(_domain, cancellationToken).ConfigureAwait(false);
        bool success = actual.CurrentKiloHertz is int observed
            && Math.Abs((long)observed - expected) <= VerifyToleranceKiloHertz;
        return new HardwareStateVerification(
            Manifest.Id,
            action.Action.CapabilityId,
            success,
            actual.CurrentKiloHertz is int value ? ControlValue.FromNumeric(ToMegaHertz(value)) : null,
            success ? $"GPU {DomainLabel} clock rollback state was read back." : $"GPU {DomainLabel} clock rollback read-back did not match the captured state.");
    }

    public async Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            GpuClockOffsetBounds? bounds = await _transport.ReadBoundsAsync(_domain, cancellationToken).ConfigureAwait(false);
            bool healthy = bounds is { IsValid: true };
            return new AdapterHealth(
                Manifest.Id,
                healthy,
                DateTimeOffset.UtcNow,
                healthy ? "GPU clock-offset transport reported a valid delta range." : "GPU clock-offset delta range is unavailable or invalid.",
                healthy ? [] : ["GPU clock-offset delta range unavailable."]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AdapterHealth(Manifest.Id, false, DateTimeOffset.UtcNow, "GPU clock-offset transport is unavailable.", [exception.Message]);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void EnsureWritesEnabled()
    {
        if (!_isArmed())
        {
            throw new NotSupportedException(
                "GPU clock-offset writes are not armed. Arm the capability with an Experimental acknowledgement for this device first.");
        }
    }

    private void EnsureOwnedCapability(string capabilityId)
    {
        if (!string.Equals(capabilityId, CapabilityId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Capability '{capabilityId}' is not owned by the GPU {DomainLabel} clock-offset adapter.", nameof(capabilityId));
        }
    }

    private async Task EnsureNotConflictedAsync(CancellationToken cancellationToken)
    {
        if (await _isConflicted(cancellationToken).ConfigureAwait(false))
        {
            throw new GpuClockSafetyException("A competing GPU tuning writer is active; refusing to prepare a write.");
        }
    }

    private async Task<GpuClockOffsetBounds> RequireBoundsAsync(CancellationToken cancellationToken)
    {
        GpuClockOffsetBounds? bounds = await _transport.ReadBoundsAsync(_domain, cancellationToken).ConfigureAwait(false);
        if (bounds is not { IsValid: true } valid)
        {
            throw new GpuClockSafetyException("The GPU clock-offset delta range is unavailable; refusing to write.");
        }

        return valid;
    }

    private static int RequireKiloHertz(ControlValue value)
    {
        if (value.Kind != ControlValueKind.Numeric || value.Numeric is not double numeric || !double.IsFinite(numeric))
        {
            throw new ArgumentException("A finite numeric GPU clock offset in megahertz is required.", nameof(value));
        }

        return checked((int)Math.Round(numeric * 1000d));
    }

    private static int ToMegaHertz(int kiloHertz) => (int)Math.Round(kiloHertz / 1000d);

    private static GpuClockOffsetState? DeserializeRollback(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GpuClockOffsetState>(token);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
