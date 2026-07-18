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
    // --- Master hardware-control switch --------------------------------------
    // One persisted, per-machine owner acknowledgement that arms every
    // implemented GPU write family (fan duty, power limit, clock offsets)
    // whenever the service is connected, replacing the per-family per-session
    // arming ceremony. The service-side contract is unchanged: arming still
    // requires the Experimental confirmation and exact device id (this switch
    // supplies them), disarm restores vendor defaults, and the physical
    // fail-safes (no voltage path, pump/CPU-fan protection, stale-sensor
    // emergency cooling, rollback on failed verify) are untouched.

    private static readonly string ControlPreferencesPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RigPilot",
        "control-preferences.json");

    private readonly SemaphoreSlim _hardwareControlCommandGate = new(1, 1);
    private readonly AsyncCommand _toggleHardwareControlCommand;
    private bool _hardwareControlPreferenceRequested = ReadPersistedHardwareControlPreference();
    private bool _hardwareControlEnabled;
    private bool _hardwareControlArmedThisConnection;
    private bool _hardwareControlArmAttemptedThisConnection;
    private bool _isHardwareControlChanging;

    public bool HardwareControlEnabled => _hardwareControlEnabled;

    public bool IsHardwareControlChanging
    {
        get => _isHardwareControlChanging;
        private set
        {
            if (!Set(ref _isHardwareControlChanging, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HardwareControlBadge));
            _toggleHardwareControlCommand.RaiseCanExecuteChanged();
            RaiseHardwareControlCanExecuteChanged();
        }
    }

    public ICommand ToggleHardwareControlCommand => _toggleHardwareControlCommand;

    /// <summary>User-facing badge for the GPU controls card (On/Off, not True/False).</summary>
    public string HardwareControlBadge => IsHardwareControlChanging
        ? "Hardware control: Verifying"
        : HardwareControlEnabled ? "Hardware control: On" : "Hardware control: Off";

    private bool CanRunHardwareAction(bool requireIdle = false) =>
        CanUseServiceWrites
        && HardwareControlEnabled
        && !IsHardwareControlChanging
        && (!requireIdle || !HasActiveOperation);

    private void ShowHardwareActionBlocked(bool requireIdle = false)
    {
        string reason = !CanUseServiceWrites
            ? GetServiceWriteBlockReason()
            : !HardwareControlEnabled
                ? "Turn on Hardware control in the header first."
                : requireIdle && HasActiveOperation
                    ? "Another hardware operation is active. Abort it and wait for restoration before starting a new operation."
                    : "This hardware action is not available yet. Refresh the dashboard and review the device status.";
        ShowNotice(reason, "Warning");
    }

    private string GetServiceWriteBlockReason()
    {
        if (IsPortableMode)
        {
            return "This action requires the RigPilot service; portable mode is read-only.";
        }

        if (!IsServiceOnline)
        {
            return "The RigPilot service is offline. Start or reconnect the service, then refresh the dashboard.";
        }

        if (_status?.RecoveryRequired == true)
        {
            return _status.Message;
        }

        return string.IsNullOrWhiteSpace(ServiceCompatibilityMessage)
            ? "The connected RigPilot service cannot accept hardware writes. Update or restart the service, then refresh."
            : ServiceCompatibilityMessage;
    }

    private Task ToggleHardwareControlAsync(object? _) =>
        ApplyHardwareControlSafelyAsync(!HardwareControlEnabled, userInitiated: true);

    private async Task ApplyHardwareControlSafelyAsync(bool enable, bool userInitiated = false)
    {
        try
        {
            await ApplyHardwareControlAsync(enable, userInitiated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowNotice($"Hardware control change failed: {exception.Message}", "Warning");
            OnPropertyChanged(nameof(HardwareControlEnabled));
        }
    }

    private async Task<bool> ApplyHardwareControlAsync(bool enable, bool userInitiated)
    {
        await _hardwareControlCommandGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!IsServiceOnline || _snapshot is null || !CanUseServiceWrites)
            {
                if (userInitiated)
                {
                    ShowNotice(GetServiceWriteBlockReason(), "Warning");
                }
                OnPropertyChanged(nameof(HardwareControlEnabled));
                return false;
            }

            IsHardwareControlChanging = true;
            string[] confirmedDeviceIds = _snapshot.Capabilities
                .Where(capability => capability.Id.StartsWith("gpufan.", StringComparison.Ordinal)
                    || capability.Id.StartsWith("gpupower.", StringComparison.Ordinal)
                    || capability.Id.StartsWith("gpuclock.", StringComparison.Ordinal))
                .Select(capability => capability.DeviceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SetHardwareControlArmed,
                    new SetHardwareControlArmedRequest(enable, enable, confirmedDeviceIds)),
                _lifetime.Token);
            HardwareControlTransactionResult? result = IpcJson.FromElement<HardwareControlTransactionResult>(response.Payload);
            if (result is null)
            {
                throw new InvalidOperationException(response.Error ?? "The service returned no hardware-control verification result.");
            }

            if (!ShouldCommitHardwareControlState(response.Success, result, enable))
            {
                _hardwareControlArmedThisConnection = false;
                ShowNotice(result.Message, result.RecoveryRequired ? "Danger" : "Warning");
                OnPropertyChanged(nameof(HardwareControlEnabled));
                return false;
            }

            SetHardwareControlState(enable);
            _hardwareControlPreferenceRequested = enable;
            PersistHardwareControlPreference(enable);
            _hardwareControlArmedThisConnection = enable;
            ShowNotice(enable
                ? $"Hardware control enabled after {result.Families.Count} GPU family/families passed read-back."
                : "Hardware control disabled after vendor/default state was restored and read back.",
                "Success");
            if (_refreshing)
            {
                // Initialisation arms after it has already fetched the
                // read-only inventory. A nested RefreshAsync is intentionally
                // discarded by the refresh guard, so replace only the snapshot
                // here and let the outer refresh build the first UI frame from
                // the newly armed descriptors.
                IpcResponse snapshotResponse = await _client.SendAsync(
                    NamedPipeRequestClient.CreateRequest(IpcCommand.GetInventory),
                    _lifetime.Token);
                EnsureSuccess(snapshotResponse);
                _snapshot = IpcJson.FromElement<HardwareSnapshot>(snapshotResponse.Payload)
                    ?? throw new InvalidDataException("Service returned an empty inventory response after hardware control changed.");
            }
            else
            {
                await RefreshAsync(full: true, userInitiated: false);
            }
            return true;
        }
        finally
        {
            IsHardwareControlChanging = false;
            _hardwareControlCommandGate.Release();
        }
    }

    private void SetHardwareControlState(bool enabled)
    {
        if (!Set(ref _hardwareControlEnabled, enabled, nameof(HardwareControlEnabled)))
        {
            OnPropertyChanged(nameof(HardwareControlEnabled));
        }
        OnPropertyChanged(nameof(HardwareControlBadge));
        RaiseHardwareControlCanExecuteChanged();
    }

    internal static bool ShouldCommitHardwareControlState(
        bool responseSuccess,
        HardwareControlTransactionResult result,
        bool requestedArmed) =>
        responseSuccess
        && result.AllRequestedFamiliesVerified
        && !result.RecoveryRequired
        && result.Armed == requestedArmed;

    private void RaiseHardwareControlCanExecuteChanged()
    {
        _applyGpuControlCommand.RaiseCanExecuteChanged();
        _startGpuAutoOcCommand.RaiseCanExecuteChanged();
        _enableGpuFanAutoModeCommand.RaiseCanExecuteChanged();
        _enableCaseFansAutoModeCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Live GPU control sliders (fan duty, power limit, clock offsets). Each
    /// apply is a normal one-action transactional profile: prepare, bounds
    /// clamp, apply, read-back verify, rollback on failure. The master
    /// Hardware-control switch supplies the Experimental + exact-device
    /// confirmations.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<GpuControlSlider> GpuControlSliders { get; } = [];

    /// <summary>Motherboard fan outputs surfaced as the same one-action slider controls.</summary>
    public System.Collections.ObjectModel.ObservableCollection<GpuControlSlider> FanControlSliders { get; } = [];

    private readonly AsyncCommand _applyGpuControlCommand;
    private readonly AsyncCommand _startGpuAutoOcCommand;
    private readonly AsyncCommand _enableGpuFanAutoModeCommand;
    private readonly AsyncCommand _enableCaseFansAutoModeCommand;

    public System.Windows.Input.ICommand ApplyGpuControlCommand => _applyGpuControlCommand;

    public System.Windows.Input.ICommand StartGpuAutoOcCommand => _startGpuAutoOcCommand;

    public System.Windows.Input.ICommand SetKrakenPumpCommand { get; }

    private void RebuildGpuControlSliders()
    {
        if (_snapshot is null)
        {
            return;
        }

        (string Prefix, string Label)[] families =
        [
            ("gpufan.duty:", "GPU fan duty"),
            ("gpupower.limit:", "GPU power limit"),
            ("gpuclock.core:", "GPU core clock offset"),
            ("gpuclock.memory:", "GPU memory clock offset")
        ];
        List<GpuControlSlider> next = [];
        foreach ((string prefix, string label) in families)
        {
            CapabilityDescriptor? capability = _snapshot.Capabilities
                .FirstOrDefault(item => item.Id.StartsWith(prefix, StringComparison.Ordinal));
            if (capability?.Range is NumericRange range)
            {
                next.Add(new GpuControlSlider
                {
                    CapabilityId = capability.Id,
                    AdapterId = capability.AdapterId,
                    DeviceId = capability.DeviceId,
                    Name = label,
                    Minimum = range.Minimum,
                    Maximum = range.Maximum,
                    Default = range.Default,
                    Unit = capability.Unit ?? string.Empty,
                    Value = prefix.StartsWith("gpuclock", StringComparison.Ordinal) ? 0
                        : prefix == "gpufan.duty:" ? range.Maximum
                        : range.Maximum
                });
            }
        }

        // Rebuild only when the capability set changes so a slider mid-drag is
        // not reset by the one-second snapshot refresh.
        if (!next.Select(item => item.CapabilityId).SequenceEqual(GpuControlSliders.Select(item => item.CapabilityId)))
        {
            GpuControlSliders.Clear();
            foreach (GpuControlSlider slider in next)
            {
                GpuControlSliders.Add(slider);
            }
        }

        // Motherboard fan outputs: every writable bounded cooling control gets
        // the same slider treatment. The transaction engine still enforces the
        // floors, pump/CPU-fan protections, and read-back on every apply.
        List<GpuControlSlider> fans = _snapshot.Capabilities
            .Where(capability => capability.Id.StartsWith("lhm.control:", StringComparison.Ordinal)
                && capability.Domain == ControlDomain.Cooling
                && capability.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified
                && capability.Range is NumericRange
                && GetCoolingOutputAssignment(capability) is not { IsSafetyCritical: true }
                // The NVML gpufan.duty slider owns the GPU cooler; LHM's own
                // GPU-fan controls are the same physical fans via a second path.
                && !capability.Id.Contains("/gpu-nvidia/", StringComparison.Ordinal))
            .OrderBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(capability =>
            {
                NumericRange range = (NumericRange)capability.Range!;
                // Start the slider at the fan's live duty when telemetry
                // exposes it, so opening the page never suggests a jump to 100%.
                string controlPath = capability.Id["lhm.control:".Length..];
                double? currentDuty = _snapshot.Sensors
                    .FirstOrDefault(sensor => sensor.Unit == "%"
                        && sensor.Value is not null
                        && sensor.SensorId.EndsWith(controlPath, StringComparison.Ordinal))
                    ?.Value;
                return new GpuControlSlider
                {
                    CapabilityId = capability.Id,
                    AdapterId = capability.AdapterId,
                    DeviceId = capability.DeviceId,
                    Name = capability.Name,
                    Minimum = range.Minimum,
                    Maximum = range.Maximum,
                    Unit = capability.Unit ?? "%",
                    Value = currentDuty is double duty
                        ? Math.Clamp(duty, range.Minimum, range.Maximum)
                        : range.Maximum
                };
            })
            .ToList();
        if (!fans.Select(item => item.CapabilityId).SequenceEqual(FanControlSliders.Select(item => item.CapabilityId)))
        {
            FanControlSliders.Clear();
            foreach (GpuControlSlider fan in fans)
            {
                FanControlSliders.Add(fan);
            }
        }
    }

    public ICommand EnableGpuFanAutoModeCommand => _enableGpuFanAutoModeCommand;

    public ICommand EnableCaseFansAutoModeCommand => _enableCaseFansAutoModeCommand;

    private enum AutomaticCoolingSelection
    {
        None,
        Silent,
        Balanced,
        Cooling
    }

    private AutomaticCoolingSelection _caseFanSelection;
    private AutomaticCoolingSelection _gpuFanSelection;

    public bool IsCaseFanSilentSelected => _caseFanSelection == AutomaticCoolingSelection.Silent;

    public bool IsCaseFanBalancedSelected => _caseFanSelection == AutomaticCoolingSelection.Balanced;

    public bool IsCaseFanCoolingSelected => _caseFanSelection == AutomaticCoolingSelection.Cooling;

    public bool IsGpuFanSilentSelected => _gpuFanSelection == AutomaticCoolingSelection.Silent;

    public bool IsGpuFanBalancedSelected => _gpuFanSelection == AutomaticCoolingSelection.Balanced;

    public bool IsGpuFanCoolingSelected => _gpuFanSelection == AutomaticCoolingSelection.Cooling;

    private void SetAutomaticCoolingSelection(bool gpuFans, AutomaticCoolingSelection selection)
    {
        ref AutomaticCoolingSelection target = ref gpuFans ? ref _gpuFanSelection : ref _caseFanSelection;
        if (target == selection)
        {
            return;
        }

        target = selection;
        string prefix = gpuFans ? "IsGpuFan" : "IsCaseFan";
        OnPropertyChanged($"{prefix}SilentSelected");
        OnPropertyChanged($"{prefix}BalancedSelected");
        OnPropertyChanged($"{prefix}CoolingSelected");
    }

    private void SynchronizeAutomaticCoolingSelection()
    {
        const string caseFanProfileId = "auto.profile.case-fans";
        const string gpuFanProfileId = "auto.profile.gpu-fan";
        string? activeProfileId = _status?.ActiveProfileId;
        bool caseFansActive = string.Equals(activeProfileId, caseFanProfileId, StringComparison.Ordinal);
        bool gpuFansActive = string.Equals(activeProfileId, gpuFanProfileId, StringComparison.Ordinal);

        if (!caseFansActive)
        {
            SetAutomaticCoolingSelection(gpuFans: false, AutomaticCoolingSelection.None);
        }
        if (!gpuFansActive)
        {
            SetAutomaticCoolingSelection(gpuFans: true, AutomaticCoolingSelection.None);
        }

        if ((caseFansActive || gpuFansActive)
            && _suiteProfilesById.TryGetValue(activeProfileId!, out ProfileV2? profile)
            && TryReadAutomaticCoolingMode(profile, out CoolingCurveMode mode))
        {
            bool gpuFans = gpuFansActive;
            SetAutomaticCoolingSelection(gpuFans, ToSelection(mode));
        }
    }

    internal static bool TryReadAutomaticCoolingMode(ProfileV2 profile, out CoolingCurveMode mode)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (CoolingCurveMode candidate in Enum.GetValues<CoolingCurveMode>())
        {
            if (profile.Name.EndsWith($" {candidate.ToString().ToLowerInvariant()} mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }

    private static AutomaticCoolingSelection ToSelection(CoolingCurveMode mode) => mode switch
    {
        CoolingCurveMode.Silent => AutomaticCoolingSelection.Silent,
        CoolingCurveMode.Cooling => AutomaticCoolingSelection.Cooling,
        _ => AutomaticCoolingSelection.Balanced
    };

    /// <summary>
    /// One-click automatic cooling mode: builds and applies a bounded
    /// temperature→duty graph (20% uncalibrated floor, full maximum for emergency
    /// headroom) through the service graph engine, which keeps read-back
    /// verification and the stale-sensor → maximum-cooling protection. GPU mode
    /// binds the armed GPU fan to GPU temperature; case-fan mode binds every
    /// writable motherboard fan output to the maximum of CPU and GPU
    /// temperature. Pump and CPU-fan role protections are enforced service-side.
    /// </summary>
    /// <summary>Maps a command parameter ("silent"/"balanced"/"cooling") to the curve mode, defaulting to Balanced.</summary>
    private static CoolingCurveMode ParseCoolingCurveMode(object? parameter) =>
        Enum.TryParse(parameter as string, ignoreCase: true, out CoolingCurveMode mode) ? mode : CoolingCurveMode.Balanced;

    /// <summary>
    /// Explains why an automatic fan mode has no usable output: a competing
    /// controller holding the fans (the common case — RigPilot blocks only
    /// overlapping controls and never fights another writer) names the blocker
    /// and points at Close blockers; a genuinely absent control says so.
    /// </summary>
    private string DescribeUnavailableFanOutputs(IReadOnlyList<CapabilityDescriptor> candidates, bool gpuFans)
    {
        string label = gpuFans ? "GPU fan control" : "motherboard fan control";
        string? owner = DescribeConflictOwners(candidates);
        if (owner is not null)
        {
            return $"{label} is blocked by {owner}. Close the competing app — Diagnostics has a \"Close blockers\" button — or stop it, then refresh.";
        }

        if (!HardwareControlEnabled)
        {
            return $"No armed {label} is available. Turn on Hardware control in the header, then refresh.";
        }

        return gpuFans
            ? "No GPU fan control was reported on this system. Refresh after the GPU is detected."
            : "No writable motherboard fan outputs were reported on this system.";
    }

    /// <summary>The distinct competing-writer names across any Blocked capabilities, or null when none is blocked.</summary>
    private static string? DescribeConflictOwners(IReadOnlyList<CapabilityDescriptor> candidates)
    {
        string[] owners = [.. candidates
            .Where(capability => capability.State == CapabilityAccessState.Blocked
                && !string.IsNullOrWhiteSpace(capability.ConflictOwner))
            .SelectMany(capability => capability.ConflictOwner!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        return owners.Length == 0 ? null : string.Join(", ", owners);
    }

    // --- Adaptive automatic cooling: pick the curve from live temperatures ----

    private CoolingCurveMode? _lastAdaptiveCoolingMode;
    private AsyncCommand? _startAdaptiveCoolingCommand;

    public ICommand StartAdaptiveCoolingCommand => _startAdaptiveCoolingCommand ??= new AsyncCommand(
        parameter => StartAdaptiveCoolingAsync(string.Equals(parameter as string, "gpu", StringComparison.OrdinalIgnoreCase)),
        _ => CanRunHardwareAction(),
        ReportError,
        _ => ShowHardwareActionBlocked());

    /// <summary>
    /// One-click adaptive cooling: reads the current CPU package and GPU core
    /// temperatures and picks Silent, Balanced, or Cooling (with hysteresis so
    /// the choice doesn't flap), then applies it through the same verified
    /// graph engine as the explicit modes.
    /// </summary>
    public async Task StartAdaptiveCoolingAsync(bool gpuFans)
    {
        double? cpu = ReadLiveTemperature("CPU Package");
        double? gpu = ReadLiveTemperature("GPU Core");
        CoolingCurveMode mode = CoolingModeSelector.Choose(cpu, gpu, _lastAdaptiveCoolingMode);
        _lastAdaptiveCoolingMode = mode;
        ShowNotice($"Adaptive cooling picked {mode}: {CoolingModeSelector.Describe(mode, cpu, gpu)}", "Info");
        await StartAutomaticCoolingAsync(gpuFans, mode);
    }

    private double? ReadLiveTemperature(string sensorName) => (_snapshot?.Sensors ?? [])
        .Where(sensor => string.Equals(sensor.Unit, "°C", StringComparison.OrdinalIgnoreCase)
            && sensor.Quality == SensorQuality.Good
            && sensor.Value.HasValue
            && sensor.Name.Contains(sensorName, StringComparison.OrdinalIgnoreCase))
        .Select(sensor => sensor.Value)
        .FirstOrDefault();

    // --- Full Auto OC: core pass, wait for completion, then memory pass ------

    private AsyncCommand? _startFullAutoOcCommand;
    private string _fullAutoOcStatus = "Runs the core-clock auto-OC to completion, then the memory pass, unattended — each with the full safety envelope (83 °C ceiling, screening, rollback, boot sentinel).";

    public ICommand StartFullAutoOcCommand => _startFullAutoOcCommand ??= new AsyncCommand(
        _ => StartFullAutoOcAsync(),
        _ => CanRunHardwareAction(requireIdle: true),
        ReportError,
        _ => ShowHardwareActionBlocked(requireIdle: true));

    public string FullAutoOcStatus
    {
        get => _fullAutoOcStatus;
        private set => Set(ref _fullAutoOcStatus, value);
    }

    /// <summary>
    /// The complete unattended overclocking pass: core auto-OC, wait for the
    /// bounded engine to finish (including its screening and restore), then
    /// the memory auto-OC. A core pass that ends in anything but Completed
    /// stops the sequence — memory is never tuned on top of a failed core run.
    /// </summary>
    public async Task StartFullAutoOcAsync()
    {
        FullAutoOcStatus = "Core clock pass starting…";
        string? coreOperationId = await StartGpuClockAutoOcAsync(
            "gpuclock.core:", "GPU core clock", AutoOcCoreSafetyMarginMhz);
        if (coreOperationId is null)
        {
            FullAutoOcStatus = "The core pass did not start; the memory pass was not run.";
            return;
        }

        HardwareOperationState? coreOutcome = await WaitForOperationAsync(coreOperationId, "core clock");
        if (coreOutcome != HardwareOperationState.Completed)
        {
            FullAutoOcStatus = $"Stopped after the core pass ({coreOutcome?.ToString() ?? "not started"}); the memory pass was not run.";
            ShowNotice(FullAutoOcStatus, "Warning");
            return;
        }

        FullAutoOcStatus = "Core pass completed. Memory clock pass starting…";
        string? memoryOperationId = await StartGpuClockAutoOcAsync(
            "gpuclock.memory:", "GPU memory clock", AutoOcMemorySafetyMarginMhz);
        HardwareOperationState? memoryOutcome = memoryOperationId is null
            ? null
            : await WaitForOperationAsync(memoryOperationId, "memory clock");
        FullAutoOcStatus = memoryOutcome == HardwareOperationState.Completed
            ? "Full Auto OC finished: core and memory passes both completed with their safety margins applied. Run the benchmark on Games & tools to confirm the gain."
            : $"Core pass completed; the memory pass ended {memoryOutcome?.ToString() ?? "without starting"}.";
        ShowNotice(FullAutoOcStatus, memoryOutcome == HardwareOperationState.Completed ? "Success" : "Warning");
    }

    /// <summary>
    /// Polls the service operation status until the current tune reaches a
    /// terminal state (bounded at 45 minutes — far beyond any screening run).
    /// </summary>
    private async Task<HardwareOperationState?> WaitForOperationAsync(string operationId, string label)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(45);
        HardwareOperationState? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_operation is { } operation
                && string.Equals(operation.Id, operationId, StringComparison.Ordinal))
            {
                last = operation.State;
                if (operation.State is not (HardwareOperationState.Pending or HardwareOperationState.Running or HardwareOperationState.Screening))
                {
                    return operation.State;
                }

                FullAutoOcStatus = $"Auto OC ({label}): {operation.State} — {operation.Message}";
            }

            await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token);
            await RefreshAsync(full: false, userInitiated: false);
            if (_operation is { } current
                && !string.Equals(current.Id, operationId, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return last;
    }

    public async Task StartAutomaticCoolingAsync(bool gpuFans, CoolingCurveMode mode = CoolingCurveMode.Balanced)
    {
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header first.", "Warning");
            return;
        }
        if (_snapshot is null)
        {
            return;
        }

        bool IsFanOutput(CapabilityDescriptor capability) => gpuFans
            ? capability.Id.StartsWith("gpufan.duty:", StringComparison.Ordinal) && capability.Range is NumericRange
            : capability.Id.StartsWith("lhm.control:", StringComparison.Ordinal)
                && capability.Domain == ControlDomain.Cooling
                && capability.Range is NumericRange
                && !capability.Id.Contains("/gpu-nvidia/", StringComparison.Ordinal);

        CapabilityDescriptor[] candidates = [.. _snapshot.Capabilities.Where(IsFanOutput)];
        CapabilityDescriptor[] outputs = [.. candidates.Where(capability =>
            capability.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified)
            .Where(capability => gpuFans || GetCoolingOutputAssignment(capability) is not { IsSafetyCritical: true })];
        if (outputs.Length == 0)
        {
            bool onlyProtectedOutputs = !gpuFans
                && candidates.Any(capability => GetCoolingOutputAssignment(capability) is { IsSafetyCritical: true });
            ShowNotice(
                onlyProtectedOutputs
                    ? "No case-fan output is available. CPU-fan and pump outputs remain excluded from one-click case-fan modes."
                    : DescribeUnavailableFanOutputs(candidates, gpuFans),
                "Warning");
            return;
        }

        AdaptiveCoolingProfileDraft draft;
        try
        {
            draft = AdaptiveCoolingProfileFactory.CreateAutomaticMode(
                outputs,
                gpuFans ? "GPU fan" : "Case fans",
                _snapshot.Sensors,
                preferGpuSourceOnly: gpuFans,
                mode: mode);
        }
        catch (InvalidOperationException exception)
        {
            ShowNotice(exception.Message, "Warning");
            return;
        }

        IpcResponse saveResponse = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SaveCoolingGraph, draft.Graph, _status?.StateRevision, Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(saveResponse);
        UpdateStateRevision(saveResponse);

        IpcResponse applyResponse = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.ApplyProfileV2,
                new ApplyProfileV2Request(
                    draft.Profile,
                    ProfileActivationSource.Manual,
                    ConfirmExperimental: true,
                    [.. outputs.Select(capability => capability.DeviceId).Distinct(StringComparer.Ordinal)],
                    ConfirmManualVoltage: false),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(applyResponse);
        SetAutomaticCoolingSelection(gpuFans, ToSelection(mode));
        ShowNotice(
            $"{draft.Profile.Name} is active: {outputs.Length} output(s) follow the {mode} temperature curve with a {AdaptiveCoolingProfileFactory.UncalibratedFloorDutyPercent:0}% floor (or each controller's higher reported minimum). Stale sensors command maximum cooling.",
            "Success");
        await RefreshAsync(full: true, userInitiated: false);
    }

    public async Task ApplyGpuControlAsync(GpuControlSlider slider)
    {
        ArgumentNullException.ThrowIfNull(slider);
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header first.", "Warning");
            return;
        }

        double value = Math.Round(slider.Value);
        ProfileAction action = new(
            $"gpu-slider-{Guid.NewGuid():N}",
            slider.AdapterId,
            slider.CapabilityId,
            ControlValue.FromNumeric(value),
            Required: true,
            Order: 0);
        ProfileV2 profile = new(
            ProfileV2.CurrentSchemaVersion,
            $"gpu-direct-{slider.CapabilityId}",
            slider.Name,
            "Direct GPU control from the Performance page.",
            [action],
            new SafetyLimits(),
            null,
            null,
            null,
            [],
            [],
            IsBuiltIn: false,
            IsExperimental: true);
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.ApplyProfileV2,
            new ApplyProfileV2Request(
                profile,
                ProfileActivationSource.Manual,
                ConfirmExperimental: true,
                [slider.DeviceId],
                ConfirmManualVoltage: false),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        ShowNotice($"{slider.Name} applied and read-back verified at {value:0.##} {slider.Unit}.", "Success");
        await RefreshAsync(full: true, userInitiated: false);
    }

    // --- Efficiency power target (documented power-limit APIs only) -----------

    private string _undervoltStatus = "Lowers the GPU power target through the same transactional, read-back-verified path as the slider. Frame rates stay close to stock in most games while heat and fan noise drop. This is not a voltage-frequency curve editor.";

    public string UndervoltStatus
    {
        get => _undervoltStatus;
        private set => Set(ref _undervoltStatus, value);
    }

    private AsyncCommand? _applyUndervoltPresetCommand;
    public ICommand ApplyUndervoltPresetCommand => _applyUndervoltPresetCommand ??= new AsyncCommand(
        parameter => ApplyUndervoltPresetAsync(parameter as string ?? string.Empty),
        _ => CanRunHardwareAction(),
        ReportError,
        _ => ShowHardwareActionBlocked());

    public async Task ApplyUndervoltPresetAsync(string preset)
    {
        GpuControlSlider? power = GpuControlSliders.FirstOrDefault(slider =>
            slider.CapabilityId.StartsWith("gpupower.limit:", StringComparison.Ordinal));
        if (power is null)
        {
            ShowNotice("The GPU power-limit control is not available. Turn on Hardware control and check the Devices page.", "Warning");
            return;
        }

        double? target = UndervoltPresets.ComputeTargetWatts(power.Minimum, power.Maximum, power.Default, preset);
        if (target is not double watts)
        {
            ShowNotice("That efficiency power target is not available for this GPU's reported power range.", "Warning");
            return;
        }

        await ApplyGpuControlAsync(new GpuControlSlider
        {
            CapabilityId = power.CapabilityId,
            AdapterId = power.AdapterId,
            DeviceId = power.DeviceId,
            Name = power.Name,
            Minimum = power.Minimum,
            Maximum = power.Maximum,
            Default = power.Default,
            Unit = power.Unit,
            Value = watts,
        });
        UndervoltStatus = $"{UndervoltPresets.Describe(preset)} applied: power limit {watts:0} {power.Unit}, read-back verified. Run the frame-rate benchmark on Games & tools to confirm your games hold their FPS.";
    }

    // --- NZXT Kraken pump control ---------------------------------------------

    private double _krakenPumpDutyTarget = 100;
    private string _krakenPumpStatus = "Pump duty 60–100%. The write is read back from the cooler's own status stream; the pump is never slowed below the floor or stopped.";

    public double KrakenPumpDutyTarget
    {
        get => _krakenPumpDutyTarget;
        set => Set(ref _krakenPumpDutyTarget, value);
    }

    public string KrakenPumpStatus
    {
        get => _krakenPumpStatus;
        private set => Set(ref _krakenPumpStatus, value);
    }

    public async Task SetKrakenPumpAsync()
    {
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header first.", "Warning");
            return;
        }

        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.SetKrakenPumpDuty,
                new KrakenPumpRequestV1(
                    KrakenPumpRequestV1.CurrentSchemaVersion,
                    (int)Math.Round(KrakenPumpDutyTarget),
                    ConfirmExperimental: true,
                    KrakenPumpRequestV1.ExactDeviceId)),
            _lifetime.Token);
        EnsureSuccess(response);
        KrakenPumpResultV1 result = IpcJson.FromElement<KrakenPumpResultV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty pump result.");
        KrakenPumpStatus = result.Message;
        ShowNotice(result.Message, result.Outcome switch
        {
            KrakenPumpOutcome.ReadBackVerified => "Success",
            KrakenPumpOutcome.WriteIssued => "Info",
            _ => "Warning"
        });
    }

    // Refined Auto-OC tuning parameters. The coarse scan climbs in ~12 steps;
    // the refinement then bisects the gap to the first failing step to find the
    // stability edge, and the safety margin backs the shipped offset off from
    // that edge so it runs with headroom rather than on the cliff. Memory uses
    // a larger margin because GDDR6X clock error scales in bigger increments.
    private const int AutoOcRefinementCandidates = 5;
    private const double AutoOcCoreSafetyMarginMhz = 15;
    private const double AutoOcMemorySafetyMarginMhz = 100;
    // Stop climbing once a stable candidate is within this many degrees of the
    // 83 °C ceiling, so the shipped overclock keeps real thermal headroom.
    private const double AutoOcThermalHeadroomCelsius = 4;

    public System.Windows.Input.ICommand StartGpuMemoryAutoOcCommand => _startGpuMemoryAutoOcCommand ??= new AsyncCommand(
        _ => StartGpuMemoryAutoOcAsync(),
        _ => CanRunHardwareAction(requireIdle: true),
        ReportError,
        _ => ShowHardwareActionBlocked(requireIdle: true));

    private AsyncCommand? _startGpuMemoryAutoOcCommand;

    /// <summary>
    /// One-click GPU core auto-OC: climbs the armed core clock offset, refines
    /// the stability edge between the last stable step and the first failing
    /// one, then backs off a small safety margin — the way a careful
    /// overclocker works. Uses the existing bounded engine (Performance
    /// objective, 83 °C ceiling, 10-minute final screening, WHEA/thermal/
    /// display-reset aborts, rollback, boot sentinel). No voltage is touched.
    /// The Hardware-control switch supplies the acknowledgements.
    /// </summary>
    public async Task StartGpuAutoOcAsync() =>
        _ = await StartGpuClockAutoOcAsync("gpuclock.core:", "GPU core clock", AutoOcCoreSafetyMarginMhz);

    /// <summary>
    /// One-click GPU memory auto-OC: the same refined climb/edge-find/back-off
    /// search applied to the armed memory clock offset. GDDR6X memory tuning is
    /// often the larger real-world gain on this class of card. Same safety gates.
    /// </summary>
    public async Task StartGpuMemoryAutoOcAsync() =>
        _ = await StartGpuClockAutoOcAsync("gpuclock.memory:", "GPU memory clock", AutoOcMemorySafetyMarginMhz);

    private async Task<string?> StartGpuClockAutoOcAsync(string capabilityPrefix, string label, double safetyMarginMhz)
    {
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header first.", "Warning");
            return null;
        }

        OperationTargetDisplay? target = TuneTargets.FirstOrDefault(
            item => item.Descriptor.Id.StartsWith(capabilityPrefix, StringComparison.Ordinal));
        if (target is null)
        {
            // The tuning target list only carries armable controls; if the
            // capability exists but is blocked by a competing writer, say so.
            CapabilityDescriptor? blocked = _snapshot?.Capabilities.FirstOrDefault(capability =>
                capability.Id.StartsWith(capabilityPrefix, StringComparison.Ordinal)
                && capability.State == CapabilityAccessState.Blocked);
            string owner = blocked?.ConflictOwner is { Length: > 0 } conflict ? conflict : string.Empty;
            ShowNotice(owner.Length > 0
                ? $"{label} tuning is blocked by {owner}. Close the competing app (Diagnostics has a \"Close blockers\" button) or stop it, then refresh."
                : $"The {label} target is not available on this system.", "Warning");
            return null;
        }

        SelectedTuneTarget = target;
        SelectedTuneObjective = TuningObjective.Performance;
        TuneTemperatureCeilingText = "83";
        AdvancedWritesAcknowledged = true;
        TuneDeviceAcknowledged = true;
        await StartTuneCoreAsync(AutoOcRefinementCandidates, safetyMarginMhz, AutoOcThermalHeadroomCelsius);
        return _operation?.Id;
    }

    private async Task EnsureHardwareControlArmedAsync()
    {
        if (_status is null || !CanUseServiceWrites)
        {
            return;
        }

        if (_status.HardwareControlArmed == _hardwareControlPreferenceRequested)
        {
            _hardwareControlArmedThisConnection = _status.HardwareControlArmed;
            SetHardwareControlState(_status.HardwareControlArmed);
            return;
        }

        if (!_hardwareControlArmAttemptedThisConnection)
        {
            _hardwareControlArmAttemptedThisConnection = true;
            await ApplyHardwareControlSafelyAsync(_hardwareControlPreferenceRequested);
        }
    }

    private static bool ReadPersistedHardwareControlPreference()
    {
        // Owner policy amendment (2026-07-16): Hardware control defaults ON
        // when no preference has been persisted yet; an explicit opt-out is
        // remembered. Arming still records the Experimental + exact-device
        // confirmations on every connect, and unchecking restores defaults.
        try
        {
            if (!System.IO.File.Exists(ControlPreferencesPath))
            {
                return true;
            }

            return System.Text.Json.JsonSerializer.Deserialize<ControlPreferences>(
                System.IO.File.ReadAllText(ControlPreferencesPath))?.HardwareControlEnabled == true;
        }
        catch (Exception exception) when (exception is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void PersistHardwareControlPreference(bool enabled)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ControlPreferencesPath)!);
            System.IO.File.WriteAllText(
                ControlPreferencesPath,
                System.Text.Json.JsonSerializer.Serialize(new ControlPreferences(enabled)));
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            // A failed preference write only means the switch resets next launch.
        }
    }

    private sealed record ControlPreferences(bool HardwareControlEnabled);


    public sealed class GpuControlSlider
    {
        public string CapabilityId { get; init; } = string.Empty;
        public string AdapterId { get; init; } = string.Empty;
        public string DeviceId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public double Minimum { get; init; }
        public double Maximum { get; init; }
        public string Unit { get; init; } = string.Empty;
        public double Value { get; set; }

        /// <summary>Vendor default (Afterburner-style 100% reference), when the adapter discovered one.</summary>
        public double? Default { get; init; }
    }
}
