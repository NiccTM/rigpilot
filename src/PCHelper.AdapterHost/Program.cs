using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

await using AdapterCoordinator coordinator = new([new LibreHardwareMonitorAdapter()]);
string pipeName = GetArgument("--pipe") ?? ProtocolConstants.AdapterHostPipeName;
string? sessionToken = GetArgument("--token")
    ?? Environment.GetEnvironmentVariable("PCHELPER_ADAPTER_HOST_TOKEN");
AdapterHostFailureV1? lastFailure = null;

if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
{
    HardwareSnapshot probe = await coordinator.CaptureAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(probe, JsonDefaults.Options));
    return;
}

if (args.Contains("--discover-controllers", StringComparer.OrdinalIgnoreCase))
{
    // This process IS the disposable controller-discovery child. If native HidSharp
    // code terminates it, the parent contains the abnormal exit; a managed failure is
    // reported as a contained result. Controllers here are read-only inventory only.
    ControllerDiscoveryResultV1 discovery = LibreHardwareMonitorAdapter.DiscoverControllersInProcess();
    Console.WriteLine(JsonSerializer.Serialize(discovery, JsonDefaults.Options));
    return;
}

if (args.Contains("--read-ryzen-smu", StringComparer.OrdinalIgnoreCase))
{
    // Disposable read-only Ryzen SMU feasibility child. Only read-class module
    // functions are referenced; a native PawnIO fault is contained by the parent.
    RyzenSmuFeasibilityV1 smu = RyzenSmuFeasibilityReader.Read();
    Console.WriteLine(JsonSerializer.Serialize(smu, JsonDefaults.Options));
    return;
}

