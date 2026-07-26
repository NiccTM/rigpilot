using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using PCHelper.Core;

namespace PCHelper.Adapters;

/// <summary>
/// GPU fan transport backed by NVIDIA NVAPI via the NvAPIWrapper library
/// (LGPL-3.0). NVAPI's cooler API is the standard NVIDIA fan-write path and
/// complements the hand-rolled NVML transport. Writes are gated identically to
/// <see cref="NvmlGpuFanCoolerTransport"/>: they proceed only when the instance
/// has been explicitly armed via an acknowledged operator action (or the env
/// override). Constructing or wiring this transport never issues a fan write.
/// </summary>
public sealed class NvApiGpuFanCoolerTransport : IGpuFanCoolerTransport, IDisposable
{
    public const string WriteOptInEnvironmentVariable = NvmlGpuFanCoolerTransport.WriteOptInEnvironmentVariable;
    private readonly object _gate = new();
    private readonly uint _gpuIndex;
    private PhysicalGPU _gpu;
    private bool _armed;
    private bool _disposed;

    private NvApiGpuFanCoolerTransport(PhysicalGPU gpu, uint gpuIndex)
    {
        _gpu = gpu;
        _gpuIndex = gpuIndex;
    }

    /// <summary>
    /// Initialises NVAPI and binds to the requested GPU index. Returns false when
    /// NVAPI, an NVIDIA GPU, or a controllable cooler is unavailable. No write occurs.
    /// </summary>
    public static bool TryCreate(uint gpuIndex, out NvApiGpuFanCoolerTransport transport, out string message)
    {
        transport = null!;
        try
        {
            NVIDIA.Initialize();
            PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
            if (gpuIndex >= gpus.Length)
            {
                message = $"NVAPI found {gpus.Length} GPU(s); index {gpuIndex} is out of range.";
                return false;
            }

            PhysicalGPU gpu = gpus[gpuIndex];
            if (!gpu.CoolerInformation.Coolers.Any())
            {
                message = "The NVIDIA GPU exposes no NVAPI cooler for control.";
                return false;
            }

            transport = new NvApiGpuFanCoolerTransport(gpu, gpuIndex);
            message = "NVAPI GPU fan transport was loaded.";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            message = $"NVAPI is unavailable: {exception.GetType().Name}.";
            return false;
        }
    }

    /// <summary>NVAPI cooler control is available while this instance is live.</summary>
    public bool CanWrite => !_disposed;

