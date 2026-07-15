using PCHelper.Contracts;

namespace PCHelper.Core;

public static class ProfileMigration
{
    public static ProfileV2 Upgrade(ProfileV1 profile) => new(
        ProfileV2.CurrentSchemaVersion,
        profile.Id,
        profile.Name,
        profile.Description,
        profile.Actions,
        profile.SafetyLimits,
        CoolingGraphId: null,
        LightingSceneId: null,
        OsdLayoutId: null,
        ManualOnlyActionIds: [],
        profile.AutomationReferences,
        profile.IsBuiltIn,
        profile.IsExperimental);

    public static ProfileV1 Downgrade(ProfileV2 profile) => new(
        ProfileV1.CurrentSchemaVersion,
        profile.Id,
        profile.Name,
        profile.Description,
        profile.HardwareActions,
        profile.SafetyLimits,
        profile.AutomationReferences,
        profile.IsBuiltIn,
        profile.IsExperimental);
}

public static class ProfileV2Validator
{
    public static ProfileValidationResult Validate(
        ProfileV2 profile,
        IReadOnlyDictionary<string, CapabilityDescriptorV2> capabilities,
        ProfileActivationSource source,
        bool confirmManualVoltage)
    {
        List<string> errors = [];
        List<string> warnings = [];
        List<string> skipped = [];

        if (profile.SchemaVersion != ProfileV2.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported profile schema {profile.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Profile ID and name are required.");
        }

        if (profile.HardwareActions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count()
            != profile.HardwareActions.Count)
        {
            errors.Add("Profile action IDs must be unique.");
        }

        HashSet<string> actionIds = profile.HardwareActions
            .Select(action => action.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string manualActionId in profile.ManualOnlyActionIds)
        {
            if (!actionIds.Contains(manualActionId))
            {
                errors.Add($"Manual-only action '{manualActionId}' does not exist.");
            }
        }

        foreach (ProfileAction action in profile.HardwareActions)
        {
            if (!capabilities.TryGetValue(action.CapabilityId, out CapabilityDescriptorV2? descriptor))
            {
                AddUnavailable(action, "Capability is unavailable.", errors, skipped);
                continue;
            }

            CapabilityDescriptor capability = descriptor.Capability;
            if (!string.Equals(action.AdapterId, capability.AdapterId, StringComparison.Ordinal))
            {
                errors.Add($"Action '{action.Id}' adapter does not own capability '{action.CapabilityId}'.");
                continue;
            }

            if (capability.State is not (CapabilityAccessState.Verified or CapabilityAccessState.Experimental))
            {
                AddUnavailable(action, capability.Reason, errors, skipped);
                continue;
            }

            if (descriptor.OwnershipState is not (OwnershipState.Available or OwnershipState.OwnedByPcHelper))
            {
                AddUnavailable(action, $"Capability ownership is {descriptor.OwnershipState}.", errors, skipped);
                continue;
            }

            bool manualOnly = profile.ManualOnlyActionIds.Contains(action.Id, StringComparer.Ordinal)
                || descriptor.BootPolicy is BootApplyPolicy.ManualOnly or BootApplyPolicy.Never;
            if (manualOnly && source is not ProfileActivationSource.Manual)
            {
                errors.Add($"Action '{action.Id}' is manual-only and cannot run from {source}.");
            }

            if (descriptor.Hazard == HazardClass.Voltage)
            {
                if (!profile.ManualOnlyActionIds.Contains(action.Id, StringComparer.Ordinal))
                {
                    errors.Add($"Voltage action '{action.Id}' must be marked manual-only.");
                }

                if (source is not ProfileActivationSource.Manual || !confirmManualVoltage)
                {
                    errors.Add($"Voltage action '{action.Id}' requires a manual per-session acknowledgement.");
                }

                if (profile.IsBuiltIn)
                {
                    errors.Add("Built-in profiles cannot contain voltage actions.");
                }
            }

            ValidateValue(action, capability, errors);
        }

        return new ProfileValidationResult(errors.Count == 0, errors, warnings, skipped);
    }

    private static void ValidateValue(ProfileAction action, CapabilityDescriptor capability, List<string> errors)
    {
        if (action.Value.Kind != capability.ValueKind)
        {
            errors.Add($"Action '{action.Id}' value kind does not match '{capability.Name}'.");
            return;
        }

        if (capability.Range is NumericRange range && action.Value.Numeric is double numeric)
        {
            if (!double.IsFinite(numeric) || numeric < range.Minimum || numeric > range.Maximum)
            {
                errors.Add($"Action '{action.Id}' value must be within {range.Minimum}-{range.Maximum} {capability.Unit}.".TrimEnd());
            }
        }
    }

    private static void AddUnavailable(
        ProfileAction action,
        string reason,
        List<string> errors,
        List<string> skipped)
    {
        string message = $"Action '{action.Id}': {reason}";
        if (action.Required)
        {
            errors.Add(message);
        }
        else
        {
            skipped.Add(message);
        }
    }
}

public static class CapabilityProfileFactory
{
    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid PowerSaver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformance = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static IReadOnlyList<ProfileV2> Create(HardwareSnapshot snapshot)
    {
        HashSet<Guid> schemes = ReadPowerSchemes(snapshot);
        return
        [
            CreatePowerProfile("quiet", "Quiet", "Lower-noise stock-safe policy.", Select(schemes, PowerSaver, Balanced)),
            CreatePowerProfile("efficiency", "Efficiency", "Lower-power stock-safe policy.", Select(schemes, PowerSaver, Balanced)),
            CreatePowerProfile("balanced", "Balanced", "Default stock-safe policy.", Select(schemes, Balanced)),
            CreatePowerProfile("performance", "Performance", "Higher responsiveness without above-stock tuning.", Select(schemes, HighPerformance, UltimatePerformance, Balanced))
        ];
    }

    private static ProfileV2 CreatePowerProfile(string id, string name, string description, Guid? scheme)
    {
        ProfileAction[] actions = scheme is Guid value
            ?
            [
                new ProfileAction(
                    $"{id}.power",
                    "windows.power",
                    "windows.power.active-scheme",
                    ControlValue.FromText(value.ToString("D")),
                    Required: true,
                    Order: 0)
            ]
            : [];
        return new ProfileV2(
            ProfileV2.CurrentSchemaVersion,
            id,
            name,
            description,
            actions,
            new SafetyLimits(),
            CoolingGraphId: null,
            LightingSceneId: null,
            OsdLayoutId: null,
            ManualOnlyActionIds: [],
            AutomationReferences: [],
            IsBuiltIn: true,
            IsExperimental: false);
    }

    private static HashSet<Guid> ReadPowerSchemes(HardwareSnapshot snapshot)
    {
        HardwareDevice? power = snapshot.Devices.FirstOrDefault(device => device.Id == "windows:power");
        if (power?.Properties.TryGetValue("availableSchemes", out string? value) != true
            || string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => Guid.TryParse(text, out Guid scheme) ? scheme : Guid.Empty)
            .Where(scheme => scheme != Guid.Empty)
            .ToHashSet();
    }

    private static Guid? Select(HashSet<Guid> available, params Guid[] preferred) =>
        preferred.FirstOrDefault(available.Contains) is Guid value && value != Guid.Empty ? value : null;
}