if (args.Contains("--read-kraken", StringComparer.OrdinalIgnoreCase))
{
    // Disposable read-only Kraken X3 telemetry child. The firmware streams status
    // reports unsolicited, so this path performs no HID writes; a native fault is
    // contained by the parent exactly like the HID inventory child.
    KrakenTelemetryV1 kraken = KrakenX3TelemetryReader.Read();
    Console.WriteLine(JsonSerializer.Serialize(kraken, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-kraken-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Disposable Kraken X3 lighting child: writes one fixed-colour (or off)
    // lighting report to the sync channel. Lighting only — the pump/cooling
    // registers are untouched — and a native fault is contained by the parent.
    int argumentIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-kraken-rgb", StringComparison.OrdinalIgnoreCase));
    string value = argumentIndex >= 0 && argumentIndex + 1 < args.Length ? args[argumentIndex + 1] : string.Empty;
    bool off = string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    KrakenLightingResultV1 lighting = KrakenX3LightingWriter.Write(off ? string.Empty : value, off);
    Console.WriteLine(JsonSerializer.Serialize(lighting, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-aura-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Disposable AURA addressable lighting child: writes direct-mode static
    // colour (or off) frames to both headers of the ASUS AURA USB controller.
    // Lighting registers only, no EEPROM/save command, contained by the parent.
    int auraArgumentIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-aura-rgb", StringComparison.OrdinalIgnoreCase));
    string auraValue = auraArgumentIndex >= 0 && auraArgumentIndex + 1 < args.Length ? args[auraArgumentIndex + 1] : string.Empty;
    // Target forms: RRGGBB, off, RRGGBB@1, off@2 — the @N suffix drives one
    // header only (e.g. a passive ARGB GPU sag bracket on that header).
    AuraLightingResultV1 aura = AuraUsbLightingWriter.TryParseTarget(auraValue, out string auraColour, out bool auraOff, out int? auraHeader)
        ? AuraUsbLightingWriter.Write(auraColour, auraOff, auraHeader)
        : AuraLightingResultV1.Unavailable(KrakenLightingOutcome.Failed, $"'{auraValue}' is not RRGGBB, off, RRGGBB@1, or off@2.");
    Console.WriteLine(JsonSerializer.Serialize(aura, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-kraken-pump", StringComparer.OrdinalIgnoreCase))
{
    // Disposable Kraken X3 pump child: writes one fixed-duty (flat profile)
    // speed report to the pump channel and reads the firmware status stream
    // back. The duty is hard-clamped to [60, 100]% inside the writer — the
    // pump is never slowed below the safety floor and never stopped — and a
    // native fault is contained by the parent.
    int pumpArgumentIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-kraken-pump", StringComparison.OrdinalIgnoreCase));
    string pumpValue = pumpArgumentIndex >= 0 && pumpArgumentIndex + 1 < args.Length ? args[pumpArgumentIndex + 1] : string.Empty;
    KrakenPumpResultV1 pump = int.TryParse(pumpValue, out int pumpDuty)
        ? KrakenX3PumpWriter.Write(pumpDuty)
        : KrakenPumpResultV1.Unavailable(KrakenPumpOutcome.Failed, "--set-kraken-pump requires an integer duty percentage.");
    Console.WriteLine(JsonSerializer.Serialize(pump, JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-smbus-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Disposable, purely read-only ENE DIMM RGB detection child. Read-byte
    // transactions only, restricted to the RGB-controller address range
    // (0x70-0x77); no pointer, colour, or any other byte is written to the
    // bus. A native PawnIO fault is contained by the parent.
    SmbusRgbProbeResultV1 smbusProbe = SmbusRgbDetection.Probe();
    Console.WriteLine(JsonSerializer.Serialize(smbusProbe, JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-razer-usb", StringComparer.OrdinalIgnoreCase))
{
    // Read-only interface diagnostics for the audited Razer device: report
    // lengths and openability per HID interface. No report is written.
    Console.WriteLine(JsonSerializer.Serialize(RazerUsbRgbWriter.ProbeInterfaces(), JsonDefaults.Options));
    return;
}

if (args.Contains("--set-razer-usb-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Disposable native Razer USB lighting child: one extended-matrix static
    // command to the first audited Razer device, with the firmware status
    // reply read back. No firmware/profile/EEPROM command class is issued;
    // a native HID fault is contained by the parent.
    int razerIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-razer-usb-rgb", StringComparison.OrdinalIgnoreCase));
    string razerValue = razerIndex >= 0 && razerIndex + 1 < args.Length ? args[razerIndex + 1] : string.Empty;
    bool razerOff = string.Equals(razerValue, "off", StringComparison.OrdinalIgnoreCase);
    RazerRgbResultV1 razer = RazerUsbRgbWriter.Write(razerOff ? string.Empty : razerValue, razerOff);
    Console.WriteLine(JsonSerializer.Serialize(razer, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-razer-custom", StringComparer.OrdinalIgnoreCase))
{
    // Addressable-matrix Razer lighting child: writes a per-LED custom frame (the O11
    // Dynamic renders this, unlike the plain static effect) then the custom-frame effect.
    // Args: <RRGGBB|off> [ledCount]. ledCount is exposed for live tuning of the O11's LED
    // total; it defaults to 75 (covers the ~70-LED Razer Edition with margin).
    int customIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-razer-custom", StringComparison.OrdinalIgnoreCase));
    string customValue = customIndex >= 0 && customIndex + 1 < args.Length ? args[customIndex + 1] : string.Empty;
    int customRows = customIndex >= 0 && customIndex + 2 < args.Length && int.TryParse(args[customIndex + 2], out int parsedRows) ? parsedRows : 4;
    int customCols = customIndex >= 0 && customIndex + 3 < args.Length && int.TryParse(args[customIndex + 3], out int parsedCols) ? parsedCols : 16;
    bool customOff = string.Equals(customValue, "off", StringComparison.OrdinalIgnoreCase);
    RazerRgbResultV1 custom = RazerUsbRgbWriter.Write(customOff ? string.Empty : customValue, customOff, customFrameMatrix: (customRows, customCols));
    Console.WriteLine(JsonSerializer.Serialize(custom, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-razer-argb", StringComparer.OrdinalIgnoreCase))
{
    // EXTENDED_ARGB Razer lighting child: the O11 Dynamic detects as RAZER_MATRIX_TYPE_EXTENDED_ARGB
    // in OpenRGB, which honours the dedicated 321-byte razer_argb_report (report id 0x04/0x84) —
    // NOT the extended-matrix custom-frame command (--set-razer-custom), which it acknowledges but
    // never renders. This path raises the ARGB-channel brightnesses then writes one ARGB frame per
    // row. Args: <RRGGBB|off> [rows] [cols], defaulting to the O11's 4x16 matrix.
    int argbIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-razer-argb", StringComparison.OrdinalIgnoreCase));
    string argbValue = argbIndex >= 0 && argbIndex + 1 < args.Length ? args[argbIndex + 1] : string.Empty;
    int argbRows = argbIndex >= 0 && argbIndex + 2 < args.Length && int.TryParse(args[argbIndex + 2], out int parsedArgbRows) ? parsedArgbRows : 4;
    int argbCols = argbIndex >= 0 && argbIndex + 3 < args.Length && int.TryParse(args[argbIndex + 3], out int parsedArgbCols) ? parsedArgbCols : 16;
    bool argbOff = string.Equals(argbValue, "off", StringComparison.OrdinalIgnoreCase);
    RazerRgbResultV1 argb = RazerUsbRgbWriter.WriteArgb(argbOff ? string.Empty : argbValue, argbOff, argbRows, argbCols);
    Console.WriteLine(JsonSerializer.Serialize(argb, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-razer-hold", StringComparer.OrdinalIgnoreCase))
{
    // Lead-(b) persistent-session diagnostic: sends the faithful EXTENDED sequence (brightness
    // across candidate LED ids, per-row custom frame, mode-custom) then HOLDS the HID handle
    // open for N seconds, refreshing the frame. If the O11 is acknowledged-but-dark only because
    // the controller reverts when the last handle closes, the case lights ONLY during the hold.
    // Run as SYSTEM watching the case. Args: <RRGGBB|off> [seconds] [rows] [cols].
    int holdIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-razer-hold", StringComparison.OrdinalIgnoreCase));
    string holdValue = holdIndex >= 0 && holdIndex + 1 < args.Length ? args[holdIndex + 1] : string.Empty;
    int holdSeconds = holdIndex >= 0 && holdIndex + 2 < args.Length && int.TryParse(args[holdIndex + 2], out int parsedHoldSeconds) ? parsedHoldSeconds : 15;
    int holdRows = holdIndex >= 0 && holdIndex + 3 < args.Length && int.TryParse(args[holdIndex + 3], out int parsedHoldRows) ? parsedHoldRows : 4;
    int holdCols = holdIndex >= 0 && holdIndex + 4 < args.Length && int.TryParse(args[holdIndex + 4], out int parsedHoldCols) ? parsedHoldCols : 16;
    bool holdOff = string.Equals(holdValue, "off", StringComparison.OrdinalIgnoreCase);
    RazerRgbResultV1 hold = RazerUsbRgbWriter.WriteHold(holdOff ? string.Empty : holdValue, holdOff, holdSeconds, holdRows, holdCols);
    Console.WriteLine(JsonSerializer.Serialize(hold, JsonDefaults.Options));
    return;
}

if (args.Contains("--set-smbus-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Disposable DIMM RGB child: the production audited path. Detection +
    // device-name identity gates run first; colour is transmitted only to an
    // identity-confirmed ENE controller on an audited kit, and the address
    // policy re-checks every transaction. 'off' writes black.
    int dimmIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-smbus-rgb", StringComparison.OrdinalIgnoreCase));
    string dimmValue = dimmIndex >= 0 && dimmIndex + 1 < args.Length ? args[dimmIndex + 1] : string.Empty;
    if (string.Equals(dimmValue, "off", StringComparison.OrdinalIgnoreCase))
    {
        dimmValue = "000000";
    }

    SmbusRgbFirstLightResultV1 dimm = SmbusRgbFirstLight.Apply(dimmValue);
    Console.WriteLine(JsonSerializer.Serialize(
        new DimmRgbResultV1(DimmRgbResultV1.CurrentSchemaVersion, dimm.WriteOutcome, dimm.TransactionsIssued, dimm.Message),
        JsonDefaults.Options));
    return;
}

if (args.Contains("--survey-smbus", StringComparer.OrdinalIgnoreCase))
{
    // Disposable, purely read-only FCH SMBus port survey: SPD presence reads
    // (DRAM-type byte) and ENE detection reads on every selectable port. No
    // write of any kind is issued; port selection is routing, not a write.
    Console.WriteLine(JsonSerializer.Serialize(SmbusBusSurvey.Run(), JsonDefaults.Options));
    return;
}

if (args.Contains("--identify-smbus-rgb-deep", StringComparer.OrdinalIgnoreCase))
{
    // Operator-run deep identity pass at one named RGB-range address: tries
    // the documented read-command/pointer variants; each issues exactly one
    // pointer write and byte reads. Refuses non-RGB addresses outright.
    int deepIndex = Array.FindIndex(args, argument => string.Equals(argument, "--identify-smbus-rgb-deep", StringComparison.OrdinalIgnoreCase));
    string deepValue = deepIndex >= 0 && deepIndex + 1 < args.Length ? args[deepIndex + 1] : string.Empty;
    byte deepAddress = Convert.ToByte(deepValue, 16);
    Console.WriteLine(JsonSerializer.Serialize(SmbusRgbIdentify.DeepRun(deepAddress), JsonDefaults.Options));
    return;
}

if (args.Contains("--identify-smbus-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Operator-run ENE identity read: one pointer write plus byte reads per
    // acknowledged RGB address. No colour/mode/apply register is touched.
    Console.WriteLine(JsonSerializer.Serialize(SmbusRgbIdentify.Run(), JsonDefaults.Options));
    return;
}

if (args.Contains("--first-light-smbus-rgb", StringComparer.OrdinalIgnoreCase))
{
    // Operator-run witnessed first-light for the ENE DIMM RGB protocol. This
    // is the ONLY path that may transmit to an unaudited kit, and it exists to
    // be run manually by the operator while watching the DIMMs. It bypasses
    // only the audit gate — the SmbusAddressPolicy still checks every
    // transaction, so nothing here can reach an SPD, thermal, or PMIC address.
    int firstLightIndex = Array.FindIndex(args, argument => string.Equals(argument, "--first-light-smbus-rgb", StringComparison.OrdinalIgnoreCase));
    string firstLightColour = firstLightIndex >= 0 && firstLightIndex + 1 < args.Length ? args[firstLightIndex + 1] : string.Empty;
    Console.WriteLine(JsonSerializer.Serialize(SmbusRgbFirstLight.Run(firstLightColour), JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-gpu-fan-bisect3", StringComparer.OrdinalIgnoreCase))
{
    // Round 3. Rounds 1 and 2 excluded the NVAPI client set, thread affinity, write
    // repetition and elapsed ownership -- every stage ACCEPTED.
    //
    // What no probe has reproduced is SUSTAINED background NVAPI traffic. The service runs
    // LibreHardwareMonitor telemetry continuously against the NVIDIA GPU, and that polling
    // never stops -- not even when the cooling graph is quiesced, which is why the earlier
    // "quiesced graph" test was not actually quiet. Round 1 called CaptureAsync exactly
    // once; continuous polling is a different condition.
    //
    // This is also the mechanism the codebase already suspects. NvidiaGpuFanAdapter's
    // RestoreAutomaticWithRetryAsync comment records a live restore refused with
    // NVAPI_INVALID_USER_PRIVILEGE "moments after the preceding apply's settle-poll made a
    // burst of rapid NVAPI reads and a write", and calls it "rapid-call driver-session
    // fragility". The settle-poll was de-densified to 12 x 500 ms in response, but the
    // telemetry loop was never part of that accounting.
    //
    // Stages, each independent, with a background load running during write AND restore:
    //   A control            : no background load        (MUST pass or run is void)
    //   B lhm-telemetry-loop : continuous CaptureAsync, as the service runs
    //   C rapid-nvapi-reads  : tight ReadStateAsync loop -- the harshest read pressure
    // First REFUSED stage names the mechanism.
    //
    // WARNING: a positive result leaves the fan Manual at 70% (safe, over-cooled). Clear
    // with --probe-gpu-fan-restore-only from a fresh process.
    List<object> stages3 = [];
    string? destabiliser = null;

    async Task<bool> LoadStageAsync(string name, Func<CancellationToken, Task> backgroundLoad)
    {
        if (destabiliser is not null)
        {
            return false;
        }

        if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport fan, out string bindMessage))
        {
            stages3.Add(new { stage = name, result = "SETUP FAILED", detail = bindMessage });
            destabiliser = name;
            return false;
        }

        using CancellationTokenSource loadStop = new();
        Task load = Task.Run(async () =>
        {
            try
            {
                await backgroundLoad(loadStop.Token);
            }
            catch (Exception)
            {
                // A failing load must not mask the restore result being measured.
            }
        });

        // Let the background traffic establish before touching the cooler.
        await Task.Delay(TimeSpan.FromSeconds(3));

        fan.SetArmed(true);
        string own;
        try
        {
            await fan.SetManualDutyAsync("0", 70, CancellationToken.None);
            own = "accepted";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            own = $"refused: {exception.Message}";
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        string restore;
        try
        {
            await fan.RestoreAutomaticAsync("0", CancellationToken.None);
            restore = "ACCEPTED";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            restore = "REFUSED";
        }

        await loadStop.CancelAsync();
        try
        {
            await load;
        }
        catch (Exception)
        {
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        GpuFanChannelState after = await fan.ReadStateAsync("0", CancellationToken.None);
        bool stuck = after.Policy != GpuFanControlPolicy.Automatic;
        stages3.Add(new { stage = name, takeOwnership = own, restore, policyAfter = after.Policy.ToString() });
        if (stuck)
        {
            destabiliser = name;
        }

        return !stuck;
    }

    await LoadStageAsync("A-control-no-background-load", _ => Task.CompletedTask);

    await LoadStageAsync("B-continuous-lhm-telemetry", async token =>
    {
        while (!token.IsCancellationRequested)
        {
            await coordinator.CaptureAsync(token);
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
    });

    await LoadStageAsync("C-rapid-nvapi-reads", async token =>
    {
        if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport reader, out _))
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            await reader.ReadStateAsync("0", token);
        }
    });

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            available = true,
            processId = Environment.ProcessId,
            stages = stages3,
            destabiliser,
            verdict = destabiliser is null
                ? "No stage reproduced the refusal. Sustained NVAPI read pressure is excluded too. STOP probing from outside: instrument the service process itself rather than continuing to model it."
                : destabiliser == "A-control-no-background-load"
                    ? "CONTROL FAILED -- baseline could not restore, so this run proves nothing."
                    : $"REPRODUCED: '{destabiliser}' -- sustained NVAPI traffic of this kind is what makes the restore fail. This matches the 'rapid-call driver-session fragility' already recorded in NvidiaGpuFanAdapter."
        },
        JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-gpu-fan-bisect2", StringComparer.OrdinalIgnoreCase))
{
    // Round 2 of the bisection. Round 1 added every service-side NVAPI consumer (NVML,
    // clock-offset with writes, power limit, LibreHardwareMonitor) and restore stayed
    // ACCEPTED at every stage -- so the poison is not the set of NVAPI clients.
    //
    // What every successful probe has shared, and the service has NOT: a single thread.
    // The service writes fan duty from the cooling graph engine's timer thread and calls
    // restore from the IPC handler thread. If NVAPI binds cooler control to the writing
    // THREAD, restore from a different thread would be refused -- which fits everything
    // observed, including why a fresh process that never wrote (restore-only) succeeds.
    //
    // Stages, each independent (fan rebound, ownership retaken):
    //   A control     : write and restore on the same thread     -- must pass or run is void
    //   B cross-thread: write on a dedicated thread, restore on the main thread
    //   C repetition  : 20 writes on one thread, then restore
    //   D elapsed     : write, idle 60s, then restore
    // First REFUSED stage names the mechanism.
    //
    // WARNING: a positive result leaves the fan Manual at 70% (safe, over-cooled) because
    // the process cannot restore -- that IS the finding. Clear with
    // --probe-gpu-fan-restore-only from a fresh process.
    List<object> stages2 = [];
    string? mechanism = null;

    async Task<bool> StageAsync(string name, Action<NvApiGpuFanCoolerTransport> takeOwnership)
    {
        if (mechanism is not null)
        {
            return false;
        }

        if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport fan, out string bindMessage))
        {
            stages2.Add(new { stage = name, result = "SETUP FAILED", detail = bindMessage });
            mechanism = name;
            return false;
        }

        fan.SetArmed(true);
        string own;
        try
        {
            takeOwnership(fan);
            own = "accepted";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            own = $"refused: {exception.Message}";
        }

        await Task.Delay(TimeSpan.FromSeconds(2));

        string restore;
        try
        {
            await fan.RestoreAutomaticAsync("0", CancellationToken.None);
            restore = "ACCEPTED";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            restore = "REFUSED";
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        GpuFanChannelState after = await fan.ReadStateAsync("0", CancellationToken.None);
        bool stuck = after.Policy != GpuFanControlPolicy.Automatic;
        stages2.Add(new { stage = name, takeOwnership = own, restore, policyAfter = after.Policy.ToString() });
        if (stuck)
        {
            mechanism = name;
        }

        return !stuck;
    }

    // A: everything on the calling thread -- the shape every passing probe has had.
    await StageAsync("A-control-same-thread", fan =>
        fan.SetManualDutyAsync("0", 70, CancellationToken.None).GetAwaiter().GetResult());

    // B: the write happens on a dedicated OS thread that has fully exited before the
    // restore runs, mirroring the service's timer-thread-writes / IPC-thread-restores split.
    await StageAsync("B-write-on-dedicated-thread", fan =>
    {
        Exception? threadFailure = null;
        Thread writer = new(() =>
        {
            try
            {
                fan.SetManualDutyAsync("0", 70, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                threadFailure = exception;
            }
        });
        writer.Start();
        writer.Join();
        if (threadFailure is not null)
        {
            throw threadFailure;
        }
    });

    // C: many writes, as a ticking graph engine would issue.
    await StageAsync("C-twenty-writes-same-thread", fan =>
    {
        for (int index = 0; index < 20; index++)
        {
            fan.SetManualDutyAsync("0", 70, CancellationToken.None).GetAwaiter().GetResult();
            Thread.Sleep(500);
        }
    });

    // D: ownership left to sit before restoring.
    await StageAsync("D-write-then-idle-60s", fan =>
    {
        fan.SetManualDutyAsync("0", 70, CancellationToken.None).GetAwaiter().GetResult();
        Thread.Sleep(TimeSpan.FromSeconds(60));
    });

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            available = true,
            processId = Environment.ProcessId,
            stages = stages2,
            mechanism,
            verdict = mechanism is null
                ? "No stage reproduced the refusal. Thread affinity, write repetition and elapsed ownership are all excluded; the difference is something else about the service process."
                : mechanism == "A-control-same-thread"
                    ? "CONTROL FAILED -- baseline could not restore, so this run proves nothing. Confirm the fan was Automatic and the process elevated, then re-run."
                    : $"REPRODUCED: '{mechanism}' is the first condition under which restore is refused."
        },
        JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-gpu-fan-bisect", StringComparer.OrdinalIgnoreCase))
{
    // Bisects the difference between a fresh process (restore ACCEPTED, reproducibly) and
    // the service process (restore REFUSED, reproducibly) by rebuilding the service's NVAPI
    // environment one component at a time inside a single process.
    //
    // Excluded already, by direct test: handle age (RefreshGpuHandle shipped and re-tested,
    // identical failure), ownership (a fresh process restores a fan it never owned),
    // concurrency (refused again against a quiesced graph), identity (LocalSystem both
    // sides), NVIDIA.Unload(), and IPC impersonation. What is left is process-scoped.
    //
    // The service also constructs, alongside the NVAPI fan transport: an NVML fan transport
    // probe, an NVAPI clock-offset transport with WRITES ENABLED, an NVAPI power-limit
    // transport (whose TDP anchor is read via NVML), and LibreHardwareMonitor, which uses
    // NVAPI itself. A fresh probe has essentially one NVAPI consumer. One of those extra
    // consumers is the likely poison.
    //
    // Each stage: take ownership cleanly -> add ONE component -> attempt restore. The first
    // stage whose restore is REFUSED names the culprit, and the probe stops there.
    //
    // Everything is done in ONE run because each run costs an elevated/SYSTEM invocation.
    //
    // WARNING: a positive result deliberately leaves the fan Manual at 70% (safe, over-cooled)
    // because the poisoned process cannot restore it -- that IS the finding. Clear it
    // afterwards with --probe-gpu-fan-restore-only from a fresh process.
    List<object> stages = [];
    string? culprit = null;
    NvApiGpuFanCoolerTransport bisectFan = null!;

    async Task<bool> TryStageAsync(string name, Func<Task> addComponent)
    {
        if (culprit is not null)
        {
            return false;
        }

        // Rebind a fan transport per stage so the fan handle itself is never the variable.
        if (!NvApiGpuFanCoolerTransport.TryCreate(0, out bisectFan, out string bindMessage))
        {
            stages.Add(new { stage = name, result = "SETUP FAILED", detail = bindMessage });
            culprit = name;
            return false;
        }

        bisectFan.SetArmed(true);
        string ownership;
        try
        {
            await bisectFan.SetManualDutyAsync("0", 70, CancellationToken.None);
            ownership = "accepted";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ownership = $"refused: {exception.Message}";
        }

        await Task.Delay(TimeSpan.FromSeconds(2));

        string componentOutcome = "ok";
        try
        {
            await addComponent();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            componentOutcome = $"{exception.GetType().Name}: {exception.Message}";
        }

        string restore;
        try
        {
            await bisectFan.RestoreAutomaticAsync("0", CancellationToken.None);
            restore = "ACCEPTED";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            restore = "REFUSED";
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        GpuFanChannelState after = await bisectFan.ReadStateAsync("0", CancellationToken.None);
        bool poisoned = after.Policy != GpuFanControlPolicy.Automatic;
        stages.Add(new
        {
            stage = name,
            takeOwnership = ownership,
            componentSetup = componentOutcome,
            restore,
            policyAfter = after.Policy.ToString()
        });

        if (poisoned)
        {
            culprit = name;
        }

        return !poisoned;
    }

    // Stage 0 is the control: it must pass, or the whole run is meaningless.
    await TryStageAsync("0-control-fan-transport-only", () => Task.CompletedTask);

    await TryStageAsync("1-plus-nvml", () =>
    {
        NvmlGpuFanCoolerTransport.TryCreate(enableWrites: true, out NvmlGpuFanCoolerTransport nvml, out _);
        return Task.CompletedTask;
    });

    await TryStageAsync("2-plus-nvapi-clock-offset-writes-enabled", async () =>
    {
        if (NvapiGpuClockOffsetTransport.TryCreate(0, enableWrites: true, out NvapiGpuClockOffsetTransport clock, out _))
        {
            await clock.ReadBoundsAsync(GpuClockOffsetDomain.Core, CancellationToken.None);
        }
    });

    await TryStageAsync("3-plus-nvapi-power-limit", () =>
    {
        NvApiGpuPowerLimitTransport.TryCreate(0, 350_000, enableWrites: true, out NvApiGpuPowerLimitTransport power, out _);
        return Task.CompletedTask;
    });

    await TryStageAsync("4-plus-librehardwaremonitor-capture", async () =>
    {
        await coordinator.CaptureAsync(CancellationToken.None);
    });

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            available = true,
            processId = Environment.ProcessId,
            stages,
            culprit,
            verdict = culprit is null
                ? "No stage reproduced the refusal -- every service-like NVAPI consumer was added and restore still succeeded. The poison is NOT one of these components; look at service-specific sequencing or long-running state instead."
                : culprit == "0-control-fan-transport-only"
                    ? "CONTROL FAILED -- the baseline stage could not restore, so this run proves nothing. Check the fan was Automatic and the process is elevated, then re-run."
                    : $"REPRODUCED: '{culprit}' is the first component whose presence blocks restore. That is the poison. Fan is left Manual 70% -- clear with --probe-gpu-fan-restore-only.",
            note = "Stages are cumulative and ordered; only the FIRST refusal is meaningful."
        },
        JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-gpu-fan-restore-only", StringComparer.OrdinalIgnoreCase))
{
    // Isolates HANDLE FRESHNESS from OWNERSHIP -- the two were confounded in
    // --probe-gpu-fan-owner-restore, which had both and so proved neither.
    //
    // Reproduced 2026-07-20: the SERVICE, genuinely owning the fan (Manual 66% via the
    // Cooling curve), was refused every restore path with NVAPI_INVALID_USER_PRIVILEGE.
    // A fresh probe owning the fan was ACCEPTED on the first path. Same LocalSystem
    // identity both sides, so identity is excluded. What differs is that the service
    // caches its PhysicalGPU handle from startup (PCHelperRuntime.cs:123) while the probe
    // enumerates seconds before use -- and NVAPI reports stale handles with misleading
    // statuses.
    //
    // This probe takes NO ownership. It binds a fresh handle and calls restore on a fan
    // owned by the live service:
    //   ACCEPTED -> freshness is the variable, ownership is irrelevant. The fix is to
    //               re-enumerate the GPU handle before restore, NOT anything structural.
    //   REFUSED  -> ownership really is required; the earlier result stands as-is.
    if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport restoreOnlyTransport, out string restoreOnlyMessage))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { available = false, message = restoreOnlyMessage }, JsonDefaults.Options));
        return;
    }

    GpuFanChannelState restoreOnlyBefore = await restoreOnlyTransport.ReadStateAsync("0", CancellationToken.None);
    restoreOnlyTransport.SetArmed(true);

    string restoreOnlyOutcome;
    try
    {
        await restoreOnlyTransport.RestoreAutomaticAsync("0", CancellationToken.None);
        restoreOnlyOutcome = "ACCEPTED";
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        restoreOnlyOutcome = $"REFUSED: {exception.Message}";
    }

    await Task.Delay(TimeSpan.FromSeconds(2));
    GpuFanChannelState restoreOnlyAfter = await restoreOnlyTransport.ReadStateAsync("0", CancellationToken.None);

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            available = true,
            processId = Environment.ProcessId,
            tookOwnership = false,
            policyBefore = restoreOnlyBefore.Policy.ToString(),
            commandedDutyBefore = restoreOnlyBefore.CommandedDutyPercent,
            restoreOnlyOutcome,
            policyAfter = restoreOnlyAfter.Policy.ToString(),
            commandedDutyAfter = restoreOnlyAfter.CommandedDutyPercent,
            verdict = restoreOnlyBefore.Policy != GpuFanControlPolicy.Manual
                ? "VOID -- the fan was not Manual to begin with, so restore short-circuits on its early-return guard and proves nothing."
                : restoreOnlyAfter.Policy == GpuFanControlPolicy.Automatic
                    // NOTE: this reads "a fresh PROCESS", not "a fresh handle". Re-enumerating the
                    // handle INSIDE the service was implemented, deployed and RE-TESTED, and the
                    // restore was refused identically -- so handle age is excluded, as are ownership
                    // (this probe owns nothing) and concurrency (verified against a quiesced graph).
                    // What remains is process-scoped and not yet identified. Do not restate this as
                    // a root cause; two confident root causes have already been wrong.
                    ? "A FRESH PROCESS restored a fan it never owned. The service process cannot, regardless of handle age, ownership, or concurrent writes. The distinguishing factor is process-scoped and UNIDENTIFIED -- this probe narrows the search, it does not name a cause."
                    : "A fresh process without ownership was refused; the process-scoped theory does not hold on its own."
        },
        JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-gpu-fan-unload-ownership", StringComparer.OrdinalIgnoreCase))
{
    // Tests the prime suspect for how the service loses the ability to restore.
    //
    // Owner-restore is now known to work (see --probe-gpu-fan-owner-restore). So the
    // question is what strips ownership from a long-lived service. NVIDIA.Unload() is
    // process-global: after it, the process holds a NEW NVAPI session that is not the
    // fan's owner. Both RestoreAutomaticAsync's last-resort strategy
    // (ReleaseNvApiSessionToReclaimFan) and NvApiGpuFanCoolerTransport.Dispose() call it.
    // If the hypothesis holds, the last-resort "restore" strategy is not a fallback at
    // all -- it destroys the working path, permanently, for the rest of the process.
    //
    // Sequence: own the fan -> Dispose (which calls Unload) -> rebind a fresh transport
    // -> attempt restore from the new session.
    //   REFUSED  -> Unload strips ownership. Remove/hard-gate that strategy.
    //   ACCEPTED -> Unload is harmless; look elsewhere.
    //
    // The probe finishes by re-taking ownership and restoring properly if the fan is
    // still Manual, so a confirming (REFUSED) result does not leave the fan stuck.
    int unloadArgumentIndex = Array.FindIndex(args, argument => string.Equals(argument, "--probe-gpu-fan-unload-ownership", StringComparison.OrdinalIgnoreCase));
    string unloadArgumentValue = unloadArgumentIndex >= 0 && unloadArgumentIndex + 1 < args.Length ? args[unloadArgumentIndex + 1] : string.Empty;
    int unloadProbeDuty = Math.Clamp(int.TryParse(unloadArgumentValue, out int parsedUnloadDuty) ? parsedUnloadDuty : 70, 50, 100);

    if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport firstSession, out string firstSessionMessage))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { available = false, message = firstSessionMessage }, JsonDefaults.Options));
        return;
    }

    GpuFanChannelState unloadStateBefore = await firstSession.ReadStateAsync("0", CancellationToken.None);
    firstSession.SetArmed(true);
    string unloadTakeOutcome;
    try
    {
        await firstSession.SetManualDutyAsync("0", unloadProbeDuty, CancellationToken.None);
        unloadTakeOutcome = "accepted";
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        unloadTakeOutcome = $"refused: {exception.GetType().Name}: {exception.Message}";
    }

    await Task.Delay(TimeSpan.FromSeconds(2));
    GpuFanChannelState unloadStateOwned = await firstSession.ReadStateAsync("0", CancellationToken.None);

    // The event under test: process-global NVAPI unload, then a fresh session.
    firstSession.Dispose();
    if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport secondSession, out string secondSessionMessage))
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new { available = false, stage = "rebind-after-unload", message = secondSessionMessage },
            JsonDefaults.Options));
        return;
    }

    secondSession.SetArmed(true);
    string restoreAfterUnloadOutcome;
    try
    {
        await secondSession.RestoreAutomaticAsync("0", CancellationToken.None);
        restoreAfterUnloadOutcome = "ACCEPTED";
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        restoreAfterUnloadOutcome = $"REFUSED: {exception.Message}";
    }

    await Task.Delay(TimeSpan.FromSeconds(2));
    GpuFanChannelState unloadStateAfter = await secondSession.ReadStateAsync("0", CancellationToken.None);

    // Self-heal: never leave the fan stuck just because the hypothesis was confirmed.
    string recoveryOutcome = "not needed";
    if (unloadStateAfter.Policy == GpuFanControlPolicy.Manual)
    {
        try
        {
            await secondSession.SetManualDutyAsync("0", unloadProbeDuty, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await secondSession.RestoreAutomaticAsync("0", CancellationToken.None);
            recoveryOutcome = "re-took ownership on the new session and restored";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            recoveryOutcome = $"FAILED: {exception.Message}";
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        unloadStateAfter = await secondSession.ReadStateAsync("0", CancellationToken.None);
    }

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            available = true,
            processId = Environment.ProcessId,
            probeDutyPercent = unloadProbeDuty,
            policyBefore = unloadStateBefore.Policy.ToString(),
            takeOwnershipOutcome = unloadTakeOutcome,
            policyWhileOwned = unloadStateOwned.Policy.ToString(),
            restoreAfterUnloadOutcome,
            recoveryOutcome,
            finalPolicy = unloadStateAfter.Policy.ToString(),
            finalCommandedDutyPercent = unloadStateAfter.CommandedDutyPercent,
            verdict = unloadTakeOutcome != "accepted"
                ? "VOID -- never owned the fan; re-run elevated as SYSTEM."
                : restoreAfterUnloadOutcome == "ACCEPTED"
                    ? "NVIDIA.Unload() does NOT strip fan ownership; look elsewhere for what breaks the service's restore."
                    : "CONFIRMED -- NVIDIA.Unload() strips fan ownership: restore succeeds for an owner but is refused after a process-global unload. ReleaseNvApiSessionToReclaimFan destroys the working restore path; remove or hard-gate it, and audit transport Dispose ordering."
        },
        JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-gpu-fan-owner-restore", StringComparer.OrdinalIgnoreCase))
{
    // Operator-run experiment isolating ONE question the restore-elimination table never
    // separated: does NVAPI refuse a cooler restore because the call is privileged, or
    // because the CALLER DOES NOT OWN THE FAN?
    //
    // Every recorded refusal appears to have been measured from a non-owner. The
    // cold-start arming refusals invoked restore "purely to reach the automatic state the
    // cooler was already resting in" (see RestoreAutomaticAsync's own comment) -- the
    // service held no manual ownership at that moment. The event-2009 refusals observed
    // on 2026-07-20 came from a service whose fan was owned by an exited probe. So the
    // table's headline claim -- manual control can be taken but never given back -- may be
    // true only for a non-owner.
    //
    // This probe takes ownership and then immediately restores FROM THE SAME PROCESS,
    // which nothing so far has isolated. If restore is accepted here, the real fix is far
    // smaller than a child process: restore while the service still owns the fan.
    //
    // It is also the one experiment that can un-stick a stuck fan without a reboot, since
    // a successful restore leaves the cooler on the driver curve.
    int restoreArgumentIndex = Array.FindIndex(args, argument => string.Equals(argument, "--probe-gpu-fan-owner-restore", StringComparison.OrdinalIgnoreCase));
    string restoreArgumentValue = restoreArgumentIndex >= 0 && restoreArgumentIndex + 1 < args.Length ? args[restoreArgumentIndex + 1] : string.Empty;
    int restoreProbeDuty = Math.Clamp(int.TryParse(restoreArgumentValue, out int parsedRestoreDuty) ? parsedRestoreDuty : 70, 50, 100);

    if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport restoreTransport, out string restoreTransportMessage))
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new { available = false, message = restoreTransportMessage },
            JsonDefaults.Options));
        return;
    }

    GpuFanChannelState restoreStateBefore = await restoreTransport.ReadStateAsync("0", CancellationToken.None);
    restoreTransport.SetArmed(true);

    string takeOwnershipOutcome;
    try
    {
        await restoreTransport.SetManualDutyAsync("0", restoreProbeDuty, CancellationToken.None);
        takeOwnershipOutcome = "accepted";
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        takeOwnershipOutcome = $"refused: {exception.GetType().Name}: {exception.Message}";
    }

    // Read-back is stale immediately after a write, so settle before asserting ownership.
    await Task.Delay(TimeSpan.FromSeconds(2));
    GpuFanChannelState restoreStateOwned = await restoreTransport.ReadStateAsync("0", CancellationToken.None);

    string restoreOutcome;
    try
    {
        await restoreTransport.RestoreAutomaticAsync("0", CancellationToken.None);
        restoreOutcome = "ACCEPTED";
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        restoreOutcome = $"REFUSED: {exception.Message}";
    }

    await Task.Delay(TimeSpan.FromSeconds(2));
    GpuFanChannelState restoreStateAfter = await restoreTransport.ReadStateAsync("0", CancellationToken.None);

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            available = true,
            processId = Environment.ProcessId,
            probeDutyPercent = restoreProbeDuty,
            policyBefore = restoreStateBefore.Policy.ToString(),
            commandedDutyBefore = restoreStateBefore.CommandedDutyPercent,
            takeOwnershipOutcome,
            policyWhileOwned = restoreStateOwned.Policy.ToString(),
            commandedDutyWhileOwned = restoreStateOwned.CommandedDutyPercent,
            ownerRestoreOutcome = restoreOutcome,
            policyAfterRestore = restoreStateAfter.Policy.ToString(),
            commandedDutyAfterRestore = restoreStateAfter.CommandedDutyPercent,
            // The verdict must depend on the precondition actually holding. If the manual
            // write was refused this process never owned the fan, so the restore result
            // says nothing about owner-restore -- report VOID rather than a conclusion the
            // run cannot support. (Measured unelevated: the manual write itself is refused
            // with NVAPI_INVALID_USER_PRIVILEGE, so this probe MUST run elevated/as SYSTEM.)
            verdict = takeOwnershipOutcome != "accepted"
                ? "VOID -- could not take ownership (the manual write was refused), so the restore result is meaningless. Re-run elevated, ideally as SYSTEM in session 0."
                : restoreStateAfter.Policy == GpuFanControlPolicy.Automatic
                    ? "OWNER RESTORE WORKS -- restore while owning the fan is accepted; fix is to restore before release, not a child process."
                    : "Owner restore did NOT return the cooler to Automatic; the refusal is not merely an ownership problem."
        },
        JsonDefaults.Options));
    return;
}

if (args.Contains("--probe-gpu-fan-ownership", StringComparer.OrdinalIgnoreCase))
{
    // Operator-run, throwaway experiment answering exactly one question: is GPU fan
    // ownership released when the *owning process* exits, or specifically when the
    // *service* exits? Every observation behind the child-process design was made on
    // service exit, because the service is the only process that has ever held the
    // fan. The architecture assumes that generalises to a child. It has never been
    // tested, and it is load-bearing: if it is false, the design fails only after the
    // process boundary, IPC, and lifecycle work is already built.
    //
    // This child takes manual ownership and exits WITHOUT restoring. The omission is
    // the experiment. Afterwards, read `pchelper-cli gpu-fan-state`:
    //   policy=Automatic -> process exit releases ownership; build the real child.
    //   policy=Manual    -> it does not; the architecture is wrong, stop now.
    //
    // Two design notes that are easy to get wrong:
    //
    // 1. The failure mode must be safe. If ownership DOES survive exit, the fan is
    //    left at whatever duty this set, with no live handle able to change it, until
    //    the service restarts. So the duty is clamped to a HIGH floor: the bad
    //    outcome leaves the card over-cooled and audible, never under-cooled. High is
    //    the safe direction for a fan, so the device floor is deliberately not honoured
    //    while the ceiling is.
    // 2. Whether the transport is disposed before exit is now the variable under test,
    //    selected with --dispose-before-exit. Dispose calls NVIDIA.Unload(). The
    //    elimination table records Unload as insufficient *on its own* ("ran; cooler
    //    still Manual") -- but that was Unload WITHOUT process exit. Unload FOLLOWED BY
    //    process exit is untested, and it is precisely what the service's graceful
    //    shutdown does: the one case where release has ever actually been observed.
    //    Run without the flag = bare exit (measured 2026-07-20: does NOT release, even
    //    as LocalSystem in session 0). Run with it = Unload-then-exit.
    int fanArgumentIndex = Array.FindIndex(args, argument => string.Equals(argument, "--probe-gpu-fan-ownership", StringComparison.OrdinalIgnoreCase));
    string fanArgumentValue = fanArgumentIndex >= 0 && fanArgumentIndex + 1 < args.Length ? args[fanArgumentIndex + 1] : string.Empty;
    int requestedFanDuty = int.TryParse(fanArgumentValue, out int parsedFanDuty) ? parsedFanDuty : 70;
    const int experimentSafeFloorPercent = 50;
    int appliedFanDuty = Math.Clamp(requestedFanDuty, experimentSafeFloorPercent, 100);

    if (!NvApiGpuFanCoolerTransport.TryCreate(0, out NvApiGpuFanCoolerTransport fanOwnershipTransport, out string fanOwnershipMessage))
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new { available = false, message = fanOwnershipMessage },
            JsonDefaults.Options));
        return;
    }

    GpuFanChannelState fanStateBefore = await fanOwnershipTransport.ReadStateAsync("0", CancellationToken.None);
    if (await fanOwnershipTransport.ReadBoundsAsync("0", CancellationToken.None) is { } fanBounds)
    {
        appliedFanDuty = Math.Min(appliedFanDuty, fanBounds.CeilingPercent);
    }

    fanOwnershipTransport.SetArmed(true);
    await fanOwnershipTransport.SetManualDutyAsync("0", appliedFanDuty, CancellationToken.None);
    GpuFanChannelState fanStateAfter = await fanOwnershipTransport.ReadStateAsync("0", CancellationToken.None);

    // Read-back immediately after a write is not trustworthy: both duty fields come from
    // cooler.CurrentLevel, which was measured returning the PRE-write value right after a
    // write and settling only later. Report it, but never conclude from it.
    bool disposeBeforeExit = args.Contains("--dispose-before-exit", StringComparer.OrdinalIgnoreCase);

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            available = true,
            processId = Environment.ProcessId,
            requestedDutyPercent = requestedFanDuty,
            appliedDutyPercent = appliedFanDuty,
            policyBefore = fanStateBefore.Policy.ToString(),
            commandedDutyBefore = fanStateBefore.CommandedDutyPercent,
            policyAfter = fanStateAfter.Policy.ToString(),
            commandedDutyAfter = fanStateAfter.CommandedDutyPercent,
            measuredDutyAfter = fanStateAfter.MeasuredDutyPercent,
            readBackIsUnreliableImmediatelyAfterWrite = true,
            restoreDeliberatelySkipped = true,
            disposeBeforeExit,
            nextStep = disposeBeforeExit
                ? "This process now calls NVIDIA.Unload() and exits. Read 'pchelper-cli gpu-fan-state': Automatic = Unload-then-exit releases the fan; still Manual at the applied duty = it does not."
                : "This process now exits holding manual ownership, without Unload. Read 'pchelper-cli gpu-fan-state': Automatic = bare process exit releases the fan; still Manual at the applied duty = it does not."
        },
        JsonDefaults.Options));

    if (disposeBeforeExit)
    {
        fanOwnershipTransport.Dispose();
    }

    return;
}

