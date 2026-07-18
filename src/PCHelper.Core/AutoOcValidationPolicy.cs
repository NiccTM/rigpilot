using PCHelper.Contracts;

namespace PCHelper.Core;

public static class AutoOcValidationPolicy
{
    public static readonly TimeSpan RequiredActiveUse = TimeSpan.FromHours(10);
    public static readonly TimeSpan RequiredCalendarAge = TimeSpan.FromDays(7);
    public const int RequiredSuccessfulColdBoots = 3;
    private const int MaximumRetainedEvents = 64;

    /// <summary>
    /// Minimum GPU utilisation for an interval to count as active use. An
    /// overclock idling at the desktop proves nothing about stability, so
    /// wall-clock time is deliberately NOT evidence: only intervals where the
    /// tuned device is actually working accrue toward
    /// <see cref="RequiredActiveUse"/>.
    /// </summary>
    public const double QualifyingUtilisationPercent = 25;

    /// <summary>
    /// Longest interval a single sample may contribute. Sleep, hibernation, or
    /// a stalled telemetry loop would otherwise inject hours of unobserved
    /// "evidence" on the first sample after the gap.
    /// </summary>
    public static readonly TimeSpan MaximumSampleInterval = TimeSpan.FromSeconds(30);

    public static AutoOcProfileValidationV1 Create(AutoOcResultV3 result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ProfileV2 profile = result.GeneratedProfile
            ?? throw new ArgumentException("A validation record requires a generated profile.", nameof(result));
        if (result.ValidationState != AutoOcValidationState.Provisional
            || !result.AllRequestedFamiliesVerified
            || !result.RestorationProof.PriorStateRestored
            || !result.RestorationProof.HardwareStateKnown)
        {
            throw new ArgumentException("Only a provisional Auto OC result with verified restoration can create a validation record.", nameof(result));
        }

        return new AutoOcProfileValidationV1(
            AutoOcProfileValidationV1.CurrentSchemaVersion,
            profile.Id,
            profile.Id,
            result.HardwareFingerprint,
            AutoOcValidationState.Provisional,
            result.FinishedAt,
            result.FinishedAt,
            TimeSpan.Zero,
            SuccessfulColdBoots: 0,
            SuccessfulManualApplications: 0,
            ActiveSessionStartedAt: null,
            ActiveServiceInstanceId: null,
            RelevantEvents: [],
            "Provisional: requires 10 active-use hours, seven calendar days, three successful cold-boot sessions, and no relevant WHEA or display-driver-reset events.");
    }

    public static bool CanActivate(
        AutoOcProfileValidationV1 record,
        HardwareFingerprintV1 currentFingerprint,
        ProfileActivationSource source,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(currentFingerprint);
        if (!FingerprintMatches(record.HardwareFingerprint, currentFingerprint))
        {
            reason = "The current GPU identity, VBIOS, or display-driver fingerprint differs from the tune evidence.";
            return false;
        }
        if (record.State is AutoOcValidationState.Invalidated or AutoOcValidationState.RecoveryRequired or AutoOcValidationState.Rejected)
        {
            reason = record.Message;
            return false;
        }
        if (source is not ProfileActivationSource.Manual
            && (record.State != AutoOcValidationState.Validated || record.SuccessfulManualApplications == 0))
        {
            reason = "Foreground-game, startup, and automation activation require a fully Validated tune that has already succeeded manually.";
            return false;
        }

        reason = record.State == AutoOcValidationState.Validated
            ? "The tune fingerprint and completed validation evidence match this system."
            : "The tune is Provisional and may be activated manually while local evidence accumulates.";
        return true;
    }

    public static AutoOcProfileValidationV1 Activate(
        AutoOcProfileValidationV1 record,
        string serviceInstanceId,
        ProfileActivationSource source,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceInstanceId);
        // Defence in depth: CanActivate already refuses a terminal record, so
        // reaching here means a caller skipped the gate. Never open an
        // evidence session for a rejected, invalidated, or recovering tune.
        if (IsTerminal(record.State))
        {
            return record;
        }
        if (record.ActiveSessionStartedAt is not null
            && string.Equals(record.ActiveServiceInstanceId, serviceInstanceId, StringComparison.Ordinal))
        {
            return record;
        }

