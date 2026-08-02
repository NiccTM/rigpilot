using System.Windows.Input;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.App;

public sealed partial class MainViewModel
{
    // --- Save the GPU overclock and reapply it at every service start ---------
    //
    // GPU overclocks are session-only by default: the service comes up at stock
    // so a bad offset can never make a machine unbootable. This opt-in overrides
    // that, and it is the riskiest switch in the suite, so it does not get its
    // own weaker ceremony. Ticking it is the explicit restart-risk acceptance
    // GpuOcStartupPolicy demands; the service then re-applies and read-back
    // verifies the offsets before writing anything, so an overclock that cannot
    // be proven right now can never be saved for an unattended boot. A boot that
    // does not survive the reapply is reverted by the sentinel journal rather
    // than retried.

    private bool _gpuOcStartupSaved;
    private string _gpuOcStartupStatus =
        "Overclocks are applied for this session only. Tick to save the current clock offsets and power limit and have the service reapply and verify them at every start.";

    private AsyncCommand? _toggleGpuOcStartupCommand;

    /// <summary>Whether the service currently holds a saved startup overclock.</summary>
    public bool GpuOcStartupSaved
    {
        get => _gpuOcStartupSaved;
        private set => Set(ref _gpuOcStartupSaved, value);
    }

    /// <summary>The service's own account of the saved overclock. Never inferred locally.</summary>
    public string GpuOcStartupStatus
    {
        get => _gpuOcStartupStatus;
        private set => Set(ref _gpuOcStartupStatus, value);
    }

    /// <summary>
    /// Bound with the checkbox in <c>Mode=OneWay</c> plus this command, matching the master
    /// hardware-control switch: the displayed state stays the service's answer rather than
    /// whatever the click optimistically set, so a refused save cannot leave a ticked box
    /// claiming an overclock is persisted when it is not.
    /// </summary>
    public ICommand ToggleGpuOcStartupCommand => _toggleGpuOcStartupCommand ??= new AsyncCommand(
        _ => ToggleGpuOcStartupSafelyAsync(),
        _ => CanUseServiceWrites && !IsHardwareControlChanging,
        ReportError,
        _ => ShowNotice(GetServiceWriteBlockReason(), "Warning"));

    private async Task ToggleGpuOcStartupSafelyAsync()
    {
        try
        {
            await ToggleGpuOcStartupAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowNotice($"Saving the startup overclock failed: {exception.Message}", "Warning");
        }
        finally
        {
            // Whatever happened, the box shows what the service actually holds.
            OnPropertyChanged(nameof(GpuOcStartupSaved));
        }
    }

    private async Task ToggleGpuOcStartupAsync()
    {
        if (GpuOcStartupSaved)
        {
            await SetGpuOcStartupAsync(new SetGpuOcStartupPersistenceRequest(
                Enable: false,
                DeviceId: string.Empty,
                Outputs: [],
                ConfirmedDeviceIds: [],
                ConfirmRestartRisk: false));
            return;
        }

        // Enabling re-applies the outputs, so it needs the same armed state as any
        // other hardware write.
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header before saving an overclock for startup.", "Warning");
            return;
        }

        GpuControlSlider[] persistable = GpuControlSliders
            .Where(slider => GpuOcStartupPolicy.IsPersistableCapability(slider.CapabilityId))
            .ToArray();
        if (persistable.Length == 0)
        {
            ShowNotice(
                "There is no GPU clock offset or power-limit control available to save. Fan duty is not part of a saved overclock.",
                "Warning");
            return;
        }

        string[] deviceIds = persistable
            .Select(slider => slider.DeviceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (deviceIds.Length != 1)
        {
            // The saved profile names one device and the sentinel reverts that one
            // device. Spanning two GPUs would save a profile the recovery path
            // cannot honour, so it is refused rather than guessed at.
            ShowNotice(
                "The GPU controls span more than one device, so there is no single overclock to save.",
                "Warning");
            return;
        }

        if (!persistable.Any(slider =>
            GpuOcStartupSelection.IsDeliberateValue(Math.Round(slider.Value), slider.Default)))
        {
            ShowNotice(
                "The clock offsets and power limit are all at their default values, so there is no overclock to save. "
                    + "Set the offsets you want, apply them, then tick this.",
                "Warning");
            return;
        }

        GpuOcStartupOutputV1[] outputs = persistable
            .Select(slider => new GpuOcStartupOutputV1(slider.CapabilityId, Math.Round(slider.Value)))
            .ToArray();
        string summary = string.Join(
            ", ",
            persistable.Select(slider => $"{slider.Name} {Math.Round(slider.Value):0.##} {slider.Unit}".Trim()));

        await SetGpuOcStartupAsync(
            new SetGpuOcStartupPersistenceRequest(
                Enable: true,
                DeviceId: deviceIds[0],
                Outputs: outputs,
                // Ticking the box is the restart-risk acceptance, and the device is
                // confirmed exactly as an interactive apply confirms it.
                ConfirmedDeviceIds: [deviceIds[0]],
                ConfirmRestartRisk: true),
            summary);
    }

    private async Task SetGpuOcStartupAsync(SetGpuOcStartupPersistenceRequest payload, string? summary = null)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetGpuOcStartupPersistence,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);

        if (!response.Success)
        {
            // The service refuses for reasons the operator can act on — an overclock
            // that would not verify, a missing confirmation — so its wording is shown
            // rather than a generic failure.
            GpuOcStartupStatus = response.Error ?? "The service refused to save the overclock.";
            ShowNotice(GpuOcStartupStatus, "Warning");
            await RefreshGpuOcStartupAsync(_lifetime.Token);
            return;
        }

        UpdateStateRevision(response);
        GpuOcStartupPersistenceStatus? status =
            IpcJson.FromElement<GpuOcStartupPersistenceStatus>(response.Payload);
        ApplyGpuOcStartupStatus(status);
        ShowNotice(
            payload.Enable
                ? $"Overclock saved for startup and read-back verified: {summary}."
                : "Saved startup overclock cleared. The GPU comes up at stock from now on.",
            "Success");
        await RefreshAsync(full: true, userInitiated: false);
    }

    /// <summary>
    /// Syncs the displayed state from the service. A service one version behind has no
    /// such command, so a failure clears the flag rather than throwing: an unsupported
    /// service holds no saved overclock, and claiming otherwise would be worse than
    /// showing nothing.
    /// </summary>
    private async Task RefreshGpuOcStartupAsync(CancellationToken cancellationToken)
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.GetGpuOcStartupPersistence),
            cancellationToken);
        if (!response.Success)
        {
            GpuOcStartupSaved = false;
            return;
        }

        ApplyGpuOcStartupStatus(IpcJson.FromElement<GpuOcStartupPersistenceStatus>(response.Payload));
    }

    private void ApplyGpuOcStartupStatus(GpuOcStartupPersistenceStatus? status)
    {
        if (status is null)
        {
            GpuOcStartupSaved = false;
            return;
        }

        GpuOcStartupSaved = status.Enabled;
        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            GpuOcStartupStatus = status.Message;
        }
    }
}