if (args.Contains("--discover-hid", StringComparer.OrdinalIgnoreCase))
{
    // Disposable read-only HID inventory child. Native HidSharp enumeration runs here so a
    // fault is contained by the parent; the result is classification-only inventory that
    // never carries a write capability.
    HidInventoryResultV1 hid = HidPeripheralInventory.Enumerate();
    Console.WriteLine(JsonSerializer.Serialize(hid, JsonDefaults.Options));
    return;
}

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

// The Adapter Host is a private child process. Every request is independently
// authenticated with the launch-time 256-bit token in Unwrap<T>(). Do not
// impersonate the service client here: low-level driver calls must execute
// under the host process token, and the context returned by this pipe fails
// closed as non-operator.
NamedPipeRequestServer server = new(
    pipeName,
    HandleAsync,
    clientIdentityMode: NamedPipeClientIdentityMode.TokenAuthenticatedPrivateChannel);
Console.WriteLine("RigPilot adapter host is running with capability-gated mutations.");
await server.RunAsync(shutdown.Token);

async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
{
    try
    {
        return request.Command switch
        {
            IpcCommand.Handshake => Handshake(request),
            IpcCommand.GetInventory => Success(request, await coordinator.CaptureAsync(cancellationToken)),
            IpcCommand.SubscribeSensors => Success(request, await coordinator.Adapters[0].ReadSensorsAsync(cancellationToken)),
            IpcCommand.AdapterProbe => await ProbeAsync(request, cancellationToken),
            IpcCommand.AdapterReadSensors => await ReadSensorsAsync(request, cancellationToken),
            IpcCommand.AdapterPrepare => await PrepareAsync(request, cancellationToken),
            IpcCommand.AdapterApply => await ApplyAsync(request, cancellationToken),
            IpcCommand.AdapterVerify => Success(
                request,
                await coordinator.Adapters[0].VerifyAsync(
                    Unwrap<PreparedAction>(request),
                    cancellationToken)),
            IpcCommand.AdapterRollback => await RollbackAsync(request, cancellationToken),
            IpcCommand.AdapterReset => await ResetAsync(request, cancellationToken),
            IpcCommand.AdapterVerifyDefault => await VerifyDefaultAsync(request, cancellationToken),
            IpcCommand.AdapterVerifyRollback => await VerifyRollbackAsync(request, cancellationToken),
            IpcCommand.AdapterHealth => await HealthAsync(request, cancellationToken),
            IpcCommand.AdapterDiagnostics => Diagnostics(request),
            IpcCommand.AdapterShutdown => Shutdown(request),
            IpcCommand.GetServiceStatus => Success(request, new ServiceStatus(
                "0.6.0-beta.1",
                DateTimeOffset.UtcNow,
                0,
                null,
                false,
                false,
                "Adapter host is healthy.")),
            _ => Failure(request, "NOT_IMPLEMENTED", $"Adapter-host command {request.Command} is not implemented.")
        };
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        lastFailure = DescribeFailure(request.Command, exception);
        string message = FormatFailure(lastFailure, exception);
        Console.Error.WriteLine(message);
        return Failure(request, "ADAPTER_HOST_ERROR", message);
    }
}

