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

public sealed record OperationTargetDisplay(
    CapabilityDescriptor Descriptor,
    string DisplayName,
    string State,
    string Range,
    string Reason,
    string? RpmSensorId,
    bool IsAvailable,
    bool IsExperimental)
{
    public static OperationTargetDisplay From(
        CapabilityDescriptor capability,
        string deviceName,
        string? rpmSensorId)
    {
        string range = capability.Range is NumericRange numeric
            ? $"{numeric.Minimum:0.##}–{numeric.Maximum:0.##} {capability.Unit}".TrimEnd()
            : "No numeric range";
        bool available = capability.State is CapabilityAccessState.Verified or CapabilityAccessState.Experimental
            && capability.CanResetToDefault
            && capability.Range is not null;
        return new OperationTargetDisplay(
            capability,
            $"{capability.Name} · {deviceName}",
            SplitWords(capability.State.ToString()),
            range,
            capability.Reason,
            rpmSensorId,
            available,
            capability.State == CapabilityAccessState.Experimental);
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record AutomationRuleDisplay(
    AutomationRuleV1 Rule,
    string Name,
    string Trigger,
    string Profile,
    string Priority,
    string Status)
{
    public static AutomationRuleDisplay From(AutomationRuleV1 rule) => new(
        rule,
        rule.Name,
        $"{SplitWords(rule.TriggerKind.ToString())} · {rule.TriggerValue}",
        rule.ProfileId,
        rule.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
        rule.Enabled ? "Enabled" : "Disabled");

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record SensorDisplay(string Name, string Device, string DisplayValue, string Severity, string Glyph);

public sealed record HidDeviceDisplay(string ProductName, string Identity, string Classes);

public sealed record ProfileCardDisplay(
    ProfileV1 Profile,
    string Name,
    string Description,
    string Objective,
    string Glyph,
    string ActionSummary,
    string StatusLabel,
    bool IsActive,
    bool IsExperimental,
    bool RequiresManualAcknowledgement)
{
    public static ProfileCardDisplay From(ProfileV1 profile, bool active, ProfileV2? suiteProfile = null)
    {
        (string objective, string glyph) = profile.Id.ToLowerInvariant() switch
        {
            "quiet" => ("Lower acoustic target", "\uE708"),
            "performance" => ("Prioritise sustained output", "\uE945"),
            _ => ("Everyday efficiency", "\uE9D2")
        };
        int manualOnlyCount = suiteProfile?.ManualOnlyActionIds.Count ?? 0;
        bool hasCoolingGraph = !string.IsNullOrWhiteSpace(suiteProfile?.CoolingGraphId);
        List<string> bundleParts = [];
        if (profile.Actions.Count > 0)
        {
            bundleParts.Add($"{profile.Actions.Count} typed action{(profile.Actions.Count == 1 ? string.Empty : "s")}");
        }
        if (hasCoolingGraph)
        {
            bundleParts.Add("continuous cooling graph");
        }
        if (!string.IsNullOrWhiteSpace(suiteProfile?.LightingSceneId))
        {
            bundleParts.Add("user-session lighting scene");
        }
        if (!string.IsNullOrWhiteSpace(suiteProfile?.OsdLayoutId))
        {
            bundleParts.Add("OSD layout");
        }
        if (manualOnlyCount > 0)
        {
            bundleParts.Add($"{manualOnlyCount} Manual Only");
        }
        string actionSummary = bundleParts.Count == 0
            ? "No hardware or companion actions in this build"
            : string.Join(" · ", bundleParts);
        return new ProfileCardDisplay(
            profile,
            profile.Name,
            profile.Description,
            objective,
            glyph,
            actionSummary,
            active ? "Active" : manualOnlyCount > 0 ? "Manual only" : profile.IsExperimental ? "Experimental" : "Stock-safe",
            active,
            profile.IsExperimental,
            manualOnlyCount > 0);
    }
}

public sealed record DeviceDisplay(
    string Id,
    string Name,
    string Kind,
    string Manufacturer,
    string Model,
    string Details,
    string Glyph,
    string? CompatibilityLabel,
    string SearchText)
{
    public static DeviceDisplay From(HardwareDevice device)
    {
        string manufacturer = string.IsNullOrWhiteSpace(device.Manufacturer) ? "Unknown manufacturer" : device.Manufacturer;
        string model = string.IsNullOrWhiteSpace(device.Model) ? "Model not reported" : device.Model;
        device.Properties.TryGetValue("compatibilityLabel", out string? compatibilityLabel);
        device.Properties.TryGetValue("boardPartnerLabel", out string? boardPartnerLabel);
        string details = string.IsNullOrWhiteSpace(compatibilityLabel)
            ? $"{manufacturer} \u00B7 {model}"
            : $"{manufacturer} \u00B7 {model} · {compatibilityLabel}";
        if (!string.IsNullOrWhiteSpace(boardPartnerLabel))
        {
            details = string.Concat(details, " \u00B7 ", boardPartnerLabel);
        }
        string glyph = device.Kind switch
        {
            DeviceKind.Cpu => "\uE950",
            DeviceKind.Gpu => "\uE7F4",
            DeviceKind.Motherboard or DeviceKind.Bios => "\uE950",
            DeviceKind.Memory => "\uE964",
            DeviceKind.Storage => "\uEDA2",
            DeviceKind.Network => "\uE968",
            DeviceKind.Cooling => "\uE9CA",
            DeviceKind.Lighting => "\uE706",
            DeviceKind.OperatingSystem => "\uE782",
            _ => "\uE772"
        };
        return new DeviceDisplay(
            device.Id,
            device.Name,
            SplitWords(device.Kind.ToString()),
            manufacturer,
            model,
            details,
            glyph,
            compatibilityLabel,
            $"{device.Name} {device.Kind} {manufacturer} {model} {compatibilityLabel} {string.Join(' ', device.Properties.Values)}");
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record CapabilityDisplay(
    CapabilityAccessState AccessState,
    string Name,
    string State,
    string Reason,
    string Range,
    string Evidence,
    string Domain,
    string Risk,
    string Owner,
    string NextSafeStep,
    string StateTone)
{
    // The capability lists are rebuilt on every snapshot refresh, which
    // recreates these display records. The details-expander state must survive
    // that churn or an open expander snaps shut within a second, so it is
    // persisted per capability ID for the lifetime of the process.
    private static readonly HashSet<string> ExpandedDetailIds = [];

    /// <summary>Stable capability identity used to persist UI state across refreshes.</summary>
    public string Id { get; init; } = string.Empty;

    public bool IsDetailsExpanded
    {
        get
        {
            lock (ExpandedDetailIds)
            {
                return ExpandedDetailIds.Contains(Id);
            }
        }
        set
        {
            lock (ExpandedDetailIds)
            {
                if (value)
                {
                    ExpandedDetailIds.Add(Id);
                }
                else
                {
                    ExpandedDetailIds.Remove(Id);
                }
            }
        }
    }

    public static CapabilityDisplay From(CapabilityDescriptor capability)
    {
        string range = capability.Range is NumericRange numeric
            ? $"{numeric.Minimum:0.##}\u2013{numeric.Maximum:0.##} {capability.Unit}".TrimEnd()
            : capability.ValueKind.ToString();
        string owner = string.IsNullOrWhiteSpace(capability.ConflictOwner)
            ? "No competing writer detected"
            : $"Blocked by {capability.ConflictOwner}";
        string nextSafeStep = GetNextSafeStep(capability);
        // Blocked gets its own calm tone rather than sharing Critical with
        // Faulted: a competing writer is a normal, recoverable situation
        // (informational), and red stays reserved for genuine faults so it
        // keeps its meaning.
        string tone = capability.State switch
        {
            CapabilityAccessState.Verified => "Safe",
            CapabilityAccessState.Experimental => "Warning",
            CapabilityAccessState.Blocked => "Blocked",
            CapabilityAccessState.Faulted => "Critical",
            _ => "Neutral"
        };
        // Friendlier state labels: an armable-but-disarmed control reads as
        // "Off" rather than bureaucratic "Read Only"; an armed Experimental
        // control reads as "On". The underlying AccessState is unchanged.
        // "Off"/"On" only for controls the Hardware-control switch can actually
        // arm; permanently informational read-only cards keep "Read Only" so
        // they do not masquerade as switches.
        bool armable = capability.Id.StartsWith("gpufan.", StringComparison.Ordinal)
            || capability.Id.StartsWith("gpupower.", StringComparison.Ordinal)
            || capability.Id.StartsWith("gpuclock.", StringComparison.Ordinal);
        string stateLabel = capability.State switch
        {
            CapabilityAccessState.ReadOnly when armable => "Off",
            CapabilityAccessState.Experimental when armable => "On",
            _ => SplitWords(capability.State.ToString())
        };
        return new CapabilityDisplay(
            capability.State,
            capability.Name,
            stateLabel,
            capability.Reason,
            range,
            SplitWords(capability.Evidence.ToString()),
            SplitWords(capability.Domain.ToString()),
            capability.Risk.ToString(),
            owner,
            nextSafeStep,
            tone)
        {
            Id = capability.Id
        };
    }

    private static string GetNextSafeStep(CapabilityDescriptor capability) => capability.State switch
    {
        CapabilityAccessState.Verified => capability.CanResetToDefault
            ? "Use only within the published bounds; firmware/default reset is available."
            : "Use only within the published bounds; reset evidence is still limited.",
        CapabilityAccessState.Experimental => "Keep this control manual and exact-device scoped until apply, read-back, reset, and fault-screening evidence passes.",
        CapabilityAccessState.Blocked when !string.IsNullOrWhiteSpace(capability.ConflictOwner) =>
            "Resolve the named competing writer through the ownership workflow. Never terminate a process by name alone.",
        CapabilityAccessState.Blocked => "Resolve the stated driver, firmware, bounds, or reset gate before any write can be considered.",
        CapabilityAccessState.ReadOnly => "Telemetry and inventory are available; no reviewed write endpoint is published for this exact device.",
        CapabilityAccessState.Unsupported => "No supported adapter path exists for this exact device and software version.",
        CapabilityAccessState.Faulted => "Use recovery and diagnostics, restore firmware/default control, then collect a new exact-device trace.",
        _ => "Review the capability evidence before changing hardware state."
    };

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

/// <summary>
/// Presentation model for the Advanced Lab Experimental Control Center. It
/// makes the current gates explicit without promoting an inventory item to a
/// write capability. Only bounded, resettable, non-protected motherboard-fan
/// outputs can be routed into the existing commissioning wizard.
/// </summary>
public sealed record ExperimentalControlDisplay(
    CapabilityDescriptor Descriptor,
    string Name,
    string Device,
    string Domain,
    string Range,
    string Evidence,
    string Path,
    string Readiness,
    string NextSafeStep,
    bool IsCoolingControl,
    bool IsGpuCoolingControl,
    bool IsProtected,
    bool CanOpenCoolingCommissioning,
    string Tone)
{
    public static ExperimentalControlDisplay From(
        CapabilityDescriptor capability,
        string deviceName,
        CoolingOutputAssignmentV1? assignment,
        bool serviceWritePathReady,
        bool sessionAcknowledged)
    {
        bool cooling = capability.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
            || capability.Name.Contains("fan", StringComparison.OrdinalIgnoreCase)
            || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase);
        bool gpuCooling = cooling
            && (capability.Name.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || capability.DeviceId.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || capability.AdapterId.Contains("gpu", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("geforce", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("radeon", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("arc", StringComparison.OrdinalIgnoreCase));
        bool protectedByRole = CoolingOutputAssignmentPolicy.IsProtected(assignment, capability);
        bool protectedByName = capability.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || capability.Name.Contains("pump", StringComparison.OrdinalIgnoreCase);
        bool protectedOutput = protectedByRole || protectedByName;
        bool boundedResetPath = capability.ValueKind == ControlValueKind.Numeric
            && capability.Range is not null
            && capability.CanResetToDefault;
        bool competingWriter = !string.IsNullOrWhiteSpace(capability.ConflictOwner);
        bool canCommission = capability.State == CapabilityAccessState.Experimental
            && cooling
            && !gpuCooling
            && !protectedOutput
            && !competingWriter
            && boundedResetPath;
        string range = capability.Range is NumericRange numeric
            ? $"{numeric.Minimum:0.##}\u2013{numeric.Maximum:0.##} {capability.Unit}".TrimEnd()
            : "No numeric range";
        string path = protectedOutput
            ? "Safety protected"
            : gpuCooling
                ? "GPU validation"
                : canCommission
                    ? "Header commissioning"
                    : "Evidence required";
        string readiness = protectedOutput
            ? $"Protected as {SplitWords(assignment?.Role.ToString() ?? "CPU/Pump")}; commissioning and fan-stop remain unavailable."
            : gpuCooling
                ? "GPU fan validation is separate from chassis-header commissioning; the configured or controller-reported minimum remains in force."
                : competingWriter
                    ? $"Blocked by {capability.ConflictOwner}; resolve ownership before any write workflow."
                    : !boundedResetPath
                        ? "This adapter does not publish the bounded reset path required for commissioning."
                        : !serviceWritePathReady
                            ? "Service write path is not ready. Evidence can be reviewed, but commissioning is unavailable."
                            : !sessionAcknowledged
                                ? "Session acknowledgement required; no hardware command has been authorised."
                                : "Ready to select in Cooling. Exact-device confirmation, a physical header, RPM pairing, and a witnessed pulse are still required.";
        string nextSafeStep = protectedOutput
            ? "Keep the current safety role. A pump or CPU-fan output cannot use this commissioning path."
            : gpuCooling
                ? "Keep the configured or controller-reported GPU fan minimum. Complete repeated direct restart validation before any lower minimum is considered."
                : competingWriter
                    ? "Review the exact competing writer in Devices; never terminate a process by name alone."
                    : !boundedResetPath
                        ? "Collect adapter evidence with bounds, read-back, and reset behaviour before adding a write path."
                    : "Open Cooling, select this exact control, enter its physical chassis header, and begin setup. No hardware command is sent by this selection.";
        string tone = protectedOutput ? "Critical" : canCommission ? "Warning" : "Neutral";
        return new ExperimentalControlDisplay(
            capability,
            capability.Name,
            deviceName,
            SplitWords(capability.Domain.ToString()),
            range,
            SplitWords(capability.Evidence.ToString()),
            path,
            readiness,
            nextSafeStep,
            cooling,
            gpuCooling,
            protectedOutput,
            canCommission,
            tone);
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record DiagnosticDisplay(string Title, string Message, string Severity, string Remediation, string Glyph)
{
    public static DiagnosticDisplay From(DiagnosticWarning warning) => new(
        warning.Code,
        warning.Message,
        NormaliseSeverity(warning.Severity),
        warning.Remediation ?? "Review the Devices page for capability evidence.",
        warning.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ? "\uEA39" : "\uE7BA");

    public static DiagnosticDisplay From(ConflictDescriptor conflict) => new(
        $"{conflict.DisplayName} is running",
        $"Overlapping control families: {string.Join(", ", conflict.ResourceFamilies)}.",
        "Warning",
        conflict.Guidance,
        "\uE7BA");

    public static int Rank(DiagnosticDisplay item) => item.Severity switch
    {
        "Critical" => 0,
        "Warning" => 1,
        _ => 2
    };

    private static string NormaliseSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" or "error" => "Critical",
        "warning" or "warn" => "Warning",
        _ => "Info"
    };
}

public sealed record AdapterHealthDisplay(string Name, string Status, string Message, string Checked, bool Healthy)
{
    public static AdapterHealthDisplay From(AdapterHealth health) => new(
        health.AdapterId,
        health.Healthy ? "Healthy" : "Needs attention",
        health.Message,
        health.CheckedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        health.Healthy);
}

public sealed record SensorTrendDisplay(
    SensorTrendV1 Trend,
    string SensorId,
    string DisplayName,
    string Unit,
    string Latest,
    string Range,
    string Sparkline,
    bool IsPinned)
{
    public static SensorTrendDisplay From(SensorTrendV1 trend) => new(
        trend,
        trend.SensorId,
        trend.DisplayName,
        trend.Unit,
        trend.Latest is double latest
            ? $"{latest:0.##} {trend.Unit}".TrimEnd()
            : "Unavailable",
        trend.Minimum is double minimum && trend.Maximum is double maximum
            ? $"{minimum:0.##}–{maximum:0.##} {trend.Unit}".TrimEnd()
            : "No range",
        trend.Sparkline,
        false);

    public SensorTrendDisplay WithPinned(bool pinned) => this with { IsPinned = pinned };
}

/// <summary>
/// Presentation-only normalized series for the dashboard comparison workspace.
/// It preserves the actual value/range in the legend so unrelated units are
/// never represented as directly comparable magnitudes.
/// </summary>
public sealed record SensorComparisonSeriesDisplay(
    string SensorId,
    string DisplayName,
    string Latest,
    string Range,
    string Unit,
    WpfPointCollection Points,
    WpfBrush Stroke,
    IReadOnlyList<double> Values)
{
    private static readonly WpfColor[] Palette =
    [
        WpfColor.FromRgb(0x68, 0xB0, 0xFF),
        WpfColor.FromRgb(0x5B, 0xD6, 0xB3),
        WpfColor.FromRgb(0xE4, 0xB7, 0x5B),
        WpfColor.FromRgb(0xC2, 0x8C, 0xFF)
    ];

    public static SensorComparisonSeriesDisplay From(SensorTrendDisplay trend, int index)
    {
        SensorTrendPointV1[] source = trend.Trend.Points.ToArray();
        double minimum = trend.Trend.Minimum ?? (source.Length == 0 ? 0 : source.Min(point => point.Value));
        double maximum = trend.Trend.Maximum ?? (source.Length == 0 ? 0 : source.Max(point => point.Value));
        WpfPointCollection points = BuildPoints(source, minimum, maximum);
        WpfSolidColorBrush brush = new(Palette[index % Palette.Length]);
        brush.Freeze();
        return new SensorComparisonSeriesDisplay(
            trend.SensorId,
            trend.DisplayName,
            trend.Latest,
            trend.Range,
            trend.Unit,
            points,
            brush,
            source.Select(point => point.Value).ToArray());
    }

    private static WpfPointCollection BuildPoints(SensorTrendPointV1[] source, double minimum, double maximum)
    {
        const double width = 320;
        const double top = 12;
        const double height = 96;
        if (source.Length == 0)
        {
            return [];
        }

        double ToY(double value)
        {
            double ratio = maximum > minimum
                ? Math.Clamp((value - minimum) / (maximum - minimum), 0, 1)
                : 0.5;
            return top + (1 - ratio) * height;
        }

        if (source.Length == 1)
        {
            double y = ToY(source[0].Value);
            return [new WpfPoint(0, y), new WpfPoint(width, y)];
        }

        WpfPointCollection points = [];
        for (int index = 0; index < source.Length; index++)
        {
            double x = width * index / (source.Length - 1d);
            points.Add(new WpfPoint(x, ToY(source[index].Value)));
        }
        return points;
    }
}

public sealed record HealthRuleDisplay(
    HealthRuleV1 Rule,
    string Name,
    string Condition,
    string Action,
    string Source,
    string Summary)
{
    public static HealthRuleDisplay From(HealthRuleV1 rule)
    {
        string threshold = rule.Threshold is double value ? $" {value:0.##}" : string.Empty;
        string source = string.IsNullOrWhiteSpace(rule.SensorId) ? "System event" : rule.SensorId;
        return new HealthRuleDisplay(
            rule,
            rule.Name,
            SplitWords(rule.Condition.ToString()),
            SplitWords(rule.Action.ToString()),
            source,
            $"{rule.ConsecutiveObservations} consecutive observation(s), {rule.Cooldown.TotalSeconds:0} s cooldown{threshold}.");
    }

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record HealthAlertDisplay(
    HealthAlertEventV1 Alert,
    string RuleName,
    string Message,
    string State,
    string Action,
    string Timestamp,
    string Tone,
    bool CanAcknowledge)
{
    public static HealthAlertDisplay From(HealthAlertEventV1 alert) => new(
        alert,
        alert.RuleName,
        alert.Message,
        alert.State.ToString(),
        alert.ActionResult ?? SplitWords(alert.RequestedAction.ToString()),
        alert.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        alert.State == HealthAlertState.Cleared ? "Safe" : "Warning",
        alert.State == HealthAlertState.Active);

    private static string SplitWords(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1");
}

public sealed record TimelineEventDisplay(
    DateTimeOffset When,
    string Source,
    string Title,
    string Message,
    string Tone)
{
    public string Timestamp => When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static TimelineEventDisplay From(HealthAlertDisplay alert) => new(
        alert.Alert.UpdatedAt,
        "Health",
        alert.RuleName,
        alert.Message,
        alert.Tone);

    public static TimelineEventDisplay From(AdapterTraceDisplay trace) => new(
        DateTimeOffset.Now,
        "Adapter",
        $"{trace.Adapter}: {trace.Operation}",
        trace.Message,
        trace.Success ? "Info" : "Warning");
}

public sealed record AdapterTraceDisplay(
    string Adapter,
    string Operation,
    string Target,
    string Status,
    string Message,
    string Timestamp,
    bool Success)
{
    public static AdapterTraceDisplay From(AdapterTraceEvent trace) => new(
        trace.AdapterId,
        trace.Operation,
        string.IsNullOrWhiteSpace(trace.CapabilityId) ? "Adapter" : trace.CapabilityId,
        trace.Success ? "Completed" : "Failed",
        trace.Message,
        trace.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        trace.Success);
}

internal sealed class AsyncCommand(
    Func<object?, Task> execute,
    Func<object?, bool>? canExecute = null,
    Action<Exception>? onError = null,
    Action<object?>? onBlocked = null) : ICommand
{
    private bool _executing;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_executing && (onBlocked is not null || (canExecute?.Invoke(parameter) ?? true));

    public async void Execute(object? parameter)
    {
        if (_executing)
        {
            return;
        }

        if (!(canExecute?.Invoke(parameter) ?? true))
        {
            onBlocked?.Invoke(parameter);
            return;
        }

        _executing = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter);
        }
        catch (Exception exception)
        {
            onError?.Invoke(exception);
        }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
