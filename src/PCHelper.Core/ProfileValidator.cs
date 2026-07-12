using PCHelper.Contracts;

namespace PCHelper.Core;

public static class ProfileValidator
{
    public static ProfileValidationResult Validate(
        ProfileV1 profile,
        IReadOnlyDictionary<string, CapabilityDescriptor> capabilities,
        bool confirmExperimental)
    {
        List<string> errors = [];
        List<string> warnings = [];
        List<string> skipped = [];

        if (profile.SchemaVersion != ProfileV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported profile schema {profile.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Profile ID and name are required.");
        }

        if (profile.SafetyLimits.AllowAutomaticVoltageIncrease)
        {
            errors.Add("Automatic voltage increases are not allowed.");
        }

        foreach (IGrouping<string, ProfileAction> duplicate in profile.Actions.GroupBy(action => action.Id).Where(group => group.Count() > 1))
        {
            errors.Add($"Duplicate action ID '{duplicate.Key}'.");
        }

        foreach (ProfileAction action in profile.Actions)
        {
            if (!capabilities.TryGetValue(action.CapabilityId, out CapabilityDescriptor? capability))
            {
                AddUnavailable(action, "Capability was not discovered.", errors, skipped);
                continue;
            }

            if (!string.Equals(action.AdapterId, capability.AdapterId, StringComparison.Ordinal))
            {
                errors.Add($"Action '{action.Id}' adapter does not own capability '{action.CapabilityId}'.");
                continue;
            }

            if (capability.State is CapabilityAccessState.ReadOnly or CapabilityAccessState.Unsupported or CapabilityAccessState.Faulted)
            {
                AddUnavailable(action, capability.Reason, errors, skipped);
                continue;
            }

            if (capability.State == CapabilityAccessState.Blocked)
            {
                AddUnavailable(action, $"Blocked by {capability.ConflictOwner ?? "another controller"}: {capability.Reason}", errors, skipped);
                continue;
            }

            if (capability.Domain == ControlDomain.Other)
            {
                errors.Add($"Action '{action.Id}' capability does not declare a safety ordering domain.");
                continue;
            }

            if (capability.State == CapabilityAccessState.Experimental && !confirmExperimental)
            {
                errors.Add($"Action '{action.Id}' requires explicit Experimental control confirmation.");
            }

            if (action.Value.Kind != capability.ValueKind)
            {
                errors.Add($"Action '{action.Id}' expects {capability.ValueKind}, not {action.Value.Kind}.");
                continue;
            }

            ValidateRange(action, capability, errors);

            if (capability.Risk is RiskLevel.Experimental or RiskLevel.Critical)
            {
                warnings.Add($"Action '{action.Id}' controls a {capability.Risk} capability.");
            }
        }

        return new ProfileValidationResult(errors.Count == 0, errors, warnings, skipped);
    }

    private static void AddUnavailable(ProfileAction action, string reason, List<string> errors, List<string> skipped)
    {
        string message = $"Action '{action.Id}' is unavailable: {reason}";
        if (action.Required)
        {
            errors.Add(message);
        }
        else
        {
            skipped.Add(message);
        }
    }

    private static void ValidateRange(ProfileAction action, CapabilityDescriptor capability, List<string> errors)
    {
        if (action.Value.Kind != ControlValueKind.Numeric || capability.Range is null)
        {
            return;
        }

        if (action.Value.Numeric is not double value || double.IsNaN(value) || double.IsInfinity(value))
        {
            errors.Add($"Action '{action.Id}' requires a finite numeric value.");
            return;
        }

        NumericRange range = capability.Range;
        if (value < range.Minimum || value > range.Maximum)
        {
            errors.Add($"Action '{action.Id}' value {value} is outside [{range.Minimum}, {range.Maximum}] {capability.Unit}.");
            return;
        }

        if (range.Step > 0)
        {
            double steps = (value - range.Minimum) / range.Step;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-6)
            {
                errors.Add($"Action '{action.Id}' value {value} does not align to step {range.Step}.");
            }
        }
    }
}