IpcResponse Handshake(IpcRequest request)
{
    _ = Unwrap<HandshakeRequest>(request);
    return Success(request, new HandshakeResponse(
        ProtocolConstants.Version,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.6.0-beta.1",
        0));
}

async Task<IpcResponse> ProbeAsync(IpcRequest request, CancellationToken cancellationToken)
{
    _ = Unwrap<string>(request);
    return Success(request, await coordinator.Adapters[0].ProbeAsync(cancellationToken));
}

async Task<IpcResponse> ReadSensorsAsync(IpcRequest request, CancellationToken cancellationToken)
{
    _ = Unwrap<string>(request);
    return Success(request, await coordinator.Adapters[0].ReadSensorsAsync(cancellationToken));
}

async Task<IpcResponse> PrepareAsync(IpcRequest request, CancellationToken cancellationToken)
{
    // Diagnostics must describe this Prepare request, not an older rejected
    // token or unrelated adapter operation.
    lastFailure = null;
    ProfileAction action = Unwrap<ProfileAction>(request);
    return Success(request, await coordinator.Adapters[0].PrepareAsync(action, cancellationToken));
}

async Task<IpcResponse> HealthAsync(IpcRequest request, CancellationToken cancellationToken)
{
    _ = Unwrap<string>(request);
    return Success(request, await coordinator.Adapters[0].GetHealthAsync(cancellationToken));
}

