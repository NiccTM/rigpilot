using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed record HardwareStartupRecoveryPlan(
    bool RequiresRecovery,
    IReadOnlyList<HardwareControlLeaseItemV1> Controls,
    string? ActiveProfileId,
    string? LastTransactionId);

/// <summary>
/// Pure lease-state planning used by service startup and shutdown. Keeping the
/// decision separate from hardware I/O makes the committed-profile migration
/// and fail-closed marker transitions directly testable.
/// </summary>
public static class HardwareControlRecoveryPlanner
{
    public static HardwareStartupRecoveryPlan BuildStartupPlan(
        HardwareControlLeaseV1? previousLease,
        ProfileTransaction? pending,
        ProfileTransaction? latestCommittedWithoutLease)
    {
        ProfileTransaction? legacyCommitted = previousLease is null
            ? latestCommittedWithoutLease
            : null;
        HardwareControlLeaseItemV1[] controls = (previousLease?.Controls ?? [])
            .Concat((pending?.PreparedActions ?? []).Select(ToLeaseItem))
            .Concat((legacyCommitted?.PreparedActions ?? []).Select(ToLeaseItem))
            .DistinctBy(item => (item.AdapterId, item.CapabilityId))
            .OrderBy(item => item.AdapterId, StringComparer.Ordinal)
            .ThenBy(item => item.CapabilityId, StringComparer.Ordinal)
            .ToArray();
        bool requiresRecovery = pending is not null
            || legacyCommitted is not null
            || previousLease is { CleanShutdown: false }
            || previousLease?.Controls.Count > 0;
        return new HardwareStartupRecoveryPlan(
            requiresRecovery,
            controls,
            previousLease?.ActiveProfileId ?? pending?.ProfileId ?? legacyCommitted?.ProfileId,
            pending?.Id ?? legacyCommitted?.Id ?? previousLease?.LastTransactionId);
    }

    public static HardwareControlLeaseV1 CreateRunningMarker(
        string serviceInstanceId,
        HardwareControlLeaseV1? previousLease,
        HardwareStartupRecoveryPlan plan,
        HardwareRecoveryResult recovery,
        DateTimeOffset now)
    {
        bool verified = recovery.AllDefaultsVerified;
        return new HardwareControlLeaseV1(
            HardwareControlLeaseV1.CurrentSchemaVersion,
            HardwareControlLeaseV1.DefaultId,
            serviceInstanceId,
            verified ? null : plan.ActiveProfileId,
            plan.LastTransactionId,
            verified ? [] : plan.Controls,
            previousLease?.AcquiredAt ?? now,
            now,
            CleanShutdown: false,
            DefaultsVerified: verified,
            verified ? HardwareControlLeaseState.Active : HardwareControlLeaseState.RecoveryRequired,
            verified
                ? plan.RequiresRecovery
                    ? "Unclean-start recovery completed with default-state read-back; the current service instance is now marked running."
                    : "The current service instance is marked running; clean shutdown has not occurred yet."
                : $"RecoveryRequired before IPC startup: {string.Join("; ", recovery.Errors)}");
    }

    public static HardwareControlLeaseV1 CreateShutdownMarker(
        string serviceInstanceId,
        HardwareControlLeaseV1? lease,
        HardwareRecoveryResult recovery,
        DateTimeOffset now)
    {
        bool verified = recovery.AllDefaultsVerified;
        return new HardwareControlLeaseV1(
            HardwareControlLeaseV1.CurrentSchemaVersion,
            HardwareControlLeaseV1.DefaultId,
            serviceInstanceId,
            verified ? null : lease?.ActiveProfileId,
            lease?.LastTransactionId,
            verified ? [] : lease?.Controls ?? [],
            lease?.AcquiredAt ?? now,
            now,
            CleanShutdown: verified,
            DefaultsVerified: verified,
            verified ? HardwareControlLeaseState.CleanShutdown : HardwareControlLeaseState.RecoveryRequired,
            verified
                ? "Clean shutdown completed after every leased capability was restored and read back at its default state."
                : $"RecoveryRequired during shutdown: {string.Join("; ", recovery.Errors)}");
    }

    private static HardwareControlLeaseItemV1 ToLeaseItem(PreparedAction action) =>
        new(action.Action.AdapterId, action.Action.CapabilityId);
}