        return record with
        {
            UpdatedAt = now,
            ActiveSessionStartedAt = now,
            ActiveServiceInstanceId = serviceInstanceId,
            SuccessfulManualApplications = record.SuccessfulManualApplications
                + (source == ProfileActivationSource.Manual ? 1 : 0),
            Message = record.State == AutoOcValidationState.Validated
                ? "Validated tune is active; stability signals and fingerprint drift remain monitored."
                : "Provisional tune is active; this session counts only after verified restoration."
        };
    }

    public static AutoOcProfileValidationV1 Deactivate(
        AutoOcProfileValidationV1 record,
        string serviceInstanceId,
        DateTimeOffset now,
        bool countSuccessfulColdBoot)
    {
        if (record.ActiveSessionStartedAt is not DateTimeOffset started
            || !string.Equals(record.ActiveServiceInstanceId, serviceInstanceId, StringComparison.Ordinal))
        {
            return record;
        }

        // Active use is NOT credited here. It accrues only through
        // RecordActiveUseSample, which requires observed load — closing a
        // session must never convert idle wall-clock into evidence.
        _ = started;
        AutoOcProfileValidationV1 updated = record with
        {
            UpdatedAt = now,
            SuccessfulColdBoots = record.SuccessfulColdBoots + (countSuccessfulColdBoot ? 1 : 0),
            ActiveSessionStartedAt = null,
            ActiveServiceInstanceId = null
        };
        return EvaluatePromotion(updated, now);
    }

    /// <summary>
    /// Credits one telemetry interval toward active use. The interval counts
    /// only when a session is open on this service instance and the tuned GPU
    /// was actually loaded (<see cref="QualifyingUtilisationPercent"/>), and is
    /// clamped to <see cref="MaximumSampleInterval"/> so a sleep/resume gap
    /// cannot inject unobserved hours. A promoted record is re-evaluated so the
    /// tenth qualified hour takes effect without waiting for shutdown.
    /// </summary>
    public static AutoOcProfileValidationV1 RecordActiveUseSample(
        AutoOcProfileValidationV1 record,
        string serviceInstanceId,
        TimeSpan sinceLastSample,
        double? gpuUtilisationPercent,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ActiveSessionStartedAt is null
            || !string.Equals(record.ActiveServiceInstanceId, serviceInstanceId, StringComparison.Ordinal)
            || IsTerminal(record.State))
        {
            return record;
        }

        // Unknown load is not qualifying load: absent telemetry never accrues.
        if (gpuUtilisationPercent is not double utilisation || utilisation < QualifyingUtilisationPercent)
        {
            return record;
        }

        TimeSpan credited = sinceLastSample > MaximumSampleInterval ? MaximumSampleInterval : sinceLastSample;
        if (credited <= TimeSpan.Zero)
        {
            return record;
        }

        return EvaluatePromotion(
            record with { UpdatedAt = now, ActiveUse = record.ActiveUse + credited },
            now);
    }

    public static AutoOcProfileValidationV1 InvalidateUncleanSession(
        AutoOcProfileValidationV1 record,
        string currentServiceInstanceId,
        DateTimeOffset now)
    {
        if (record.ActiveSessionStartedAt is null
            || string.Equals(record.ActiveServiceInstanceId, currentServiceInstanceId, StringComparison.Ordinal))
        {
            return record;
        }

        AutoOcStabilityEventV1 stabilityEvent = new(
            AutoOcStabilityEventKind.UncleanShutdown,
            now,
            "The service restarted while this tune had an open active-use session; defaults were required before reuse.");
        return record with
        {
            State = AutoOcValidationState.Invalidated,
            UpdatedAt = now,
            ActiveSessionStartedAt = null,
            ActiveServiceInstanceId = null,
            RelevantEvents = AppendEvent(record.RelevantEvents, stabilityEvent),
            Message = $"Invalidated: {stabilityEvent.Message}"
        };
    }

    public static AutoOcProfileValidationV1 InvalidateFingerprint(
        AutoOcProfileValidationV1 record,
        DateTimeOffset now) => Invalidate(
            record,
            new AutoOcStabilityEventV1(
                AutoOcStabilityEventKind.HardwareFingerprintChanged,
                now,
                "GPU identity, VBIOS, or display-driver evidence changed; the tune must be regenerated."),
            now);

    public static AutoOcProfileValidationV1 RecordStabilityEvents(
        AutoOcProfileValidationV1 record,
        IReadOnlyList<AutoOcStabilityEventV1> events,
        DateTimeOffset now)
    {
        if (record.ActiveSessionStartedAt is null || events.Count == 0)
        {
            return record;
        }

        AutoOcProfileValidationV1 updated = record;
        foreach (AutoOcStabilityEventV1 stabilityEvent in events.OrderBy(item => item.ObservedAt))
        {
            if (stabilityEvent.ObservedAt >= record.ActiveSessionStartedAt.Value)
            {
                updated = Invalidate(updated, stabilityEvent, now);
            }
        }
        return updated;
    }

    public static AutoOcProfileValidationV1 MarkRecoveryRequired(
        AutoOcProfileValidationV1 record,
        DateTimeOffset now,
        string message) => record with
    {
        State = AutoOcValidationState.RecoveryRequired,
        UpdatedAt = now,
        ActiveSessionStartedAt = null,
        ActiveServiceInstanceId = null,
        RelevantEvents = AppendEvent(record.RelevantEvents, new AutoOcStabilityEventV1(
            AutoOcStabilityEventKind.RestorationFailed,
            now,
            message)),
        Message = $"Recovery Required: {message}"
    };

    public static bool FingerprintMatches(HardwareFingerprintV1 expected, HardwareFingerprintV1 current) =>
        expected.SchemaVersion == HardwareFingerprintV1.CurrentSchemaVersion
        && current.SchemaVersion == HardwareFingerprintV1.CurrentSchemaVersion
        && string.Equals(expected.DeviceId, current.DeviceId, StringComparison.Ordinal)
        && string.Equals(expected.FingerprintSha256, current.FingerprintSha256, StringComparison.OrdinalIgnoreCase);

    private static AutoOcProfileValidationV1 Invalidate(
        AutoOcProfileValidationV1 record,
        AutoOcStabilityEventV1 stabilityEvent,
        DateTimeOffset now)
    {
        // Active use already reflects every qualified sample credited before
        // this event; no additional wall-clock is added on the way out.
        return record with
        {
            State = AutoOcValidationState.Invalidated,
            UpdatedAt = now,
            ActiveSessionStartedAt = null,
            ActiveServiceInstanceId = null,
            RelevantEvents = AppendEvent(record.RelevantEvents, stabilityEvent),
            Message = $"Invalidated: {stabilityEvent.Message}"
        };
    }

    /// <summary>
    /// States a record must never leave by accruing evidence. Rejected belongs
    /// here with Invalidated and RecoveryRequired: a tune the screening refused
    /// or the operator discarded must stay refused, and must not be resurrected
    /// into Provisional — let alone Validated — simply because time passed with
    /// it applied.
    /// </summary>
    public static bool IsTerminal(AutoOcValidationState state) =>
        state is AutoOcValidationState.Invalidated
            or AutoOcValidationState.RecoveryRequired
            or AutoOcValidationState.Rejected;

    private static AutoOcProfileValidationV1 EvaluatePromotion(
        AutoOcProfileValidationV1 record,
        DateTimeOffset now)
    {
        if (IsTerminal(record.State))
        {
            return record;
        }

        bool complete = record.ActiveUse >= RequiredActiveUse
            && now - record.CreatedAt >= RequiredCalendarAge
            && record.SuccessfulColdBoots >= RequiredSuccessfulColdBoots
            && record.RelevantEvents.Count == 0;
        return record with
        {
            State = complete ? AutoOcValidationState.Validated : AutoOcValidationState.Provisional,
            Message = complete
                ? "Validated: 10 active-use hours, seven calendar days, three successful cold-boot sessions, and zero relevant stability events are recorded."
                : $"Provisional: {record.ActiveUse.TotalHours:0.##}/10 active hours, {Math.Min(7, Math.Max(0, (now - record.CreatedAt).TotalDays)):0.##}/7 days, and {record.SuccessfulColdBoots}/3 successful cold-boot sessions."
        };
    }

    private static AutoOcStabilityEventV1[] AppendEvent(
        IReadOnlyList<AutoOcStabilityEventV1> existing,
        AutoOcStabilityEventV1 stabilityEvent) => existing
        .Append(stabilityEvent)
        .OrderByDescending(item => item.ObservedAt)
        .Take(MaximumRetainedEvents)
        .OrderBy(item => item.ObservedAt)
        .ToArray();
}
