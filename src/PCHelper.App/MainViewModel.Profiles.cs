using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfPointCollection = System.Windows.Media.PointCollection;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public sealed partial class MainViewModel
{
    private async Task ApplyProfileAsync(ProfileV1 profile, bool manualSelection = true)
    {
        EnsureServiceWritesAvailable();
        if (!IsServiceOnline)
        {
            throw new InvalidOperationException(
                "Profiles require the RigPilot service. The current local-probe mode is read-only.");
        }

        BusyMessage = $"Applying {profile.Name}";
        IsBusy = true;
        try
        {
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.ApplyProfile,
                new ApplyProfileRequest(
                    profile,
                    ConfirmExperimental: profile.IsExperimental && AdvancedWritesAcknowledged,
                    ConfirmDevices: profile.IsExperimental && ProfileDeviceAcknowledged),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            ApplyProfileResult result = IpcJson.FromElement<ApplyProfileResult>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty profile result.");
            if (_status is not null)
            {
                _status = _status with
                {
                    StateRevision = response.StateRevision,
                    ActiveProfileId = result.ActiveProfileId
                };
            }

            _automationMachine.SetCurrentProfile(result.ActiveProfileId);
            ProfileActivationStatus = $"{profile.Name}: legacy hardware profile committed. Legacy profiles do not carry a linked lighting scene.";
            if (manualSelection)
            {
                _manualProfileId = profile.Id;
                await RefreshAsync(full: true, userInitiated: false);
                ShowNotice($"{profile.Name} is now the active profile. Manual override is active.", "Success");
            }
            else
            {
                UpdateDisplays();
            }

            NotifyAutomationProperties();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ApplyProfileCardAsync(ProfileCardDisplay card)
    {
        if (_suiteProfilesById.TryGetValue(card.Profile.Id, out ProfileV2? suiteProfile))
        {
            return ApplyProfileV2Async(suiteProfile);
        }
        return ApplyProfileAsync(card.Profile);
    }

    private async Task ApplyProfileV2Async(
        ProfileV2 profile,
        bool manualSelection = true,
        bool applyLinkedLighting = true,
        bool applyLinkedOsd = true)
    {
        EnsureServiceWritesAvailable();
        if (!IsServiceOnline)
        {
            throw new InvalidOperationException(
                "Profiles require the RigPilot service. The current local-probe mode is read-only.");
        }

        BusyMessage = $"Applying {profile.Name}";
        IsBusy = true;
        try
        {
            bool requiresManualAcknowledgement = profile.ManualOnlyActionIds.Count > 0;
            IReadOnlyList<string> confirmedDeviceIds = (profile.IsExperimental || requiresManualAcknowledgement)
                && ProfileDeviceAcknowledged
                ? GetProfileDeviceIds(profile)
                : [];
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.ApplyProfileV2,
                new ApplyProfileV2Request(
                    profile,
                    manualSelection ? ProfileActivationSource.Manual : ProfileActivationSource.Automation,
                    ConfirmExperimental: profile.IsExperimental && AdvancedWritesAcknowledged,
                    confirmedDeviceIds,
                    ConfirmManualVoltage: requiresManualAcknowledgement
                        && ManualVoltageAcknowledged
                        && ProfileDeviceAcknowledged),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            ApplyProfileResult result = IpcJson.FromElement<ApplyProfileResult>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty profile result.");
            UpdateAppliedProfileStatus(response, result);
            (string lightingMessage, bool lightingWarning) = applyLinkedLighting
                ? await ApplyLinkedLightingSceneAsync(profile)
                : ("Lighting activation was deferred to the enclosing bundle.", false);
            (string osdMessage, bool osdWarning) = applyLinkedOsd
                ? ApplyLinkedOsdLayout(profile)
                : ("OSD activation was deferred to the enclosing bundle.", false);
            bool companionWarning = lightingWarning || osdWarning;
            string companionMessage = $"{lightingMessage} {osdMessage}";
            ProfileActivationStatus = $"{profile.Name}: hardware transaction committed and verified. {companionMessage}";
            if (manualSelection)
            {
                _manualProfileId = profile.Id;
                await RefreshAsync(full: true, userInitiated: false);
                ShowNotice(
                    $"{profile.Name} is now the active profile. Manual override is active. {companionMessage}",
                    companionWarning ? "Warning" : "Success");
            }
            else
            {
                UpdateDisplays();
            }

            NotifyAutomationProperties();
            if (requiresManualAcknowledgement)
            {
                ManualVoltageAcknowledged = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string[] GetProfileDeviceIds(ProfileV2 profile)
    {
        IEnumerable<string> capabilityIds = profile.HardwareActions.Select(action => action.CapabilityId);
        if (profile.CoolingGraphId is string graphId
            && _coolingGraphsById.TryGetValue(graphId, out CoolingGraphV1? graph))
        {
            capabilityIds = capabilityIds.Concat(graph.Outputs.Select(output => output.CapabilityId));
        }

        return capabilityIds
        .Select(capabilityId => _snapshot?.Capabilities.FirstOrDefault(capability => capability.Id == capabilityId)?.DeviceId)
        .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
        .Cast<string>()
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    }

    private void UpdateAppliedProfileStatus(IpcResponse response, ApplyProfileResult result)
    {
        if (_status is not null)
        {
            _status = _status with
            {
                StateRevision = response.StateRevision,
                ActiveProfileId = result.ActiveProfileId
            };
        }
        _automationMachine.SetCurrentProfile(result.ActiveProfileId);
    }

    private async Task<(string Message, bool Warning)> ApplyLinkedLightingSceneAsync(ProfileV2 profile)
    {
        if (string.IsNullOrWhiteSpace(profile.LightingSceneId))
        {
            return ("No lighting scene is linked to this profile.", false);
        }

        LightingSceneV1? scene = LightingScenes.FirstOrDefault(item =>
            string.Equals(item.Id, profile.LightingSceneId, StringComparison.Ordinal));
        if (scene is null)
        {
            return ($"Linked lighting scene '{profile.LightingSceneId}' is unavailable in the signed-in user session; hardware remains committed.", true);
        }

        (string message, bool warning) = await ApplySavedLightingSceneAsync(scene, "Linked scene");
        return warning
            ? ($"{message} The verified hardware profile remains committed.", true)
            : (message, false);
    }

    private (string Message, bool Warning) ApplyLinkedOsdLayout(ProfileV2 profile)
    {
        if (string.IsNullOrWhiteSpace(profile.OsdLayoutId))
        {
            return ("No OSD layout is linked to this profile.", false);
        }

        OsdLayoutV1? layout = OsdLayouts.FirstOrDefault(item =>
            string.Equals(item.Id, profile.OsdLayoutId, StringComparison.Ordinal));
        if (layout is null)
        {
            return ($"Linked OSD layout '{profile.OsdLayoutId}' is unavailable in the signed-in user session; hardware remains committed.", true);
        }

        try
        {
            SelectedDesktopOsdLayout = layout;
            ApplyDesktopOsdLayout(layout);
            return ($"Linked OSD '{layout.Name}' is visible.", false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return ($"Linked OSD '{layout.Name}' was not shown after the verified hardware commit: {exception.Message}", true);
        }
    }

    private async Task ResetVerifiedControlsCoreAsync()
    {
        EnsureServiceWritesAvailable();
        if (!IsServiceOnline)
        {
            throw new InvalidOperationException("Reset requires the RigPilot service; local-probe mode cannot write hardware state.");
        }

        CapabilityDescriptor[] resettable = _snapshot?.Capabilities.Where(
            item => item.State == CapabilityAccessState.Verified && item.CanResetToDefault).ToArray() ?? [];
        if (resettable.Length == 0)
        {
            ShowNotice("No resettable Verified controls are currently available.", "Info");
            return;
        }

        BusyMessage = "Restoring verified controls";
        IsBusy = true;
        try
        {
            foreach (CapabilityDescriptor capability in resettable)
            {
                IpcRequest request = NamedPipeRequestClient.CreateRequest(
                    IpcCommand.ResetHardware,
                    capability.Id,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N"));
                IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
                EnsureSuccess(response);
            }

            await RefreshAsync(full: true, userInitiated: false);
            ShowNotice($"Restored {resettable.Length} Verified control{(resettable.Length == 1 ? string.Empty : "s")} to default.", "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
