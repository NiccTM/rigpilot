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
    private async Task PreviewTakeoverCoreAsync()
    {
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.PreviewTakeover,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        TakeoverPreviewResultV1 result = IpcJson.FromElement<TakeoverPreviewResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty takeover preview.");
        TakeoverPreview = result.Plan;
        UpdateStateRevision(response);
        await RefreshOwnershipAsync(_lifetime.Token);
        OwnershipStatus = result.ExecutorStatus.CanExecute
            ? $"Previewed {result.Plan.Processes.Count} exact process(es). Store consent for every target before execution."
            : $"Previewed {result.Plan.Processes.Count} exact process(es). {result.ExecutorStatus.Message}";
        ShowNotice(
            result.Plan.Processes.Count == 0
                ? "No exact competing process could be previewed. Nothing was changed."
                : "Takeover preview created. No process, startup entry, or hardware control was changed.",
            result.Plan.Processes.Count == 0 ? "Info" : "Success");
    }

    private async Task GrantTakeoverConsentCoreAsync()
    {
        TakeoverProcessIdentity target = SelectedTakeoverTarget
            ?? throw new InvalidOperationException("Select an exact takeover target first.");
        OwnershipConsentV1 consent = TakeoverConsentValidator.Create(
            target,
            TakeoverAllowForceTermination,
            TakeoverDisableStartup,
            DateTimeOffset.UtcNow);
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.GrantOwnershipConsent,
            new GrantOwnershipConsentRequest(consent),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshOwnershipAsync(_lifetime.Token);
        ShowNotice($"Stored exact consent for '{target.DisplayName}'. It will be invalidated if the file identity changes.", "Success");
    }

    private async Task ExecuteTakeoverCoreAsync()
    {
        TakeoverPlanV1 plan = TakeoverPreview
            ?? throw new InvalidOperationException("Preview the exact competing processes before execution.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.ExecuteTakeover,
            new ExecuteTakeoverRequest(plan.Id, TakeoverExactProcessesConfirmed),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        if (!response.Success)
        {
            await RefreshOwnershipAsync(CancellationToken.None);
            EnsureSuccess(response);
        }
        UpdateStateRevision(response);
        await RefreshAsync(full: true, userInitiated: false);
        ShowNotice("Exact competing controls were reset and ownership was acquired.", "Success");
    }

    private async Task ReleaseOwnershipCoreAsync()
    {
        TakeoverTransactionV1 transaction = ActiveOwnershipTransaction
            ?? throw new InvalidOperationException("There is no active ownership lease to release.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.ReleaseOwnership,
            new ReleaseOwnershipRequest(transaction.Id),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        if (!response.Success)
        {
            await RefreshOwnershipAsync(CancellationToken.None);
            EnsureSuccess(response);
        }
        UpdateStateRevision(response);
        await RefreshAsync(full: true, userInitiated: false);
        ShowNotice("RigPilot returned affected controls to firmware/default mode and restored backed-up startup entries.", "Success");
    }

    private async Task PreviewAfterburnerImportCoreAsync()
    {
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.PreviewAfterburnerImport,
                new AfterburnerImportRequest(AfterburnerImportPath, AfterburnerImportSection)),
            _lifetime.Token);
        EnsureSuccess(response);
        AfterburnerImportPreview = IpcJson.FromElement<ProfileImportPreviewV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty Afterburner import preview.");
        AfterburnerImportStatus = AfterburnerImportPreview.Warnings.Count == 0
            ? "Preview complete. Review every mapped and manual-only action before saving."
            : $"Preview complete with {AfterburnerImportPreview.Warnings.Count} warning(s). Unsupported settings are not saved.";
        ShowNotice("Afterburner profile was parsed without applying hardware changes.", "Success");
    }

    private async Task SaveAfterburnerImportCoreAsync()
    {
        ProfileV2 profile = AfterburnerImportPreview?.Profile
            ?? throw new InvalidOperationException("Preview a valid Afterburner profile before saving it.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveProfileV2,
            profile,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        await RefreshAsync(full: true, userInitiated: false);
        AfterburnerImportStatus = $"Saved '{profile.Name}'. Manual-only actions remain blocked from boot and automation.";
        ShowNotice($"Saved imported profile '{profile.Name}'. It was not applied.", "Success");
    }

    private async Task PreviewFanControlImportCoreAsync()
    {
        FanControlImportRequest import = new(
            FanControlImportPath,
            ParseImportMappings(FanControlSensorMappings, "sensor"),
            ParseImportMappings(FanControlControlMappings, "control"));
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.PreviewFanControlImport, import),
            _lifetime.Token);
        EnsureSuccess(response);
        FanControlImportPreview = IpcJson.FromElement<CoolingImportPreviewV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty Fan Control import preview.");
        FanControlImportStatus = FanControlImportPreview.Warnings.Count == 0
            ? "Preview complete. Verify the graph source/output mapping before saving."
            : $"Preview complete with {FanControlImportPreview.Warnings.Count} warning(s). Unmapped outputs remain unavailable.";
        ShowNotice("Fan Control configuration was parsed without applying a cooling curve.", "Success");
    }

    private async Task SaveFanControlImportCoreAsync()
    {
        CoolingGraphV1 graph = FanControlImportPreview?.Graph
            ?? throw new InvalidOperationException("Preview a valid Fan Control graph before saving it.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveCoolingGraph,
            graph,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        UpdateStateRevision(response);
        FanControlImportStatus = $"Saved '{graph.Name}'. It is not applied until a profile explicitly selects it.";
        ShowNotice($"Saved imported cooling graph '{graph.Name}'.", "Success");
    }
}
