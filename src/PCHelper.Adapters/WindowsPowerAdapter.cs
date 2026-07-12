using System.ComponentModel;
using System.Runtime.InteropServices;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

public sealed partial class WindowsPowerAdapter : IHardwareAdapter
{
    private const string CapabilityId = "windows.power.active-scheme";
    private static readonly Guid BalancedScheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private HashSet<Guid> _availableSchemes = [];

    public AdapterManifest Manifest { get; } = new(
        "windows.power",
        "Windows Power",
        "0.2.0",
        "GPL-3.0-only",
        null,
        AdapterExecutionContext.SystemService,
        ["Windows 10 22H2 x64", "Windows 11 x64"],
        ["PowerPlan"]);

    public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<DiagnosticWarning> warnings = [];
        CapabilityAccessState state;
        string reason;
        try
        {
            _availableSchemes = EnumerateSchemes().ToHashSet();
            Guid active = GetActiveScheme();
            _availableSchemes.Add(active);
            state = CapabilityAccessState.Verified;
            reason = "Windows exposes bounded power-scheme selection with read-back and rollback.";
        }
        catch (Exception exception)
        {
            state = CapabilityAccessState.Faulted;
            reason = exception.Message;
            warnings.Add(new DiagnosticWarning("POWER_API_FAILED", "Warning", exception.Message, "Use Windows Settings to manage power mode."));
        }

        HardwareDevice device = new(
            "windows:power",
            "Windows power policy",
            DeviceKind.OperatingSystem,
            "Microsoft",
            null,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["availableSchemeCount"] = _availableSchemes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        CapabilityDescriptor capability = new(
            CapabilityId,
            Manifest.Id,
            device.Id,
            "Active power scheme",
            state,
            AdapterExecutionContext.SystemService,
            ControlValueKind.Text,
            null,
            null,
            RiskLevel.Safe,
            state == CapabilityAccessState.Verified ? EvidenceLevel.ReadBackVerified : EvidenceLevel.None,
            null,
            reason,
            CanResetToDefault: true,
            Domain: ControlDomain.Power);
        return Task.FromResult(new AdapterProbeResult(Manifest, [device], [capability], warnings));
    }

    public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SensorSample>>([]);

    public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAction(action);
        Guid requested = ParseScheme(action.Value);
        if (!_availableSchemes.Contains(requested))
        {
            throw new InvalidOperationException($"Power scheme '{requested}' is not installed.");
        }

        Guid previous = GetActiveScheme();
        return Task.FromResult(new PreparedAction(
            action,
            ControlValue.FromText(previous.ToString("D")),
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N")));
    }

    public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetActiveScheme(ParseScheme(action.Action.Value));
        return Task.CompletedTask;
    }

    public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid expected = ParseScheme(action.Action.Value);
        Guid actual = GetActiveScheme();
        bool success = expected == actual;
        return Task.FromResult(new ActionVerification(
            action.Action.Id,
            success,
            ControlValue.FromText(actual.ToString("D")),
            success ? "Power scheme read-back matched." : $"Expected {expected}, observed {actual}."));
    }

    public Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action.PreviousValue is not null)
        {
            SetActiveScheme(ParseScheme(action.PreviousValue));
        }

        return Task.CompletedTask;
    }

    public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(capabilityId, CapabilityId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unknown capability.", nameof(capabilityId));
        }

        if (!_availableSchemes.Contains(BalancedScheme))
        {
            throw new InvalidOperationException("The standard Balanced power scheme is not installed.");
        }

        SetActiveScheme(BalancedScheme);
        return Task.CompletedTask;
    }

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            Guid active = GetActiveScheme();
            return Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, $"Active scheme: {active:D}", []));
        }
        catch (Exception exception)
        {
            return Task.FromResult(new AdapterHealth(Manifest.Id, false, DateTimeOffset.UtcNow, "Windows power API failed.", [exception.Message]));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void EnsureAction(ProfileAction action)
    {
        if (!string.Equals(action.CapabilityId, CapabilityId, StringComparison.Ordinal) || action.Value.Kind != ControlValueKind.Text)
        {
            throw new ArgumentException("The action is not a Windows power-scheme action.", nameof(action));
        }
    }

    private static Guid ParseScheme(ControlValue value) =>
        value.Text is string text && Guid.TryParse(text, out Guid scheme)
            ? scheme
            : throw new ArgumentException("A valid power-scheme GUID is required.", nameof(value));

    private static Guid GetActiveScheme()
    {
        int result = PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer);
        if (result != 0)
        {
            throw new Win32Exception(result, "PowerGetActiveScheme failed.");
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static void SetActiveScheme(Guid scheme)
    {
        int result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (result != 0)
        {
            throw new Win32Exception(result, "PowerSetActiveScheme failed.");
        }
    }

    private static IEnumerable<Guid> EnumerateSchemes()
    {
        const uint accessScheme = 16;
        for (uint index = 0; ; index++)
        {
            uint size = 16;
            byte[] buffer = new byte[size];
            uint result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, accessScheme, index, buffer, ref size);
            if (result == 259)
            {
                yield break;
            }

            if (result != 0)
            {
                throw new Win32Exception((int)result, "PowerEnumerate failed.");
            }

            yield return new Guid(buffer);
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial int PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subgroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        [Out] byte[] buffer,
        ref uint bufferSize);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
