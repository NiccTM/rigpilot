using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class AutoOcValidationPolicyTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProvisionalTuneIsManualOnlyAndFingerprintBound()
    {
        AutoOcProfileValidationV1 record = Validation();
        HardwareFingerprintV1 matching = Fingerprint("hash-a");
        HardwareFingerprintV1 changed = Fingerprint("hash-b");

        Assert.True(AutoOcValidationPolicy.CanActivate(record, matching, ProfileActivationSource.Manual, out _));
        Assert.False(AutoOcValidationPolicy.CanActivate(record, matching, ProfileActivationSource.Automation, out string automationReason));
        Assert.Contains("Validated", automationReason, StringComparison.Ordinal);
        Assert.False(AutoOcValidationPolicy.CanActivate(record, changed, ProfileActivationSource.Manual, out string driftReason));
        Assert.Contains("fingerprint", driftReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromotionRequiresAllThreeDurableEvidenceGates()
    {
        DateTimeOffset now = CreatedAt + TimeSpan.FromDays(8);
        AutoOcProfileValidationV1 record = Validation() with
        {
            ActiveUse = TimeSpan.FromHours(10),
            SuccessfulColdBoots = 2,
            SuccessfulManualApplications = 1,
            ActiveSessionStartedAt = now - TimeSpan.FromHours(1),
            ActiveServiceInstanceId = "boot-3"
        };

        AutoOcProfileValidationV1 promoted = AutoOcValidationPolicy.Deactivate(
            record,
            "boot-3",
            now,
            countSuccessfulColdBoot: true);

        Assert.Equal(TimeSpan.FromHours(10), promoted.ActiveUse);
        Assert.Equal(3, promoted.SuccessfulColdBoots);
        Assert.Equal(AutoOcValidationState.Validated, promoted.State);
        Assert.True(AutoOcValidationPolicy.CanActivate(promoted, Fingerprint("hash-a"), ProfileActivationSource.Automation, out _));
    }

    [Fact]
    public void StabilityEventInvalidatesAndStopsActiveUseAtEvent()
    {
        DateTimeOffset started = CreatedAt + TimeSpan.FromHours(1);
        DateTimeOffset signalAt = started + TimeSpan.FromMinutes(30);
        AutoOcProfileValidationV1 record = AutoOcValidationPolicy.Activate(
            Validation(),
            "boot-1",
            ProfileActivationSource.Manual,
            started);

        // 30s of qualified load was credited before the event.
        record = AutoOcValidationPolicy.RecordActiveUseSample(
            record, "boot-1", TimeSpan.FromSeconds(30), gpuUtilisationPercent: 80, started.AddSeconds(30));

        AutoOcProfileValidationV1 invalidated = AutoOcValidationPolicy.RecordStabilityEvents(
            record,
            [new AutoOcStabilityEventV1(AutoOcStabilityEventKind.Whea, signalAt, "WHEA observed")],
            signalAt + TimeSpan.FromMinutes(5));

        Assert.Equal(AutoOcValidationState.Invalidated, invalidated.State);
        // Only the credited qualified time survives — the intervening idle
        // wall-clock is not converted into evidence on the way out.
        Assert.Equal(TimeSpan.FromSeconds(30), invalidated.ActiveUse);
        Assert.Null(invalidated.ActiveSessionStartedAt);
        Assert.Equal(AutoOcStabilityEventKind.Whea, Assert.Single(invalidated.RelevantEvents).Kind);
    }

    [Fact]
    public void IdleTimeIsNeverEvidenceNoMatterHowLongTheTuneIsApplied()
    {
        AutoOcProfileValidationV1 record = AutoOcValidationPolicy.Activate(
            Validation(), "boot-1", ProfileActivationSource.Manual, CreatedAt);

        // Twelve hours of desktop idle, sampled every 30 seconds.
        for (int sample = 0; sample < 1440; sample++)
        {
            record = AutoOcValidationPolicy.RecordActiveUseSample(
                record, "boot-1", TimeSpan.FromSeconds(30), gpuUtilisationPercent: 2,
                CreatedAt.AddSeconds(30 * (sample + 1)));
        }

        AutoOcProfileValidationV1 closed = AutoOcValidationPolicy.Deactivate(
            record, "boot-1", CreatedAt + TimeSpan.FromDays(8), countSuccessfulColdBoot: true);

        Assert.Equal(TimeSpan.Zero, closed.ActiveUse);
        Assert.Equal(AutoOcValidationState.Provisional, closed.State);
        Assert.False(AutoOcValidationPolicy.CanActivate(
            closed, Fingerprint("hash-a"), ProfileActivationSource.Automation, out _));
    }

    [Fact]
    public void OnlyLoadedIntervalsAccrueAndSleepGapsCannotInjectHours()
    {
        AutoOcProfileValidationV1 record = AutoOcValidationPolicy.Activate(
            Validation(), "boot-1", ProfileActivationSource.Manual, CreatedAt);

        // A loaded 30s interval counts in full.
        record = AutoOcValidationPolicy.RecordActiveUseSample(
            record, "boot-1", TimeSpan.FromSeconds(30), 90, CreatedAt.AddSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), record.ActiveUse);

        // An 8-hour sleep/resume gap is clamped to one maximum interval.
        record = AutoOcValidationPolicy.RecordActiveUseSample(
            record, "boot-1", TimeSpan.FromHours(8), 90, CreatedAt.AddHours(8));
        Assert.Equal(TimeSpan.FromSeconds(30) + AutoOcValidationPolicy.MaximumSampleInterval, record.ActiveUse);

        // Absent telemetry is not qualifying load.
        AutoOcProfileValidationV1 unknown = AutoOcValidationPolicy.RecordActiveUseSample(
            record, "boot-1", TimeSpan.FromSeconds(30), null, CreatedAt.AddHours(8).AddSeconds(30));
        Assert.Equal(record.ActiveUse, unknown.ActiveUse);

        // A foreign service instance cannot credit this session.
        AutoOcProfileValidationV1 foreign = AutoOcValidationPolicy.RecordActiveUseSample(
            record, "other-boot", TimeSpan.FromSeconds(30), 90, CreatedAt.AddHours(8).AddSeconds(30));
        Assert.Equal(record.ActiveUse, foreign.ActiveUse);
    }

    [Fact]
    public void OpenSessionFromPreviousServiceInstanceIsInvalidatedWithoutCountingDowntime()
    {
        AutoOcProfileValidationV1 record = Validation() with
        {
            ActiveSessionStartedAt = CreatedAt + TimeSpan.FromHours(1),
            ActiveServiceInstanceId = "old-boot"
        };

        AutoOcProfileValidationV1 invalidated = AutoOcValidationPolicy.InvalidateUncleanSession(
            record,
            "new-boot",
            CreatedAt + TimeSpan.FromDays(1));

        Assert.Equal(AutoOcValidationState.Invalidated, invalidated.State);
        Assert.Equal(TimeSpan.Zero, invalidated.ActiveUse);
        Assert.Equal(AutoOcStabilityEventKind.UncleanShutdown, Assert.Single(invalidated.RelevantEvents).Kind);
    }

    [Fact]
    public void ACrashReportedAfterShutdownStillInvalidatesTheSessionThatCrashed()
    {
        // Session runs 12:00–13:00. WHEA fires at 12:59. Clean shutdown closes
        // the session at 13:00 and banks a successful cold boot. The probe only
        // surfaces the event at 13:00:05 — after the session is gone.
        DateTimeOffset started = CreatedAt;
        DateTimeOffset crashedAt = started + TimeSpan.FromMinutes(59);
        DateTimeOffset shutdownAt = started + TimeSpan.FromHours(1);

        AutoOcProfileValidationV1 record = AutoOcValidationPolicy.Activate(
            Validation(), "boot-1", ProfileActivationSource.Manual, started);
        record = AutoOcValidationPolicy.RecordActiveUseSample(
            record, "boot-1", TimeSpan.FromSeconds(30), 95, started.AddSeconds(30));

        AutoOcProfileValidationV1 closed = AutoOcValidationPolicy.Deactivate(
            record, "boot-1", shutdownAt, countSuccessfulColdBoot: true);
        Assert.Equal(1, closed.SuccessfulColdBoots);
        Assert.Null(closed.ActiveSessionStartedAt);

        AutoOcProfileValidationV1 late = AutoOcValidationPolicy.RecordStabilityEvents(
            closed,
            [new AutoOcStabilityEventV1(AutoOcStabilityEventKind.Whea, crashedAt, "WHEA observed")],
            shutdownAt.AddSeconds(5));

        Assert.Equal(AutoOcValidationState.Invalidated, late.State);
        Assert.Equal(AutoOcStabilityEventKind.Whea, Assert.Single(late.RelevantEvents).Kind);
    }

    [Fact]
    public void EventsOutsideEverySessionWindowAreNotAttributedToThisTune()
    {
        DateTimeOffset started = CreatedAt;
        DateTimeOffset shutdownAt = started + TimeSpan.FromHours(1);

        AutoOcProfileValidationV1 closed = AutoOcValidationPolicy.Deactivate(
            AutoOcValidationPolicy.Activate(Validation(), "boot-1", ProfileActivationSource.Manual, started),
            "boot-1",
            shutdownAt,
            countSuccessfulColdBoot: true);

        // Before the tune ever ran, and well after it stopped: someone else's
        // problem, not evidence against this tune.
        AutoOcProfileValidationV1 unrelated = AutoOcValidationPolicy.RecordStabilityEvents(
            closed,
            [
                new AutoOcStabilityEventV1(AutoOcStabilityEventKind.Whea, started - TimeSpan.FromHours(2), "before"),
                new AutoOcStabilityEventV1(AutoOcStabilityEventKind.DisplayDriverReset, shutdownAt + TimeSpan.FromHours(3), "after")
            ],
            shutdownAt + TimeSpan.FromHours(4));

        Assert.NotEqual(AutoOcValidationState.Invalidated, unrelated.State);
        Assert.Empty(unrelated.RelevantEvents);
    }

    [Theory]
    [InlineData(AutoOcValidationState.Rejected)]
    [InlineData(AutoOcValidationState.Invalidated)]
    [InlineData(AutoOcValidationState.RecoveryRequired)]
    public void ATerminalTuneIsNeverResurrectedByAccruingEvidence(AutoOcValidationState terminal)
    {
        // Full evidence already banked: only the terminal state stands between
        // this record and Validated.
        AutoOcProfileValidationV1 record = Validation() with
        {
            State = terminal,
            ActiveUse = TimeSpan.FromHours(20),
            SuccessfulColdBoots = 5,
            SuccessfulManualApplications = 2,
            ActiveSessionStartedAt = CreatedAt + TimeSpan.FromHours(1),
            ActiveServiceInstanceId = "boot-1"
        };
        DateTimeOffset now = CreatedAt + TimeSpan.FromDays(30);

        Assert.True(AutoOcValidationPolicy.IsTerminal(terminal));

        // Neither closing the session nor crediting loaded samples may promote it.
        AutoOcProfileValidationV1 closed = AutoOcValidationPolicy.Deactivate(
            record, "boot-1", now, countSuccessfulColdBoot: true);
        Assert.Equal(terminal, closed.State);

        AutoOcProfileValidationV1 sampled = AutoOcValidationPolicy.RecordActiveUseSample(
            record, "boot-1", TimeSpan.FromSeconds(30), gpuUtilisationPercent: 95, now);
        Assert.Equal(terminal, sampled.State);
        Assert.Equal(record.ActiveUse, sampled.ActiveUse);

        // And it cannot open a fresh evidence session either.
        AutoOcProfileValidationV1 reactivated = AutoOcValidationPolicy.Activate(
            record with { ActiveSessionStartedAt = null, ActiveServiceInstanceId = null },
            "boot-2",
            ProfileActivationSource.Manual,
            now);
        Assert.Equal(terminal, reactivated.State);
        Assert.Null(reactivated.ActiveSessionStartedAt);
    }

    private static AutoOcProfileValidationV1 Validation() => new(
        AutoOcProfileValidationV1.CurrentSchemaVersion,
        "profile.auto",
        "profile.auto",
        Fingerprint("hash-a"),
        AutoOcValidationState.Provisional,
        CreatedAt,
        CreatedAt,
        TimeSpan.Zero,
        0,
        0,
        null,
        null,
        [],
        "Provisional");

    private static HardwareFingerprintV1 Fingerprint(string hash) => new(
        HardwareFingerprintV1.CurrentSchemaVersion,
        "gpu-1",
        "GPU-UUID",
        "PCI\\VEN_10DE",
        "94.02.71.80.90",
        "610.62",
        hash);
}
