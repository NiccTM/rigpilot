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

    private async Task ApplyProfileV2Async(ProfileV2 profile, bool manualSelection = true)
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
