using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Pure policy for the persisted physical-output registry.  It deliberately
/// biases toward a false-positive protection block: a copied or stale pump
/// assignment may keep a generic output read-only, but it must never make a
/// known pump eligible for a pulse or zero-RPM workflow.
/// </summary>
public static class CoolingOutputAssignmentPolicy
{
    public static bool IsSafetyCritical(CoolingOutputRole role) =>
        role is CoolingOutputRole.CpuFan or CoolingOutputRole.Pump;

    public static bool IsPump(CoolingOutputRole role) => role == CoolingOutputRole.Pump;

    public static bool Targets(CoolingOutputAssignmentV1 assignment, CapabilityDescriptor capability) =>
        string.Equals(assignment.CapabilityId, capability.Id, StringComparison.Ordinal);

    public static bool IsProtected(CoolingOutputAssignmentV1? assignment, CapabilityDescriptor capability) =>
        assignment is not null
        && Targets(assignment, capability)
        && IsSafetyCritical(assignment.Role);

    public static bool MatchesExactController(CoolingOutputAssignmentV1 assignment, CapabilityDescriptor capability) =>
        Targets(assignment, capability)
        && string.Equals(assignment.Id, capability.Id, StringComparison.Ordinal)
        && string.Equals(assignment.AdapterId, capability.AdapterId, StringComparison.Ordinal)
        && string.Equals(assignment.DeviceId, capability.DeviceId, StringComparison.Ordinal);

    public static bool RequiresExplicitProtectionRemoval(
        CoolingOutputAssignmentV1? existing,
        CoolingOutputRole requestedRole) =>
        existing is { IsSafetyCritical: true }
        && !IsSafetyCritical(requestedRole);

    public static IReadOnlyList<string> Validate(CoolingOutputAssignmentV1 assignment)
    {
        List<string> errors = [];
        if (assignment.SchemaVersion != CoolingOutputAssignmentV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported cooling-output assignment schema {assignment.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(assignment.Id)
            || string.IsNullOrWhiteSpace(assignment.CapabilityId)
            || string.IsNullOrWhiteSpace(assignment.AdapterId)
            || string.IsNullOrWhiteSpace(assignment.DeviceId))
        {
            errors.Add("Cooling-output assignment identity is required.");
        }
        if (string.IsNullOrWhiteSpace(assignment.HeaderName) || assignment.HeaderName.Trim().Length > 80)
        {
            errors.Add("A physical header label up to 80 characters is required.");
        }
        if (assignment.Notes?.Length > 1_000)
        {
            errors.Add("Cooling-output assignment notes cannot exceed 1000 characters.");
        }
        return errors;
    }
}