IpcResponse Diagnostics(IpcRequest request)
{
    _ = Unwrap<string>(request);
    return Success(request, CaptureDiagnostics(lastFailure));
}

async Task<IpcResponse> ApplyAsync(IpcRequest request, CancellationToken cancellationToken)
{
    PreparedAction action = Unwrap<PreparedAction>(request);
    await coordinator.Adapters[0].ApplyAsync(action, cancellationToken);
    return Success(request, action.Action.Id);
}

async Task<IpcResponse> RollbackAsync(IpcRequest request, CancellationToken cancellationToken)
{
    PreparedAction action = Unwrap<PreparedAction>(request);
    await coordinator.Adapters[0].RollbackAsync(action, cancellationToken);
    return Success(request, action.Action.Id);
}

async Task<IpcResponse> ResetAsync(IpcRequest request, CancellationToken cancellationToken)
{
    AdapterResetRequest reset = Unwrap<AdapterResetRequest>(request);
    await coordinator.Adapters[0].ResetToDefaultAsync(reset.CapabilityId, cancellationToken);
    return Success(request, reset.CapabilityId);
}

async Task<IpcResponse> VerifyDefaultAsync(IpcRequest request, CancellationToken cancellationToken)
{
    AdapterDefaultVerificationRequest verification = Unwrap<AdapterDefaultVerificationRequest>(request);
    if (coordinator.Adapters[0] is not IHardwareStateVerifier verifier)
    {
        throw new NotSupportedException("The hosted adapter cannot verify its default state.");
    }

    return Success(request, await verifier.VerifyDefaultStateAsync(verification.CapabilityId, cancellationToken));
}