    /// <summary>Arms or disarms live writes. Set only after an acknowledged operator action.</summary>
    public void SetArmed(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
        }
    }

    public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken)
    {
        GPUCooler? cooler = FindCooler(channelId);
        if (cooler is null)
        {
            return Task.FromResult<GpuFanBounds?>(null);
        }

        int minimum = Math.Max((int)AdaptiveCoolingProfileFactory.UncalibratedFloorDutyPercent, cooler.CurrentMinimumLevel);
        int maximum = Math.Min(100, cooler.CurrentMaximumLevel);
        return Task.FromResult<GpuFanBounds?>(new GpuFanBounds(minimum, maximum));
    }

    public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken)
    {
        GPUCooler? cooler = FindCooler(channelId);
        if (cooler is null)
        {
            return Task.FromResult(new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null));
        }

        GpuFanControlPolicy policy = cooler.CurrentPolicy == CoolerPolicy.Manual
            ? GpuFanControlPolicy.Manual
            : GpuFanControlPolicy.Automatic;
        int? commanded = policy == GpuFanControlPolicy.Manual ? cooler.CurrentLevel : null;
        return Task.FromResult(new GpuFanChannelState(policy, commanded, cooler.CurrentLevel));
    }

    public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        GPUCooler cooler = FindCooler(channelId)
            ?? throw new GpuFanSafetyException($"NVAPI cooler '{channelId}' is unavailable.");
        _gpu.CoolerInformation.SetCoolerSettings(cooler.CoolerId, CoolerPolicy.Manual, dutyPercent);
        return Task.CompletedTask;
    }

    public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken)
    {
        EnsureWriteArmed();
        GPUCooler cooler = FindCooler(channelId)
            ?? throw new GpuFanSafetyException($"NVAPI cooler '{channelId}' is unavailable.");

        // Nothing to undo when the cooler is not under our manual control: the driver
        // already owns the fan. This matters because RestoreCoolerSettingsToDefault is
        // privilege-gated far more tightly than the SetCoolerSettings write path and is
        // refused outright on this RTX 3090 (NVAPI_INVALID_USER_PRIVILEGE). Arming reset
        // every family from a cold start, so it invoked that refused call purely to reach
        // the automatic state the cooler was already resting in — and the refusal failed
        // the arm. Confirming the observed policy is the honest form of this check: we
        // report the restore as satisfied only because the hardware is demonstrably at
        // its default, never merely because a call was skipped.
        if (cooler.CurrentPolicy != CoolerPolicy.Manual)
        {
            return Task.CompletedTask;
        }

        // Hand the cooler back to the driver. NVAPI exposes several ways to do this and
        // this RTX 3090 refuses some of them outright with NVAPI_INVALID_USER_PRIVILEGE
        // even from the service's privileged session — while the Manual write path stays
        // fully available. Which calls a card accepts is therefore a per-card, per-driver
        // fact we cannot know ahead of time, so try each in turn and keep the first that
        // is accepted. Every refusal is recorded with its NVAPI status so a card that
        // rejects the lot reports exactly what it rejected rather than a bare exception
        // type, which is identical for every NVAPI fault and says nothing.
        List<string> refusals = [];
        foreach ((string description, Action attempt) in BuildRestoreStrategies(cooler))
        {
            try
            {
                attempt();
                return Task.CompletedTask;
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                refusals.Add($"{description} -> {DescribeNvApiFailure(exception)}");
            }
        }

        throw new GpuFanSafetyException(
            $"NVAPI refused every cooler restore path for '{channelId}': {string.Join("; ", refusals)}.");
    }

    /// <summary>
    /// The restore calls to try, in order of preference: the purpose-built restore first,
    /// then the SetCoolerSettings overloads that ask for the cooler's own reported default
    /// policy. The level-carrying overload is tried as well as the policy-only one because
    /// they reach NVAPI differently, and a card that refuses one can still accept the other.
    /// </summary>
    private IEnumerable<(string Description, Action Attempt)> BuildRestoreStrategies(GPUCooler cooler)
    {
        GPUCoolerInformation coolers = _gpu.CoolerInformation;
        int coolerId = cooler.CoolerId;
        CoolerPolicy defaultPolicy = cooler.DefaultPolicy;

        yield return ("RestoreCoolerSettingsToDefault", coolers.RestoreCoolerSettingsToDefault);
        yield return (
            $"RestoreCoolerSettingsToDefault(cooler {coolerId})",
            () => coolers.RestoreCoolerSettingsToDefault([coolerId]));
        yield return (
            $"SetCoolerSettings(policy={defaultPolicy})",
            () => coolers.SetCoolerSettings(coolerId, defaultPolicy));
        yield return (
            $"SetCoolerSettings(policy={defaultPolicy}, level={cooler.DefaultMinimumLevel})",
            () => coolers.SetCoolerSettings(coolerId, defaultPolicy, cooler.DefaultMinimumLevel));
        yield return ("SetClientFanCoolersControl(Auto)", RestoreViaClientFanCoolers);
        yield return ("NVAPI session release", ReleaseNvApiSessionToReclaimFan);
    }

    // NOTE (2026-07-20): a RefreshGpuHandle() step was added here on the theory that the
    // service's startup-cached PhysicalGPU handle goes stale and NVAPI reports that as
    // NVAPI_INVALID_USER_PRIVILEGE. It was built, deployed and re-tested against the exact
    // failing scenario, and the refusal was identical -- the service's handle was three
    // minutes old at the time. The theory is disproven and the code was reverted rather than
    // left in place looking like a fix. Do not re-add it without new evidence.

    /// <summary>
    /// Last resort: drop the NVAPI session so the driver reclaims the fan, then rebind.
    ///
    /// This driver permits taking manual control but refuses every documented way of giving
    /// it back — all four cooler restore calls and the client fan-cooler write return
    /// NVAPI_INVALID_USER_PRIVILEGE from the same privileged session that just wrote a manual
    /// duty successfully. Releasing the session is the one action observed to actually return
    /// the fan to the driver curve, so it is expressed here as a deliberate restore step
    /// rather than something only a service restart happens to achieve.
    ///
    /// The unload is process-global, so the handle every NVAPI transport caches goes stale.
    /// This rebinds its own; callers holding other transports must rebind theirs. It runs
    /// only after every in-session path has been refused, and only inside the hardware
    /// mutation gate, so no other NVAPI work is in flight.
    /// </summary>
    private void ReleaseNvApiSessionToReclaimFan()
    {
        NVIDIA.Unload();
        NVIDIA.Initialize();

        PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
        if (_gpuIndex >= gpus.Length)
        {
            throw new GpuFanSafetyException(
                $"NVAPI was released but GPU index {_gpuIndex} did not reappear; {gpus.Length} GPU(s) enumerated.");
        }

        _gpu = gpus[_gpuIndex];

        GPUCooler? reclaimed = FindCooler("0");
        if (reclaimed is { CurrentPolicy: CoolerPolicy.Manual })
        {
            throw new GpuFanSafetyException(
                "NVAPI session release completed but the cooler is still under manual control.");
        }
    }

    /// <summary>
    /// Read-only probe of the client fan-cooler interface, reporting each cooler's control
    /// mode or the NVAPI status that refused the read. Diagnostics only; it never writes.
    /// </summary>
    public string DescribeClientFanCoolerControl()
    {
        try
        {
            PrivateFanCoolersControlV1 control = GPUApi.GetClientFanCoolersControl(_gpu.Handle);
            IEnumerable<string> modes = control.FanCoolersControlEntries
                .Select(entry => $"cooler {entry.CoolerId}={entry.ControlMode}");
            return $"readable: {string.Join(", ", modes)}";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return $"refused: {DescribeNvApiFailure(exception)}";
        }
    }

    /// <summary>
    /// Hands every fan back to the driver through the client fan-cooler interface, the
    /// generation of the NVAPI cooler API that Ampere and later cards actually implement.
    ///
    /// NvAPIWrapper already falls back to this path internally, but only when the legacy
    /// call reports <c>NotSupported</c>. This RTX 3090 instead refuses the legacy restore
    /// with <c>InvalidUserPrivilege</c> — a misleading status, since the service holds a
    /// fully privileged session and legacy *manual* writes on the same session succeed.
    /// That status never matches the wrapper's fallback condition, so the working modern
    /// call was never reached and the fan could be driven but never given back. Calling it
    /// directly is what closes that gap.
    ///
    /// Every entry is set to Auto rather than just this channel's cooler: handing the card
    /// back to its own curve is only true if no fan is left under manual control.
    /// </summary>
    private void RestoreViaClientFanCoolers()
    {
        PrivateFanCoolersControlV1 currentControl = GPUApi.GetClientFanCoolersControl(_gpu.Handle);
        PrivateFanCoolersControlV1 automaticControl = new(
            currentControl.FanCoolersControlEntries
                .Select(entry => entry.ControlMode == FanCoolersControlMode.Auto
                    ? entry
                    : new PrivateFanCoolersControlV1.FanCoolersControlEntry(
                        entry.CoolerId,
                        FanCoolersControlMode.Auto))
                .ToArray(),
            currentControl.UnknownUInt);
        GPUApi.SetClientFanCoolersControl(_gpu.Handle, automaticControl);

        // Confirm the write actually landed instead of trusting that it returned without
        // throwing. A restore that reports success but leaves the cooler in manual control
        // is worse than one that fails loudly: the caller verifies the fan afterwards, sees
        // manual, and reports an unexplained default-state failure with nothing in the logs
        // pointing here. Re-reading turns that silent no-op into a named cause.
        PrivateFanCoolersControlV1 applied = GPUApi.GetClientFanCoolersControl(_gpu.Handle);
        string[] stillManual = applied.FanCoolersControlEntries
            .Where(entry => entry.ControlMode != FanCoolersControlMode.Auto)
            .Select(entry => $"cooler {entry.CoolerId}={entry.ControlMode}")
            .ToArray();
        if (stillManual.Length > 0)
        {
            throw new GpuFanSafetyException(
                "SetClientFanCoolersControl(Auto) was accepted but did not take effect: "
                + string.Join(", ", stillManual) + ".");
        }
    }

    /// <summary>
    /// Renders an NVAPI failure as its status code where one is available. The exception
    /// type is identical for every NVAPI fault, so the status is the only part that says
    /// what actually went wrong (e.g. NotSupported vs InvalidUserPrivilege).
    /// </summary>
    private static string DescribeNvApiFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NVIDIAApiException nvidiaException)
            {
                return $"{nvidiaException.Status} ({current.Message})";
            }
        }

        return exception.GetType().Name;
    }

    private GPUCooler? FindCooler(string channelId)
    {
        GPUCooler[] coolers = _gpu.CoolerInformation.Coolers.ToArray();
        if (coolers.Length == 0)
        {
            return null;
        }

        // channelId is a cooler index into the discovered coolers; default to the first.
        int index = int.TryParse(channelId.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out int parsed)
            ? parsed
            : 0;
        return index >= 0 && index < coolers.Length ? coolers[index] : coolers[0];
    }

    private void EnsureWriteArmed()
    {
        bool armed;
        lock (_gate)
        {
            armed = _armed;
        }

        if (!armed && Environment.GetEnvironmentVariable(WriteOptInEnvironmentVariable) != "1")
        {
            throw new GpuFanSafetyException(
                $"GPU fan writes require an acknowledged arm or the explicit operator opt-in ({WriteOptInEnvironmentVariable}=1).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            NVIDIA.Unload();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // NVAPI unload is best-effort; the process boundary is the real cleanup.
        }
    }
}
