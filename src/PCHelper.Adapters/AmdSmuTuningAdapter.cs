using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Bounded PBO-limit tuning (PPT/TDC/EDC) on the proven
/// prepare/apply/verify/rollback/reset shape — built entirely behind the CPU
/// qualification gate (docs/qualification/cpu-tuning-and-intel-arc.md). Two
/// independent gates must BOTH hold before a write can reach the transport:
///
///  1. a qualification witness (<c>isQualified</c>): audited per-family bounds,
///     boot-recovery qualification and a witnessed controlled pass — false on
///     every system today, so the capabilities surface as Blocked; and
///  2. the acknowledged arm (<c>isArmed</c>): the same per-session Experimental
///     acknowledgement the GPU write families use.
///
/// Every apply is journalled through the boot-recovery sentinel BEFORE the
/// transport is commanded and settled only after read-back verification, so an
/// unclean boot always reverts to vendor stock. No production transport exists;
/// the adapter is exercised only against in-memory fakes in tests. Voltage is
/// not modelled anywhere in this seam.
/// </summary>
public sealed class AmdSmuTuningAdapter : IHardwareAdapter
{
    public const string AdapterIdValue = "amd.smu.pbo-limits";
    public const string PptPrefix = "smutuning.ppt:";
    public const string TdcPrefix = "smutuning.tdc:";
    public const string EdcPrefix = "smutuning.edc:";

    private readonly ISmuTuningTransport _transport;
    private readonly CpuTuneBootSentinel _sentinel;
    private readonly string _deviceId;
    private readonly Func<bool> _isQualified;
    private readonly Func<bool> _isArmed;