async Task<IpcResponse> VerifyRollbackAsync(IpcRequest request, CancellationToken cancellationToken)
{
    AdapterRollbackVerificationRequest verification = Unwrap<AdapterRollbackVerificationRequest>(request);
    if (coordinator.Adapters[0] is not IHardwareStateVerifier verifier)
    {
        throw new NotSupportedException("The hosted adapter cannot verify its rollback state.");
    }

    return Success(request, await verifier.VerifyRollbackStateAsync(verification.Action, cancellationToken));
}

IpcResponse Shutdown(IpcRequest request)
{
    _ = Unwrap<string>(request);
    shutdown.CancelAfter(TimeSpan.FromMilliseconds(100));
    return Success(request, "shutting-down");
}

T Unwrap<T>(IpcRequest request)
{
    AdapterHostEnvelope<T> envelope = IpcJson.FromElement<AdapterHostEnvelope<T>>(request.Payload)
        ?? throw new InvalidDataException("Adapter-host mutation envelope is required.");
    if (sessionToken is null || !TokensEqual(sessionToken, envelope.SessionToken))
    {
        throw new UnauthorizedAccessException("Adapter-host session token is invalid.");
    }

    return envelope.Payload;
}

static bool TokensEqual(string expected, string actual)
{
    byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
    byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
    return expectedBytes.Length == actualBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
}

