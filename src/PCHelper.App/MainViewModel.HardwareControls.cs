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

    private bool _hardwareControlEnabled = ReadPersistedHardwareControlPreference();
    private bool _hardwareControlArmedThisConnection;

    public bool HardwareControlEnabled
    {
        get => _hardwareControlEnabled;
        set
        {
            if (!Set(ref _hardwareControlEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HardwareControlBadge));
            PersistHardwareControlPreference(value);
            _hardwareControlArmedThisConnection = false;
            _applyGpuControlCommand.RaiseCanExecuteChanged();
            _startGpuAutoOcCommand.RaiseCanExecuteChanged();
            _enableGpuFanAutoModeCommand.RaiseCanExecuteChanged();
            _enableCaseFansAutoModeCommand.RaiseCanExecuteChanged();
            _ = ApplyHardwareControlSafelyAsync(value);
        }
    }

    /// <summary>User-facing badge for the GPU controls card (On/Off, not True/False).</summary>
    public string HardwareControlBadge => HardwareControlEnabled ? "Hardware control: On" : "Hardware control: Off";

    private async Task ApplyHardwareControlSafelyAsync(bool enable)
    {
        try
        {
            await ApplyHardwareControlAsync(enable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowNotice($"Hardware control change failed: {exception.Message}", "Warning");
        }
    }

    private async Task ApplyHardwareControlAsync(bool enable)
    {
        if (!IsServiceOnline || _snapshot is null)
        {
            return;
        }

        (IpcCommand Command, string Prefix)[] families =
        [
            (IpcCommand.SetGpuFanControlArmed, "gpufan."),
            (IpcCommand.SetGpuPowerLimitArmed, "gpupower."),
            (IpcCommand.SetGpuClockOffsetArmed, "gpuclock.")
        ];
        int armed = 0;
        foreach ((IpcCommand command, string prefix) in families)
        {
            string? deviceId = _snapshot.Capabilities
                .FirstOrDefault(capability => capability.Id.StartsWith(prefix, StringComparison.Ordinal))
                ?.DeviceId;
            if (deviceId is null)
            {
                continue;
            }

            IReadOnlyList<string> confirmed = [deviceId];
            object payload = command switch
            {
                IpcCommand.SetGpuFanControlArmed => new SetGpuFanControlArmedRequest(enable, enable, confirmed),
                IpcCommand.SetGpuPowerLimitArmed => new SetGpuPowerLimitArmedRequest(enable, enable, confirmed),
                _ => new SetGpuClockOffsetArmedRequest(enable, enable, confirmed)
            };
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(command, payload),
                _lifetime.Token);
            if (response.Success)
            {
                armed++;
            }
        }

        _hardwareControlArmedThisConnection = enable && armed > 0;
        if (armed > 0)
        {
            ShowNotice(enable
                ? $"Hardware control enabled: {armed} GPU write famil{(armed == 1 ? "y" : "ies")} armed for this machine."
                : "Hardware control disabled; vendor defaults restored.",
                "Success");
            await RefreshAsync(full: true, userInitiated: false);
        }
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

    /// <summary>
    /// One-click automatic cooling mode: builds and applies a conservative
    /// temperature→duty graph (50% safety floor, full maximum for emergency
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
            capability.State is CapabilityAccessState.Experimental or CapabilityAccessState.Verified)];
        if (outputs.Length == 0)
        {
            ShowNotice(DescribeUnavailableFanOutputs(candidates, gpuFans), "Warning");
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
        ShowNotice(
            $"{draft.Profile.Name} is active: {outputs.Length} output(s) follow the {mode} temperature curve with a {AdaptiveCoolingProfileFactory.ConservativeFloorDutyPercent:0}% floor. Stale sensors command maximum cooling.",
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

    // --- Guided undervolt (power-limit based; documented APIs only) -----------

    private string _undervoltStatus = "Lowers the GPU power target through the same transactional, read-back-verified path as the slider. Frame rates stay close to stock in most games while heat and fan noise drop. This is not a voltage-frequency curve editor.";

    public string UndervoltStatus
    {
        get => _undervoltStatus;
        private set => Set(ref _undervoltStatus, value);
    }

    private AsyncCommand? _applyUndervoltPresetCommand;
    public ICommand ApplyUndervoltPresetCommand => _applyUndervoltPresetCommand ??= new AsyncCommand(
        parameter => ApplyUndervoltPresetAsync(parameter as string ?? string.Empty),
        _ => IsServiceOnline && HardwareControlEnabled,
        ReportError);

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
            ShowNotice("That undervolt preset is not available for this GPU's reported power range.", "Warning");
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
        _ => IsServiceOnline && HardwareControlEnabled,
        ReportError);

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
    public Task StartGpuAutoOcAsync() =>
        StartGpuClockAutoOcAsync("gpuclock.core:", "GPU core clock", AutoOcCoreSafetyMarginMhz);

    /// <summary>
    /// One-click GPU memory auto-OC: the same refined climb/edge-find/back-off
    /// search applied to the armed memory clock offset. GDDR6X memory tuning is
    /// often the larger real-world gain on this class of card. Same safety gates.
    /// </summary>
    public Task StartGpuMemoryAutoOcAsync() =>
        StartGpuClockAutoOcAsync("gpuclock.memory:", "GPU memory clock", AutoOcMemorySafetyMarginMhz);

    private async Task StartGpuClockAutoOcAsync(string capabilityPrefix, string label, double safetyMarginMhz)
    {
        if (!HardwareControlEnabled)
        {
            ShowNotice("Turn on Hardware control in the header first.", "Warning");
            return;
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
            return;
        }

        SelectedTuneTarget = target;
        SelectedTuneObjective = TuningObjective.Performance;
        TuneTemperatureCeilingText = "83";
        AdvancedWritesAcknowledged = true;
        TuneDeviceAcknowledged = true;
        await StartTuneCoreAsync(AutoOcRefinementCandidates, safetyMarginMhz, AutoOcThermalHeadroomCelsius);
    }

    private async Task EnsureHardwareControlArmedAsync()
    {
        if (HardwareControlEnabled && !_hardwareControlArmedThisConnection)
        {
            _hardwareControlArmedThisConnection = true; // set first so a failure does not retry every second
            await ApplyHardwareControlSafelyAsync(true);
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
