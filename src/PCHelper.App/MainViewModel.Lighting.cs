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
    private async Task AddLightingZoneCoreAsync()
    {
        DynamicLightingDevice device = SelectedDynamicLightingDevice
            ?? throw new InvalidOperationException("Select a Dynamic Lighting device first.");
        int[] indices = ParseLightingLedIndices(LightingZoneLedIndices, device.LampCount);
        if (!TryParseDouble(LightingZoneXText, out double x)
            || !TryParseDouble(LightingZoneYText, out double y)
            || !TryParseDouble(LightingZoneWidthText, out double width)
            || !TryParseDouble(LightingZoneHeightText, out double height)
            || x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Zone position must use non-negative X/Y and positive width/height values.");
        }
        LightingZoneV1 zone = new(
            $"zone.{Guid.NewGuid():N}",
            device.Id,
            indices,
            x,
            y,
            width,
            height);
        DraftLightingZones.Add(zone);
        OnPropertyChanged(nameof(CanSaveLightingLayout));
        _saveLightingLayoutCommand.RaiseCanExecuteChanged();
        ShowNotice($"Added {indices.Length} LED(s) from {device.Name} to the physical layout.", "Success");
    }

    private async Task SaveLightingLayoutCoreAsync()
    {
        if (!TryParseDouble(OpenRgbBrightnessText, out double brightness) || brightness is < 0 or > 100)
        {
            throw new InvalidOperationException("Lighting brightness must be a value from 0 to 100.");
        }
        LightingSceneV1 scene = SelectedLightingScene is null
            ? new LightingSceneV1(
                LightingSceneV1.CurrentSchemaVersion,
                $"scene.{Guid.NewGuid():N}",
                LightingLayoutName.Trim(),
                string.Empty,
                brightness,
                DraftLightingZones.ToArray(),
                DynamicLightingDevices.Where(device => !device.IsEnabled).Select(device => device.Id).ToArray())
            : SelectedLightingScene with
            {
                Name = LightingLayoutName.Trim(),
                BrightnessPercent = brightness,
                Zones = DraftLightingZones.ToArray(),
                DisabledDeviceIds = DynamicLightingDevices.Where(device => !device.IsEnabled).Select(device => device.Id).ToArray()
            };
        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.SaveLightingScene, scene, idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        LightingSceneV1 saved = IpcJson.FromElement<LightingSceneV1>(response.Payload) ?? scene;
        Replace(LightingScenes, LightingScenes.Where(item => item.Id != saved.Id).Append(saved).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        SelectedLightingScene = saved;
        SelectedGameLightingScene = saved;
        NotifyUserFeatureProperties();
        ShowNotice($"Saved physical lighting layout '{saved.Name}'.", "Success");
    }

    private async Task ApplyDynamicLightingSceneCoreAsync()
    {
        LightingSceneV1 scene = SelectedLightingScene
            ?? throw new InvalidOperationException("Select a saved lighting layout first.");
        if (HasDynamicLightingConflict)
        {
            throw new InvalidOperationException(DynamicLightingConflictReason);
        }
        if (!TryParseOpenRgbInputs(out string colour, out _))
        {
            throw new InvalidOperationException("Enter a six-digit RGB colour and brightness from 0 to 100.");
        }
        BusyMessage = "Applying Windows Dynamic Lighting scene";
        IsBusy = true;
        try
        {
            await DynamicLightingBridge.ApplyStaticSceneAsync(scene, colour, _lifetime.Token);
            DynamicLightingStatus = $"Applied '{scene.Name}' through Windows Dynamic Lighting.";
            RebuildRgbRouteAssessments();
            ShowNotice(DynamicLightingStatus, "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }


    private static int[] ParseLightingLedIndices(string input, int lampCount)
    {
        if (lampCount <= 0)
        {
            throw new InvalidOperationException("The selected Dynamic Lighting device reports no controllable lamps.");
        }
        HashSet<int> values = [];
        foreach (string token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] range = token.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out int start))
            {
                throw new InvalidOperationException("LED indices must use values such as '0-15, 20, 22-24'.");
            }
            int last = start;
            if (range.Length == 2 && !int.TryParse(range[1], out last))
            {
                throw new InvalidOperationException("LED indices must use values such as '0-15, 20, 22-24'.");
            }
            if (start < 0 || last < start || last >= lampCount || last - start > 2_048)
            {
                throw new InvalidOperationException($"LED indices must be within 0 to {lampCount - 1}.");
            }
            for (int index = start; index <= last; index++)
            {
                values.Add(index);
            }
        }
        return values.Order().ToArray();
    }

    private HardwareOperationEligibility GetCalibrationEligibility()
    {
        if (!CanUseServiceWrites)
        {
            return HardwareOperationEligibility.Deny(ServiceCompatibilityMessage);
        }

        if (SelectedCalibrationTarget is not OperationTargetDisplay target)
        {
            return HardwareOperationEligibility.Deny("Select a detected cooling control.");
        }

        if (target.RpmSensorId is null)
        {
            return HardwareOperationEligibility.Deny("No RPM sensor from the same adapter and exact device could be paired with this control.");
        }

        if (GetCoolingOutputAssignment(target.Descriptor)?.Role == CoolingOutputRole.Pump
            || SelectedCoolingOutputRole == CoolingOutputRole.Pump)
        {
            return HardwareOperationEligibility.Deny("Pump calibration is blocked until an exact device-specific nonzero-floor qualification path exists.");
        }

        if (SelectedFanCommissioningSession is not { } session
            || !string.Equals(session.CapabilityId, target.Descriptor.Id, StringComparison.Ordinal))
        {
            return HardwareOperationEligibility.Deny("Select the matching commissioning session before starting a bounded calibration.");
        }

        if (!FanCommissioningWorkflow.CanRunCalibration(session, out string? commissioningReason))
        {
            return HardwareOperationEligibility.Deny(commissioningReason!);
        }

        if (AllowCaseFanStop
            && (IsSelectedCoolingOutputProtected
                || target.Descriptor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || target.Descriptor.Name.Contains("pump", StringComparison.OrdinalIgnoreCase)))
        {
            return HardwareOperationEligibility.Deny("Zero-RPM calibration is forbidden for CPU fans and pumps.");
        }

        return HardwareOperationEligibilityEvaluator.ForCalibration(
            target.Descriptor,
            AdvancedWritesAcknowledged,
            CalibrationDeviceAcknowledged);
    }

    private FanCalibrationTemperatureLimit[] BuildCalibrationTemperatureLimits(
        CapabilityDescriptor capability) => (_snapshot?.Sensors ?? [])
        .Where(sensor => string.Equals(sensor.AdapterId, capability.AdapterId, StringComparison.Ordinal)
            && string.Equals(sensor.DeviceId, capability.DeviceId, StringComparison.Ordinal)
            && string.Equals(sensor.Unit, "Ã‚Â°C", StringComparison.OrdinalIgnoreCase)
            && sensor.Quality == SensorQuality.Good
            && sensor.Value.HasValue
            && !sensor.Name.Contains("Critical Temperature", StringComparison.OrdinalIgnoreCase)
            && !sensor.Name.Contains("Warning Temperature", StringComparison.OrdinalIgnoreCase))
        .OrderBy(sensor => sensor.Name, StringComparer.Ordinal)
        .Take(8)
        .Select(sensor => new FanCalibrationTemperatureLimit(
            sensor.SensorId,
            sensor.Name.Contains("Memory Junction", StringComparison.OrdinalIgnoreCase)
                ? 90
                : sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                    ? 85
                    : sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)
                        ? 80
                        : 90))
        .ToArray();

    private HardwareOperationEligibility GetTuneEligibility()
    {
        if (!CanUseServiceWrites)
        {
            return HardwareOperationEligibility.Deny(ServiceCompatibilityMessage);
        }

        if (SelectedTuneTarget is not OperationTargetDisplay target)
        {
            return HardwareOperationEligibility.Deny("Select a bounded cooling, CPU, or GPU control.");
        }

        if (GetCoolingOutputAssignment(target.Descriptor) is { IsSafetyCritical: true })
        {
            return HardwareOperationEligibility.Deny("Automatic tuning is unavailable for a persisted CPU-fan or pump output.");
        }

        if (!TryReadTuneLimits(out double temperatureCeiling, out double? powerCeiling))
        {
            return HardwareOperationEligibility.Deny(
                "Enter a temperature ceiling from 40 to 100 Ã‚Â°C and an optional positive power ceiling.");
        }

        return HardwareOperationEligibilityEvaluator.ForTuning(
            target.Descriptor,
            CreateTunePlan(target, temperatureCeiling, powerCeiling),
            AdvancedWritesAcknowledged,
            TuneDeviceAcknowledged);
    }

    private TunePlan CreateTunePlan(
        OperationTargetDisplay target,
        double temperatureCeiling,
        double? powerCeiling)
    {
        NumericRange range = target.Descriptor.Range
            ?? throw new InvalidOperationException("The selected capability has no numeric bounds.");
        double minimum = target.Descriptor.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
            && SelectedTuneObjective == TuningObjective.Performance
                ? range.Maximum
                : range.Minimum;
        // A GPU clock-offset overclocking search must never probe below stock:
        // negative offsets are underclocks and only waste screening time. The
        // driver delta range spans e.g. -1000..+1000 MHz; clamp the search
        // floor to 0 (stock) so the engine converges inside the useful half.
        if (SelectedTuneObjective == TuningObjective.Performance
            && target.Descriptor.Id.StartsWith("gpuclock.", StringComparison.Ordinal))
        {
            minimum = Math.Max(0, minimum);
        }
        return new TunePlan(
            Guid.NewGuid().ToString("N"),
            target.Descriptor.DeviceId,
            SelectedTuneObjective,
            new Dictionary<string, TuneBounds>(StringComparer.Ordinal)
            {
                [target.Descriptor.Id] = new TuneBounds(minimum, range.Maximum, range.Step)
            },
            TimeSpan.FromMinutes(10),
            temperatureCeiling,
            powerCeiling,
            Provisional: true,
            SoakStartedAt: null,
            ActiveUseRequired: TimeSpan.FromHours(10),
            ColdBootsRequired: 3);
    }

    private bool TryReadTuneLimits(out double temperatureCeiling, out double? powerCeiling)
    {
        powerCeiling = null;
        if (!TryParseDouble(TuneTemperatureCeilingText, out temperatureCeiling)
            || temperatureCeiling is < 40 or > 100)
        {
            return false;
        }

        string powerText = TunePowerCeilingText.Trim();
        if (powerText.Length == 0)
        {
            return true;
        }

        if (!TryParseDouble(powerText, out double parsedPower) || parsedPower <= 0)
        {
            return false;
        }

        powerCeiling = parsedPower;
        return true;
    }


    /// <summary>
    /// If the user has OpenRGB installed but its SDK server is not running,
    /// launch it minimized on demand (loopback server mode) so "Connect" is a
    /// single click. Nothing persistent is configured, nothing is downloaded,
    /// and an absent installation just falls through to the normal
    /// connection-failed message.
    /// </summary>
    private static async Task EnsureOpenRgbServerRunningAsync()
    {
        if (System.Diagnostics.Process.GetProcessesByName("OpenRGB").Length > 0)
        {
            return;
        }

        string[] candidates =
        [
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe"),
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"OpenRGB\OpenRGB.exe")
        ];
        string? executable = candidates.FirstOrDefault(System.IO.File.Exists);
        if (executable is null)
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
        {
            Arguments = "--server --startminimized",
            UseShellExecute = true
        });
        // Give device enumeration a moment before the first SDK negotiation.
        await Task.Delay(TimeSpan.FromSeconds(8));
    }

    private void EnsureServiceWritesAvailable()
    {
        if (!CanUseServiceWrites)
        {
            throw new InvalidOperationException(ServiceCompatibilityMessage);
        }
    }

    private async Task ProbeDynamicLightingCoreAsync()
    {
        BusyMessage = "Enumerating Windows Dynamic Lighting devices";
        IsBusy = true;
        try
        {
            IReadOnlyList<DynamicLightingDevice> devices = await DynamicLightingBridge.ProbeAsync(_lifetime.Token);
            Replace(DynamicLightingDevices, devices);
            DynamicLightingDevice? matchingDevice = devices.FirstOrDefault(device => device.Id == SelectedDynamicLightingDevice?.Id);
            SelectedDynamicLightingDevice = matchingDevice ?? (devices.Count == 0 ? null : devices[0]);
            DynamicLightingStatus = devices.Count == 0
                ? "Windows reported no LampArray-compatible devices."
                : $"Windows reported {devices.Count} Dynamic Lighting device(s) and {devices.Sum(device => device.LampCount)} lamps.";
            RebuildRgbRouteAssessments();
            OnPropertyChanged(nameof(DynamicLightingDeviceCount));
            _applyDynamicLightingSceneCommand.RaiseCanExecuteChanged();
            ShowNotice(DynamicLightingStatus, devices.Count == 0 ? "Info" : "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProbeOpenRgbCoreAsync()
    {
        if (!OpenRgbEnabled)
        {
            throw new InvalidOperationException("Enable the OpenRGB bridge before connecting.");
        }

        if (HasLightingConflict)
        {
            throw new InvalidOperationException(LightingConflictReason);
        }

        BusyMessage = "Negotiating with the local OpenRGB SDK server";
        IsBusy = true;
        try
        {
            await EnsureOpenRgbServerRunningAsync();
            OpenRgbSdkClient client = new();
            OpenRgbConnectionResult result = await client.ProbeAsync(_lifetime.Token);
            SetOpenRgbControllers(result.Controllers);
            OpenRgbConnected = true;
            OpenRgbStatus = result.Message;
            ShowNotice(result.Message, "Success");
        }
        catch (Exception exception)
        {
            OpenRgbConnected = false;
            SetOpenRgbControllers([]);
            OpenRgbStatus = $"Connection failed: {exception.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyOpenRgbCoreAsync(bool turnOff)
    {
        if (!OpenRgbEnabled || !OpenRgbConnected)
        {
            throw new InvalidOperationException("Connect to the local OpenRGB SDK server first.");
        }

        if (HasLightingConflict)
        {
            throw new InvalidOperationException(LightingConflictReason);
        }

        if (!TryParseOpenRgbInputs(out string colour, out int brightness))
        {
            throw new InvalidOperationException("Use a #RRGGBB colour and brightness from 0 to 100%.");
        }

        if (turnOff)
        {
            brightness = 0;
        }

        BusyMessage = turnOff ? "Turning OpenRGB lighting off" : "Applying OpenRGB lighting";
        IsBusy = true;
        try
        {
            OpenRgbSdkClient client = new();
            OpenRgbConnectionResult result = turnOff || SelectedColourway is null or { Id: "static" }
                ? await client.SetStaticColourAsync(colour, brightness, _lifetime.Token)
                : await client.SetColourwayAsync(SelectedColourway.Id, colour, brightness, _lifetime.Token);
            SetOpenRgbControllers(result.Controllers);
            OpenRgbConnected = true;
            OpenRgbStatus = turnOff
                ? $"Lighting off on {result.Controllers.Count} controller(s)."
                : result.Message;
            ShowNotice(OpenRgbStatus, "Success");
        }
        catch (Exception exception)
        {
            OpenRgbConnected = false;
            SetOpenRgbControllers([]);
            OpenRgbStatus = $"Lighting update failed: {exception.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryParseOpenRgbInputs(out string colour, out int brightness)
    {
        colour = OpenRgbColour.Trim();
        string hex = colour.TrimStart('#');
        bool validColour = hex.Length == 6
            && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _);
        bool validBrightness = int.TryParse(
            OpenRgbBrightnessText,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.CurrentCulture,
            out brightness)
            && brightness is >= 0 and <= 100;
        if (validColour && !colour.StartsWith('#'))
        {
            colour = $"#{colour}";
        }

        return validColour && validBrightness;
    }

    private void SetOpenRgbControllers(IEnumerable<OpenRgbController> controllers)
    {
        _openRgbControllers.Clear();
        _openRgbControllers.AddRange(controllers
            .GroupBy(controller => controller.Id)
            .Select(group => group.First())
            .OrderBy(controller => controller.Name, StringComparer.OrdinalIgnoreCase));
        OpenRgbControllerCount = _openRgbControllers.Count;
        RebuildRgbRouteAssessments();
    }

    private void RebuildRgbRouteAssessments()
    {
        IReadOnlyList<RgbBridgeEndpoint> dynamicLighting = DynamicLightingDevices
            .Select(device => new RgbBridgeEndpoint(
                device.Id,
                device.Name,
                ResolveRgbFamilyLabel(device.Name),
                device.LampCount,
                device.IsEnabled,
                device.Kind))
            .ToArray();
        IReadOnlyList<RgbBridgeEndpoint> openRgb = _openRgbControllers
            .Select(controller => new RgbBridgeEndpoint(
                controller.Id.ToString(CultureInfo.InvariantCulture),
                controller.Name,
                ResolveRgbFamilyLabel(controller.Name),
                controller.LedCount,
                IsEnabled: true,
                "Enumerated by the local OpenRGB SDK."))
            .ToArray();
        Replace(
            RgbRouteAssessments,
            RgbRoutingPolicy.Assess(
                _snapshot,
                dynamicLighting,
                openRgb,
                OpenRgbEnabled,
                OpenRgbConnected));
        NotifyRgbRoutingProperties();
    }

    private static string? ResolveRgbFamilyLabel(string name)
    {
        HardwareCompatibilityMatch match = HardwareCompatibilityCatalog.ClassifyPeripheral(null, name, null);
        return match.IsRecognized ? match.DisplayName : null;
    }

    /// <summary>
    /// NVML publishes read-only informational cards (per-fan telemetry, the
    /// write-transport feasibility card, the power-limit bounds card) alongside
    /// the actual writable channels. On the working Cooling/Performance pages
    /// they read as broken "Read Only" duplicates of controls that work, so
    /// they are hidden there whenever the corresponding writable channel
    /// exists. The full evidence set stays visible in the Devices decision
    /// matrix.
    /// </summary>
    private static bool IsInformationalDuplicate(
        CapabilityDescriptor capability,
        IReadOnlyList<CapabilityDescriptor> all)
    {
        if (capability.Id.StartsWith("nvml.fan", StringComparison.Ordinal))
        {
            return all.Any(other => other.Id.StartsWith("gpufan.duty:", StringComparison.Ordinal));
        }

        if (capability.Id.StartsWith("nvml.power-limit", StringComparison.Ordinal))
        {
            return all.Any(other => other.Id.StartsWith("gpupower.limit:", StringComparison.Ordinal));
        }

        return false;
    }
}