string? GetArgument(string name)
{
    int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static AdapterHostDiagnosticsV1 CaptureDiagnostics(AdapterHostFailureV1? failure)
{
    using WindowsIdentity processIdentity = WindowsIdentity.GetCurrent(ifImpersonating: false)
        ?? throw new InvalidOperationException("The Adapter Host process token is unavailable.");
    WindowsPrincipal principal = new(processIdentity);
    bool isElevated = processIdentity.IsSystem
        || principal.IsInRole(WindowsBuiltInRole.Administrator);
    WindowsIdentity? threadIdentity = WindowsIdentity.GetCurrent(ifImpersonating: true);
    string threadTokenState;
    try
    {
        threadTokenState = threadIdentity is null
            ? "NoThreadImpersonationToken"
            : $"ThreadToken:{threadIdentity.ImpersonationLevel}";
    }
    finally
    {
        threadIdentity?.Dispose();
    }

    string identityKind = processIdentity.IsSystem
        ? "LocalSystem"
        : isElevated
            ? "ElevatedAdministrator"
            : "StandardUser";
    return new AdapterHostDiagnosticsV1(
        AdapterHostDiagnosticsV1.CurrentSchemaVersion,
        DateTimeOffset.UtcNow,
        Environment.ProcessId,
        identityKind,
        isElevated,
        threadTokenState,
        "PerRequestSessionToken",
        "SkippedFailClosedForTokenAuthenticatedPrivatePipe",
        failure);
}

static AdapterHostFailureV1 DescribeFailure(IpcCommand command, Exception exception)
{
    string stage = "Unhandled";
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current.Data["PCHelper.AdapterStage"] is string declaredStage
            && !string.IsNullOrWhiteSpace(declaredStage))
        {
            stage = declaredStage;
            break;
        }
    }

    Exception root = exception.GetBaseException();
    int hResult = root.HResult;
    return new AdapterHostFailureV1(
        command.ToString(),
        stage,
        root.GetType().Name,
        hResult,
        TryGetWin32Error(hResult),
        DateTimeOffset.UtcNow);
}

static int? TryGetWin32Error(int hResult)
{
    if (hResult is >= 0 and <= 0xFFFF)
    {
        return hResult;
    }

    uint unsigned = unchecked((uint)hResult);
    return (unsigned & 0xFFFF0000u) == 0x80070000u
        ? (int)(unsigned & 0xFFFFu)
        : null;
}

static string FormatFailure(AdapterHostFailureV1 failure, Exception exception)
{
    if (exception is UnauthorizedAccessException)
    {
        return "Adapter-host session token is invalid.";
    }

    string win32 = failure.Win32Error is int code ? $"; Win32={code}" : string.Empty;
    return $"Adapter-host {failure.Command} failed at {failure.Stage} ({failure.ExceptionType}; HResult=0x{unchecked((uint)failure.HResult):X8}{win32}).";
}

static IpcResponse Success<T>(IpcRequest request, T payload) => new(
    ProtocolConstants.Version,
    request.RequestId,
    true,
    0,
    null,
    null,
    IpcJson.ToElement(payload));

static IpcResponse Failure(IpcRequest request, string code, string error) => new(
    ProtocolConstants.Version,
    request.RequestId,
    false,
    0,
    code,
    error,
    null);