    public AmdSmuTuningAdapter(
        ISmuTuningTransport transport,
        CpuTuneBootSentinel sentinel,
        string deviceId,
        Func<bool> isQualified,
        Func<bool> isArmed)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sentinel = sentinel ?? throw new ArgumentNullException(nameof(sentinel));
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _deviceId = deviceId;
        _isQualified = isQualified ?? throw new ArgumentNullException(nameof(isQualified));
        _isArmed = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
    }

    public AdapterManifest Manifest { get; } = new(
        AdapterIdValue,
        "AMD Ryzen PBO limit tuning (qualification-gated)",
        "0.6.0-beta.1",
        "AMD SMU mailbox via signed PawnIO (no transport implemented)",
        "Signed PawnIO RyzenSMU module",
        AdapterExecutionContext.SystemService,
        ["AMD Ryzen CPU on an audited Zen family with a passed CPU-tuning qualification"],
        ["CpuTuningQualificationGated", "BootRecoverySentinelRequired"]);

    public async Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        List<CapabilityDescriptor> capabilities = [];
        foreach (SmuTuningParameter parameter in (SmuTuningParameter[])
                 [SmuTuningParameter.PptWatts, SmuTuningParameter.TdcAmps, SmuTuningParameter.EdcAmps])
        {
            SmuTuningBounds? bounds = await _transport.ReadBoundsAsync(parameter, cancellationToken).ConfigureAwait(false);
            if (bounds is not { IsValid: true } valid)
            {
                continue;
            }

            bool qualified = _isQualified();
            bool armed = _isArmed();
            CapabilityAccessState state = !qualified
                ? CapabilityAccessState.Blocked
                : armed
                    ? CapabilityAccessState.Experimental
                    : CapabilityAccessState.ReadOnly;
            string reason = !qualified
                ? "CPU tuning is blocked by the qualification gate: audited per-family SMU bounds, boot-recovery qualification and a witnessed controlled pass are required before any live PBO write (docs/qualification/cpu-tuning-and-intel-arc.md). No qualification record exists for this system."
                : armed
                    ? $"Manual PBO {ParameterLabel(parameter)} limit is clamped to [{valid.Minimum}, {valid.Maximum}] {Unit(parameter)} with stock {valid.StockValue}. Manual Only: journalled through the boot-recovery sentinel, never applied automatically or at startup."
                    : $"A PBO {ParameterLabel(parameter)} limit within [{valid.Minimum}, {valid.Maximum}] {Unit(parameter)} is qualified for this system, but remains read-only until armed with an Experimental acknowledgement for this exact CPU.";

            capabilities.Add(new CapabilityDescriptor(
                CapabilityId(parameter),
                Manifest.Id,
                _deviceId,
                $"CPU PBO {ParameterLabel(parameter)} limit",
                state,
                AdapterExecutionContext.SystemService,
                ControlValueKind.Numeric,
                new NumericRange(valid.Minimum, valid.Maximum, 1),
                Unit(parameter),
                RiskLevel.Experimental,
                EvidenceLevel.Detected,
                qualified ? null : "CPU tuning qualification gate",
                reason,
                CanResetToDefault: true,
                Domain: ControlDomain.Cpu));
        }

        return new AdapterProbeResult(Manifest, [], capabilities, []);
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SensorSample>>([]);

    public async Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        SmuTuningParameter parameter = RequireOwnedParameter(action.CapabilityId);
        int requested = RequireInteger(action.Value, parameter);
        SmuTuningBounds bounds = await RequireBoundsAsync(parameter, cancellationToken).ConfigureAwait(false);
        if (requested < bounds.Minimum || requested > bounds.Maximum)
        {
            throw new SmuTuningSafetyException(
                $"Requested PBO {ParameterLabel(parameter)} limit {requested} {Unit(parameter)} is outside the qualified range [{bounds.Minimum}, {bounds.Maximum}] {Unit(parameter)}.");
        }

        SmuTuningState previous = await _transport.ReadStateAsync(parameter, cancellationToken).ConfigureAwait(false);
        return new PreparedAction(
            action with { Value = ControlValue.FromNumeric(requested) },
            previous.CurrentValue is int prior ? ControlValue.FromNumeric(prior) : null,
            DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(previous));
    }

    public async Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        EnsureWritesEnabled();
        SmuTuningParameter parameter = RequireOwnedParameter(action.Action.CapabilityId);
        int requested = RequireInteger(action.Action.Value, parameter);
        SmuTuningBounds bounds = await RequireBoundsAsync(parameter, cancellationToken).ConfigureAwait(false);
        if (requested < bounds.Minimum || requested > bounds.Maximum)
        {
            // Defence in depth: never let a value that skipped Prepare reach the SMU.
            throw new SmuTuningSafetyException(
                $"Refusing to apply PBO {ParameterLabel(parameter)} limit {requested} {Unit(parameter)} outside the qualified range [{bounds.Minimum}, {bounds.Maximum}] {Unit(parameter)}.");
        }

        // The journal entry MUST be durable before the SMU write is commanded:
        // if the machine never settles, boot recovery restores vendor stock.
        _sentinel.BeginPendingTune(new CpuTuneJournalEntry(parameter.ToString(), requested, DateTimeOffset.UtcNow));
        try
        {
            await _transport.SetLimitAsync(parameter, requested, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The write itself failed, so nothing is outstanding; a retained
            // journal entry would permanently wedge further tunes.
            _sentinel.MarkSettled();
            throw;
        }
    }

    public async Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        SmuTuningParameter parameter = RequireOwnedParameter(action.Action.CapabilityId);
        int requested = RequireInteger(action.Action.Value, parameter);
        SmuTuningState state = await _transport.ReadStateAsync(parameter, cancellationToken).ConfigureAwait(false);
        bool success = state.CurrentValue == requested;
        if (success)
        {
            _sentinel.MarkSettled();
        }

        return new ActionVerification(
            action.Action.Id,
            success,
            state.CurrentValue is int observed ? ControlValue.FromNumeric(observed) : null,
            success
                ? $"PBO {ParameterLabel(parameter)} limit verified at {requested} {Unit(parameter)}; boot journal settled."
                : $"PBO {ParameterLabel(parameter)} limit read-back {(state.CurrentValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable")} did not match requested {requested} {Unit(parameter)}; boot journal remains pending.");
    }

    public async Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        SmuTuningParameter parameter = RequireOwnedParameter(action.Action.CapabilityId);
        SmuTuningBounds bounds = await RequireBoundsAsync(parameter, cancellationToken).ConfigureAwait(false);
        SmuTuningState? previous = DeserializeRollback(action.AdapterToken);
        int target = previous?.CurrentValue is int prior && prior >= bounds.Minimum && prior <= bounds.Maximum
            ? prior
            : bounds.StockValue;
        await _transport.SetLimitAsync(parameter, target, cancellationToken).ConfigureAwait(false);
        _sentinel.MarkSettled();
    }

    public async Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        _ = RequireOwnedParameter(capabilityId);
        await _transport.RestoreStockAsync(cancellationToken).ConfigureAwait(false);
        _sentinel.MarkSettled();
    }

    public async Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            SmuTuningBounds? bounds = await _transport.ReadBoundsAsync(SmuTuningParameter.PptWatts, cancellationToken).ConfigureAwait(false);
            bool healthy = bounds is { IsValid: true };
            return new AdapterHealth(
                Manifest.Id,
                healthy,
                DateTimeOffset.UtcNow,
                healthy ? "SMU tuning transport reported valid PBO bounds." : "SMU tuning bounds are unavailable or invalid.",
                healthy ? [] : ["SMU tuning bounds unavailable."]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AdapterHealth(Manifest.Id, false, DateTimeOffset.UtcNow, "SMU tuning transport is unavailable.", [exception.Message]);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public string CapabilityId(SmuTuningParameter parameter) => parameter switch
    {
        SmuTuningParameter.PptWatts => $"{PptPrefix}{_deviceId}",
        SmuTuningParameter.TdcAmps => $"{TdcPrefix}{_deviceId}",
        _ => $"{EdcPrefix}{_deviceId}"
    };

    private void EnsureWritesEnabled()
    {
        if (!_isQualified())
        {
            throw new SmuTuningSafetyException(
                "CPU tuning is blocked by the qualification gate; no PBO write is permitted on an unqualified system.");
        }

        if (!_isArmed())
        {
            throw new NotSupportedException(
                "CPU PBO tuning is not armed. Arm the capability with an Experimental acknowledgement for this exact CPU first.");
        }
    }

    private SmuTuningParameter RequireOwnedParameter(string capabilityId)
    {
        if (string.Equals(capabilityId, CapabilityId(SmuTuningParameter.PptWatts), StringComparison.Ordinal))
        {
            return SmuTuningParameter.PptWatts;
        }

        if (string.Equals(capabilityId, CapabilityId(SmuTuningParameter.TdcAmps), StringComparison.Ordinal))
        {
            return SmuTuningParameter.TdcAmps;
        }

        if (string.Equals(capabilityId, CapabilityId(SmuTuningParameter.EdcAmps), StringComparison.Ordinal))
        {
            return SmuTuningParameter.EdcAmps;
        }

        throw new ArgumentException($"Capability '{capabilityId}' is not owned by the SMU tuning adapter.", nameof(capabilityId));
    }

    private async Task<SmuTuningBounds> RequireBoundsAsync(SmuTuningParameter parameter, CancellationToken cancellationToken)
    {
        SmuTuningBounds? bounds = await _transport.ReadBoundsAsync(parameter, cancellationToken).ConfigureAwait(false);
        if (bounds is not { IsValid: true } valid)
        {
            throw new SmuTuningSafetyException($"The PBO {ParameterLabel(parameter)} bounds are unavailable; refusing to write.");
        }

        return valid;
    }

    private static int RequireInteger(ControlValue value, SmuTuningParameter parameter)
    {
        if (value.Kind != ControlValueKind.Numeric || value.Numeric is not double numeric
            || !double.IsFinite(numeric) || numeric != Math.Floor(numeric))
        {
            throw new ArgumentException($"A finite integer PBO {ParameterLabel(parameter)} limit is required.", nameof(value));
        }

        return checked((int)numeric);
    }

    private static string ParameterLabel(SmuTuningParameter parameter) => parameter switch
    {
        SmuTuningParameter.PptWatts => "PPT",
        SmuTuningParameter.TdcAmps => "TDC",
        _ => "EDC"
    };

    private static string Unit(SmuTuningParameter parameter) =>
        parameter == SmuTuningParameter.PptWatts ? "W" : "A";

    private static SmuTuningState? DeserializeRollback(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SmuTuningState>(token);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
