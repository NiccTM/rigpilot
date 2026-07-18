using PCHelper.Contracts;

namespace PCHelper.Core;

public static class ProfileDryRunPlanner
{
    public static ProfileDryRunResultV1 Build(
        PreviewProfileV2Request request,
        IReadOnlyDictionary<string, CapabilityDescriptorV2> capabilities,
        IReadOnlyList<ProfileDryRunActionV1>? linkedActions = null,
        IReadOnlyList<string>? linkedConflicts = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);

        ProfileV2 profile = request.Profile;
        ProfileValidationResult validation = ProfileV2Validator.Validate(
            profile,
            capabilities,
            request.Source,
            request.ConfirmManualVoltage);
        List<ProfileDryRunActionV1> actions = [];
        List<string> conflicts = linkedConflicts?.ToList() ?? [];
        List<string> omitted = validation.SkippedOptionalActions.ToList();
        HashSet<string> atomicDomains = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProfileAction action in profile.HardwareActions.OrderBy(item => item.Order))
        {
            ProfileDryRunActionV1 planned = PlanHardwareAction(profile, action, request, capabilities);
            actions.Add(planned);
            if (planned.State == ProfileDryRunActionState.Conflict)
            {
                conflicts.Add(planned.Message);
            }
            if (planned.State == ProfileDryRunActionState.OmittedOptional
                && !omitted.Contains(planned.Message, StringComparer.Ordinal))
            {
                omitted.Add(planned.Message);
            }
            if (planned.State == ProfileDryRunActionState.Ready
                && capabilities.TryGetValue(action.CapabilityId, out CapabilityDescriptorV2? descriptor))
            {
                atomicDomains.Add(descriptor.Capability.Domain.ToString());
            }
        }

        if (linkedActions is not null)
        {
            actions.AddRange(linkedActions);
            foreach (ProfileDryRunActionV1 linked in linkedActions)
            {
                if (linked.State == ProfileDryRunActionState.Ready)
                {
                    atomicDomains.Add(linked.Domain);
                }
                else if (linked.State == ProfileDryRunActionState.OmittedOptional)
                {
                    omitted.Add(linked.Message);
                }
            }
        }

        AddProfileConfirmations(profile, request, capabilities, actions, conflicts);

        string[] independentDomains = actions
            .Where(item => item.State == ProfileDryRunActionState.IndependentCompanion)
            .Select(item => item.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool actionBlocked = actions.Any(item =>
            item.State is ProfileDryRunActionState.Blocked
                or ProfileDryRunActionState.Conflict
                or ProfileDryRunActionState.RequiresConfirmation);
        bool canApply = validation.Valid && !actionBlocked && conflicts.Count == 0;
        string rollback = independentDomains.Length == 0
            ? "The service journals prepared prior values, applies hardware and cooling as one transaction, verifies read-back, and restores prior values in reverse order on failure. Any unknown result enters Recovery Required."
            : "The service journals hardware and cooling prior values, verifies read-back, and restores them in reverse order on failure. User-session lighting and OSD run after that commit and report their outcomes independently; they are not part of the privileged rollback transaction.";

        return new ProfileDryRunResultV1(
            ProfileDryRunResultV1.CurrentSchemaVersion,
            profile.Id,
            canApply,
            profile.HardwareActions
                .Where(item => item.Required)
                .Select(item => item.CapabilityId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            actions,
            conflicts.Distinct(StringComparer.Ordinal).ToArray(),
            omitted.Distinct(StringComparer.Ordinal).ToArray(),
            atomicDomains.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            independentDomains,
            rollback,
            DateTimeOffset.UtcNow);
    }

    private static ProfileDryRunActionV1 PlanHardwareAction(
        ProfileV2 profile,
        ProfileAction action,
        PreviewProfileV2Request request,
        IReadOnlyDictionary<string, CapabilityDescriptorV2> capabilities)
    {
        string unavailableState = action.Required ? "Blocked" : "Omitted";
        if (!capabilities.TryGetValue(action.CapabilityId, out CapabilityDescriptorV2? descriptor))
        {
            return Unavailable(action, $"{unavailableState}: capability is not present on this system.");
        }

        CapabilityDescriptor capability = descriptor.Capability;
        string domain = capability.Domain.ToString();
        string description = $"{capability.Name} = {DescribeValue(action.Value, capability.Unit)}";
        if (!string.Equals(action.AdapterId, capability.AdapterId, StringComparison.Ordinal))
        {
            return new(action.Id, domain, description, ProfileDryRunActionState.Blocked, action.Required,
                action.CapabilityId, "The profile names an adapter that does not own this exact capability.");
        }
        if (capability.State is not (CapabilityAccessState.Verified or CapabilityAccessState.Experimental))
        {
            return Unavailable(action, $"{unavailableState}: {capability.Reason}", domain, description);
        }
        if (descriptor.OwnershipState is not (OwnershipState.Available or OwnershipState.OwnedByPcHelper))
        {
            ProfileDryRunActionState state = action.Required
                ? ProfileDryRunActionState.Conflict
                : ProfileDryRunActionState.OmittedOptional;
            return new(action.Id, domain, description, state, action.Required, action.CapabilityId,
                $"Capability ownership is {descriptor.OwnershipState}; resolve the competing owner first.");
        }

        bool manualOnly = profile.ManualOnlyActionIds.Contains(action.Id, StringComparer.Ordinal)
            || descriptor.BootPolicy is BootApplyPolicy.ManualOnly or BootApplyPolicy.Never;
        if (manualOnly && request.Source is not ProfileActivationSource.Manual)
        {
            return new(action.Id, domain, description, ProfileDryRunActionState.Blocked, action.Required,
                action.CapabilityId, "This action is Manual Only and cannot be activated by a game or automation rule.");
        }
        if (descriptor.Hazard == HazardClass.Voltage && !request.ConfirmManualVoltage)
        {
            return new(action.Id, domain, description, ProfileDryRunActionState.RequiresConfirmation, action.Required,
                action.CapabilityId, "A manual per-session voltage acknowledgement is required. RigPilot never increases voltage automatically.");
        }
        if (!ValueIsWithinCapability(action.Value, capability))
        {
            return new(action.Id, domain, description, ProfileDryRunActionState.Blocked, action.Required,
                action.CapabilityId, "The requested value does not match this capability's type or bounds.");
        }

        return new ProfileDryRunActionV1(action.Id, domain, description, ProfileDryRunActionState.Ready, action.Required,
            action.CapabilityId, descriptor.SupportsReadBack
                ? "Ready; the service must apply and verify this value by read-back."
                : "Blocked: this capability does not declare read-back support.") with
        {
            State = descriptor.SupportsReadBack ? ProfileDryRunActionState.Ready : ProfileDryRunActionState.Blocked
        };
    }

    private static void AddProfileConfirmations(
        ProfileV2 profile,
        PreviewProfileV2Request request,
        IReadOnlyDictionary<string, CapabilityDescriptorV2> capabilities,
        List<ProfileDryRunActionV1> actions,
        List<string> conflicts)
    {
        if (!profile.IsExperimental)
        {
            return;
        }

        if (!request.ConfirmExperimental)
        {
            actions.Add(new(
                "confirm-experimental",
                "Safety",
                "Experimental profile acknowledgement",
                ProfileDryRunActionState.RequiresConfirmation,
                true,
                null,
                "Explicit acknowledgement of Experimental controls is required."));
        }

        string[] missingDevices = profile.HardwareActions
            .Select(item => capabilities.TryGetValue(item.CapabilityId, out CapabilityDescriptorV2? descriptor)
                ? descriptor.Capability.DeviceId
                : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(item => !request.ConfirmedDeviceIds.Contains(item, StringComparer.Ordinal))
            .ToArray();
        if (missingDevices.Length > 0)
        {
            string message = $"Exact-device confirmation is missing for: {string.Join(", ", missingDevices)}.";
            actions.Add(new(
                "confirm-devices",
                "Safety",
                "Exact-device acknowledgement",
                ProfileDryRunActionState.RequiresConfirmation,
                true,
                null,
                message));
            conflicts.Add(message);
        }
    }

    private static ProfileDryRunActionV1 Unavailable(
        ProfileAction action,
        string message,
        string domain = "Hardware",
        string? description = null) => new(
            action.Id,
            domain,
            description ?? action.CapabilityId,
            action.Required ? ProfileDryRunActionState.Blocked : ProfileDryRunActionState.OmittedOptional,
            action.Required,
            action.CapabilityId,
            message);

    private static bool ValueIsWithinCapability(ControlValue value, CapabilityDescriptor capability)
    {
        if (value.Kind != capability.ValueKind)
        {
            return false;
        }
        if (capability.Range is not NumericRange range)
        {
            return true;
        }
        return value.Numeric is double numeric
            && double.IsFinite(numeric)
            && numeric >= range.Minimum
            && numeric <= range.Maximum;
    }

    private static string DescribeValue(ControlValue value, string? unit) => value.Kind switch
    {
        ControlValueKind.Numeric => $"{value.Numeric:0.##} {unit}".TrimEnd(),
        ControlValueKind.Boolean => value.Boolean is true ? "On" : "Off",
        ControlValueKind.Colour => value.Text ?? "Colour",
        ControlValueKind.Text => value.Text ?? string.Empty,
        ControlValueKind.Curve => $"{value.Curve?.Count ?? 0} points",
        _ => value.Kind.ToString()
    };
}
