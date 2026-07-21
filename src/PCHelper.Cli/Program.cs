using System.Text.Json;
using System.Security.Principal;
using System.Globalization;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        bool local = args.Contains("--local", StringComparer.OrdinalIgnoreCase);
        try
        {
            return command switch
            {
                "probe" => await ProbeAsync(json, local),
                "runtime-preflight" => await RuntimePreflightAsync(json),
                "status" => await ServiceCommandAsync<ServiceStatus>(IpcCommand.GetServiceStatus, json),
                "profiles" => await ServiceCommandAsync<IReadOnlyList<ProfileV1>>(IpcCommand.GetProfiles, json),
                "capabilities" => await ServiceCommandAsync<IReadOnlyList<CapabilityDescriptorV2>>(IpcCommand.GetCapabilitiesV2, json),
                "output-roles" => await ServiceCommandAsync<IReadOnlyList<CoolingOutputAssignmentV1>>(IpcCommand.GetCoolingOutputAssignments, json),
                "set-output-role" => await SetCoolingOutputRoleAsync(args, json),
                "commission-sessions" => await ServiceCommandAsync<IReadOnlyList<FanCommissioningSessionV1>>(IpcCommand.GetFanCommissioningSessions, json),
                "confirm-case-fan" => await ConfirmCaseFanAsync(args, json),
                "calibrate-case-fan" => await CalibrateCaseFanAsync(args, json),
                "operation" => await OperationAsync(args, json),
                "cooling-reports" => await ServiceCommandAsync<IReadOnlyList<CoolingQualificationReportV1>>(IpcCommand.GetCoolingQualificationReports, json),
                "cooling-graphs" => await ServiceCommandAsync<IReadOnlyList<CoolingGraphV1>>(IpcCommand.GetCoolingGraphs, json),
                "discover-controllers" => await ServiceCommandAsync<ControllerDiscoveryResultV1>(IpcCommand.DiscoverControllers, json),
                "discover-hid" => await DiscoverHidAsync(json),
                "ryzen-smu-feasibility" => await ReadRyzenSmuFeasibilityAsync(json),
                "close-blockers" => await StopConflictingProcessesAsync(args, json),
                "kraken-rgb" => await SetKrakenLightingAsync(args, json),
                "razer-rgb" => await SetRazerLightingAsync(args, json),
                "kraken-pump" => await SetKrakenPumpAsync(args, json),
                "gpu-fan-state" => await ServiceCommandAsync<GpuFanStateV1>(IpcCommand.GetGpuFanState, json),
                "gpu-fan-arm" => await SetGpuFanArmedAsync(args, json, arm: true),
                "gpu-fan-disarm" => await SetGpuFanArmedAsync(args, json, arm: false),
                "gpu-power-arm" => await SetGpuPowerArmedAsync(args, json, arm: true),
                "gpu-power-disarm" => await SetGpuPowerArmedAsync(args, json, arm: false),
                "gpu-clock-arm" => await SetGpuClockArmedAsync(args, json, arm: true),
                "gpu-clock-disarm" => await SetGpuClockArmedAsync(args, json, arm: false),
                "cpu-tuning-arm" => await SetCpuTuningArmedAsync(args, json, arm: true),
                "cpu-tuning-disarm" => await SetCpuTuningArmedAsync(args, json, arm: false),
                "trace" => await TraceAsync(json),
                "profiles-v2" => await ServiceCommandAsync<IReadOnlyList<ProfileV2>>(IpcCommand.GetProfilesV2, json),
                "games" => await UserCommandAsync<IReadOnlyList<GameEntryV1>>(IpcCommand.GetGames, json),
                "import-afterburner" => await PreviewAfterburnerAsync(args, json),
                "import-fancontrol" => await PreviewFanControlAsync(args, json),
                "pack-inspect" => await InspectAdapterPackAsync(args, json),
                "pack-list" => await ServiceCommandAsync<IReadOnlyList<AdapterPackInspection>>(IpcCommand.GetAdapterPacks, json),
                "pack-install" => await InstallAdapterPackAsync(args, json),
                "pack-remove" => await RemoveAdapterPackAsync(args, json),
                "report" => await ExportReportAsync(args, json),
                "qualification" => await QualificationAsync(args, json),
                "qualification-draft" => await QualificationDraftAsync(args, json),
                "direct-prepare" => await DirectPrepareAsync(args, json),
                "commission-preflight" => await CommissionPreflightAsync(args, json),
                "commission-pulse" => await CommissionPulseAsync(args, json),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"RigPilot CLI failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ProbeAsync(bool json, bool local)
    {
        HardwareSnapshot snapshot;
        try
        {
            snapshot = local
                ? throw new IOException("Local probe requested.")
                : await SendAsync<HardwareSnapshot>(IpcCommand.GetInventory);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await using AdapterCoordinator coordinator = new(
            [
                new SystemInventoryAdapter(),
                new WindowsPowerAdapter(),
                new NvmlTelemetryAdapter(),
                new IntelGraphicsControlAdapter(),
                new AmdGraphicsControlAdapter(),
                new VendorControlEligibilityAdapter(),
                new WindowsPeripheralInventoryAdapter(),
                new LibreHardwareMonitorAdapter()
            ]);
            snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        }

        Write(snapshot, json, PrintSnapshot);
        return snapshot.AdapterHealth.Any(health => !health.Healthy) ? 3 : 0;
    }

    private static async Task<int> RuntimePreflightAsync(bool json)
    {
        string clientVersion = RuntimeVersion.Get(typeof(Cli).Assembly);
        ServiceRuntimeCompatibilityV1 compatibility;
        try
        {
            NamedPipeRequestClient client = new(ProtocolConstants.ServicePipeName);
            IpcResponse response = await client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.Handshake,
                    new HandshakeRequestV2(
                        "RigPilot CLI",
                        clientVersion,
                        ProtocolConstants.Version,
                        ProtocolConstants.Version)),
                CancellationToken.None);
            if (!response.Success)
            {
                string detail = string.IsNullOrWhiteSpace(response.Error)
                    ? response.ErrorCode ?? "unknown service error"
                    : $"{response.ErrorCode}: {response.Error}";
                compatibility = ServiceRuntimeCompatibility.Unavailable(
                    clientVersion,
                    $"The installed service rejected the protocol-2 handshake ({detail}). Update the app and service together.");
            }
            else
            {
                HandshakeResponseV2? current = IpcJson.FromElement<HandshakeResponseV2>(response.Payload);
                compatibility = current is { SelectedProtocolVersion: > 0 }
                    ? ServiceRuntimeCompatibility.Evaluate(clientVersion, current)
                    : ServiceRuntimeCompatibility.EvaluateLegacy(
                        clientVersion,
                        IpcJson.FromElement<HandshakeResponse>(response.Payload));
            }
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            compatibility = ServiceRuntimeCompatibility.Unavailable(
                clientVersion,
                $"The service handshake could not complete: {exception.Message}");
        }

        Write(compatibility, json, value => Console.WriteLine(
            $"{value.State}: {value.Summary}"));
        return compatibility.CanUseServiceWrites ? 0 : 3;
    }

    private static async Task<int> ServiceCommandAsync<T>(IpcCommand command, bool json)
    {
        T payload = await SendAsync<T>(command);
        Write(payload, json, value => Console.WriteLine(value));
        return 0;
    }

    private static async Task<int> ReadRyzenSmuFeasibilityAsync(bool json)
    {
        RyzenSmuFeasibilityV1 result = await SendAsync<RyzenSmuFeasibilityV1>(IpcCommand.ReadRyzenSmuFeasibility);
        Write(result, json, value =>
        {
            Console.WriteLine($"Ryzen SMU feasibility: {value.Outcome}. {value.Message}");
            if (value.Outcome == RyzenSmuFeasibilityOutcome.Succeeded)
            {
                Console.WriteLine($"  SMU firmware {value.SmuFirmwareVersion}, PM table {value.PmTableVersion}, codename id {value.CodeNameId}");
                Console.WriteLine($"  PPT {value.PptValueWatts,7:0.##} / {value.PptLimitWatts,7:0.##} W");
                Console.WriteLine($"  TDC {value.TdcValueAmperes,7:0.##} / {value.TdcLimitAmperes,7:0.##} A");
                Console.WriteLine($"  EDC {value.EdcValueAmperes,7:0.##} / {value.EdcLimitAmperes,7:0.##} A");
                Console.WriteLine($"  THM {value.ThmValueCelsius,7:0.##} / {value.ThmLimitCelsius,7:0.##} °C");
            }
        });
        return result.Outcome == RyzenSmuFeasibilityOutcome.Succeeded ? 0 : 3;
    }

    private static async Task<int> DiscoverHidAsync(bool json)
    {
        HidInventoryResultV1 result = await SendAsync<HidInventoryResultV1>(IpcCommand.DiscoverHidInventory);
        Write(result, json, value =>
        {
            Console.WriteLine($"HID inventory: {value.Outcome} — {value.Devices.Count} device(s). {value.Detail}");
            foreach (IGrouping<string, HidDeviceInventoryItemV1> group in value.Devices
                .GroupBy(device => device.DeviceClass)
                .OrderByDescending(group => group.Count()))
            {
                Console.WriteLine($"  {group.Key,-18} {group.Count()}");
            }

            foreach (HidDeviceInventoryItemV1 device in value.Devices
                .Where(device => device.ProductName is not null)
                .Take(25))
            {
                Console.WriteLine(
                    $"    {device.DeviceClass,-16} VID={device.VendorId:X4} PID={device.ProductId:X4} " +
                    $"usage={device.UsagePage:X2}:{device.Usage:X2}  {device.ProductName}");
            }
        });
        return result.Outcome == HidInventoryOutcome.Succeeded ? 0 : 3;
    }

    private static async Task<int> OperationAsync(string[] args, bool json)
    {
        string? operationId = Option(args, "--id")?.Trim();
        if (HasFlag(args, "--id") && (string.IsNullOrWhiteSpace(operationId) || operationId.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new ArgumentException("--id requires an operation identifier.", nameof(args));
        }
        if (operationId is { Length: > 128 })
        {
            throw new ArgumentOutOfRangeException(nameof(args), "--id must contain at most 128 characters.");
        }

        HardwareOperationStatus? operation = string.IsNullOrWhiteSpace(operationId)
            ? await SendAsync<HardwareOperationStatus?>(IpcCommand.GetOperationStatus).ConfigureAwait(false)
            : await SendAsync<HardwareOperationStatus>(
                IpcCommand.GetOperationById,
                new OperationLookupRequest(operationId)).ConfigureAwait(false);
        Write(operation, json, value => Console.WriteLine(value));
        return 0;
    }

    /// <summary>
    /// Stores a service-owned physical-output classification. This is a typed
    /// policy mutation only: it never opens a hardware adapter and cannot
    /// change fan duty, calibration, or firmware/default control.
    /// </summary>
    private static async Task<int> SetCoolingOutputRoleAsync(string[] args, bool json)
    {
        string capabilityId = RequiredOption(args, "--capability");
        string headerName = RequiredOption(args, "--header").Trim();
        string? rpmSensorId = Option(args, "--rpm-sensor")?.Trim();
        string roleText = RequiredOption(args, "--role");
        if (!Enum.TryParse(roleText, ignoreCase: true, out CoolingOutputRole role)
            || !Enum.IsDefined(role))
        {
            throw new ArgumentException(
                "--role must be one of Unknown, CaseFan, CpuFan, or Pump.",
                nameof(args));
        }

        if (role is CoolingOutputRole.CpuFan or CoolingOutputRole.Pump
            && !HasFlag(args, "--confirm-safety-role"))
        {
            throw new InvalidOperationException(
                "Saving a CPU-fan or pump role requires --confirm-safety-role. This policy protects the output; it does not issue a fan command.");
        }

        HardwareSnapshot snapshot = await SendAsync<HardwareSnapshot>(IpcCommand.GetInventory);
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, capabilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected cooling control was not discovered by the current service snapshot.");
        if (capability.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety)
            || capability.ValueKind != ControlValueKind.Numeric)
        {
            throw new InvalidOperationException("Only a detected numeric cooling output can receive a physical-output role.");
        }

        if (!string.IsNullOrWhiteSpace(rpmSensorId))
        {
            SensorSample? rpm = snapshot.Sensors.FirstOrDefault(item =>
                string.Equals(item.SensorId, rpmSensorId, StringComparison.Ordinal));
            if (rpm is null
                || !string.Equals(rpm.Unit, "RPM", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rpm.AdapterId, capability.AdapterId, StringComparison.Ordinal)
                || !string.Equals(rpm.DeviceId, capability.DeviceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "--rpm-sensor must identify an RPM sensor from the same exact controller as --capability.");
            }
        }

        ServiceStatus status = await SendAsync<ServiceStatus>(IpcCommand.GetServiceStatus);
        CoolingOutputAssignmentV1 assignment = new(
            CoolingOutputAssignmentV1.CurrentSchemaVersion,
            capability.Id,
            capability.Id,
            capability.AdapterId,
            capability.DeviceId,
            string.IsNullOrWhiteSpace(rpmSensorId) ? null : rpmSensorId,
            headerName,
            role,
            DateTimeOffset.UtcNow,
            "Saved through the typed RigPilot CLI physical-output role command.");
        IpcResponse response = await SendResponseAsync(
            IpcCommand.SaveCoolingOutputAssignment,
            new CoolingOutputAssignmentUpdateRequest(
                assignment,
                ConfirmRemoveSafetyProtection: HasFlag(args, "--confirm-remove-safety-protection")),
            status.StateRevision,
            Guid.NewGuid().ToString("N"));
        CoolingOutputAssignmentSaveResultV1 result = IpcJson.FromElement<CoolingOutputAssignmentSaveResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty cooling-output assignment result.");
        Write(result, json, value => Console.WriteLine(
            value.Removed
                ? $"Removed physical-output role for {value.Assignment.CapabilityId}. No hardware command was sent."
                : $"Stored {value.Assignment.Role} role for {value.Assignment.HeaderName}. No hardware command was sent."));
        return 0;
    }

    /// <summary>
    /// Records an operator's case-fan confirmation for an existing
    /// identification session. The confirmation changes only persisted
    /// commissioning state; it cannot issue a fan command.
    /// </summary>
    private static async Task<int> ConfirmCaseFanAsync(string[] args, bool json)
    {
        string sessionId = RequiredOption(args, "--session");
        string headerName = RequiredOption(args, "--header").Trim();
        if (!HasFlag(args, "--confirm-case-fan"))
        {
            throw new InvalidOperationException(
                "confirm-case-fan requires --confirm-case-fan. It records a user-declared case-fan mapping and sends no hardware command.");
        }

        IReadOnlyList<FanCommissioningSessionV1> sessions = await SendAsync<IReadOnlyList<FanCommissioningSessionV1>>(
            IpcCommand.GetFanCommissioningSessions);
        FanCommissioningSessionV1 session = sessions.FirstOrDefault(item =>
            string.Equals(item.Id, sessionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The requested commissioning session was not found.");
        if (session.State != FanCommissioningState.AwaitingIdentification || session.IsCpuOrPump)
        {
            throw new InvalidOperationException(
                "Only an awaiting-identification, non-CPU/non-pump session can be confirmed as a case fan.");
        }
        if (!FanCommissioningWorkflow.IsDeclaredChassisHeader(headerName))
        {
            throw new InvalidOperationException(
                "--header must use an explicit case/chassis header alias, for example CASE_FAN_1 or CHA_FAN1.");
        }

        ServiceStatus status = await SendAsync<ServiceStatus>(IpcCommand.GetServiceStatus);
        IpcResponse response = await SendResponseAsync(
            IpcCommand.ConfirmFanCommissioning,
            new ConfirmFanCommissioningRequest(
                session.Id,
                HeaderConfirmed: true,
                headerName,
                "User-declared generic case-fan mapping. Physical location remains a generic alias.",
                PhysicalHeaderObserved: false),
            status.StateRevision,
            Guid.NewGuid().ToString("N"));
        FanCommissioningSessionV1 confirmed = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty confirmed commissioning session.");
        Write(confirmed, json, value => Console.WriteLine(
            $"{value.HeaderName} recorded as a user-declared case fan. No hardware command was sent and physical header observation remains pending."));
        return confirmed.State == FanCommissioningState.ReadyForCalibration && confirmed.HeaderConfirmed ? 0 : 3;
    }

    /// <summary>
    /// Runs the complete bounded case-fan calibration workflow. It requires an
    /// already-confirmed session, an explicit stop/restart acknowledgement,
    /// and a same-controller temperature ceiling. The service restores the
    /// original or firmware/default policy on every exit path.
    /// </summary>
    private static async Task<int> CalibrateCaseFanAsync(string[] args, bool json)
    {
        string sessionId = RequiredOption(args, "--session");
        string capabilityId = RequiredOption(args, "--capability");
        string rpmSensorId = RequiredOption(args, "--rpm-sensor");
        string temperatureSensorId = RequiredOption(args, "--temperature-sensor");
        double temperatureLimit = RequiredDoubleOption(args, "--temperature-limit", 40, 110);
        int settlingSeconds = OptionalIntOption(args, "--settling-seconds", 2, 1, 10);
        int restartCycles = OptionalIntOption(args, "--restart-cycles", 2, 2, 3);
        int timeoutSeconds = OptionalIntOption(args, "--timeout-seconds", 300, 60, 600);
        if (!HasFlag(args, "--confirm-experimental")
            || !HasFlag(args, "--confirm-device")
            || !HasFlag(args, "--allow-fan-stop"))
        {
            throw new InvalidOperationException(
                "calibrate-case-fan requires --confirm-experimental, --confirm-device, and --allow-fan-stop. It will deliberately measure the stop and restart thresholds.");
        }

        IReadOnlyList<FanCommissioningSessionV1> sessions = await SendAsync<IReadOnlyList<FanCommissioningSessionV1>>(
            IpcCommand.GetFanCommissioningSessions);
        FanCommissioningSessionV1 session = sessions.FirstOrDefault(item =>
            string.Equals(item.Id, sessionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The requested commissioning session was not found.");
        if (session.State != FanCommissioningState.ReadyForCalibration
            || !session.HeaderConfirmed
            || session.IsCpuOrPump
            || !string.Equals(session.CapabilityId, capabilityId, StringComparison.Ordinal)
            || !string.Equals(session.RpmSensorId, rpmSensorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The session must be a confirmed case fan and match the requested capability and RPM sensor.");
        }

        HardwareSnapshot snapshot = await SendAsync<HardwareSnapshot>(IpcCommand.GetInventory);
        CapabilityDescriptor capability = snapshot.Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, capabilityId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected cooling control was not discovered by the current service snapshot.");
        SensorSample? rpm = snapshot.Sensors.FirstOrDefault(item =>
            string.Equals(item.SensorId, rpmSensorId, StringComparison.Ordinal));
        SensorSample? thermal = snapshot.Sensors.FirstOrDefault(item =>
            string.Equals(item.SensorId, temperatureSensorId, StringComparison.Ordinal));
        if (capability.Domain is not (ControlDomain.Cooling or ControlDomain.CoolingSafety)
            || capability.ValueKind != ControlValueKind.Numeric
            || rpm is null
            || !string.Equals(rpm.Unit, "RPM", StringComparison.OrdinalIgnoreCase)
            || thermal is null
            || !string.Equals(thermal.Unit, "°C", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rpm.AdapterId, capability.AdapterId, StringComparison.Ordinal)
            || !string.Equals(rpm.DeviceId, capability.DeviceId, StringComparison.Ordinal)
            || !string.Equals(thermal.AdapterId, capability.AdapterId, StringComparison.Ordinal)
            || !string.Equals(thermal.DeviceId, capability.DeviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The requested control, RPM sensor, and temperature safety sensor must be present on the same exact controller.");
        }

        ServiceStatus status = await SendAsync<ServiceStatus>(IpcCommand.GetServiceStatus);
        StartCalibrationRequest request = new(
            capability.Id,
            rpm.SensorId,
            ConfirmExperimental: true,
            ConfirmDevice: true,
            AllowFanStop: true,
            SettlingTime: TimeSpan.FromSeconds(settlingSeconds),
            StableSampleCount: 3,
            MaximumSampleCount: 15,
            SampleInterval: TimeSpan.FromMilliseconds(500),
                StabilityTolerancePercent: 10,
                RestartVerificationCycles: restartCycles,
                TemperatureLimits: [new FanCalibrationTemperatureLimit(thermal.SensorId, temperatureLimit)],
                CommissioningSessionId: session.Id);
        IpcResponse startResponse = await SendResponseAsync(
            IpcCommand.StartCalibration,
            request,
            status.StateRevision,
            Guid.NewGuid().ToString("N"));
        HardwareOperationStatus operation = IpcJson.FromElement<HardwareOperationStatus>(startResponse.Payload)
            ?? throw new InvalidDataException("The service returned an empty calibration operation.");
        HardwareOperationStatus completed = await WaitForTerminalOperationAsync(
            operation.Id,
            TimeSpan.FromSeconds(timeoutSeconds));

        FanCommissioningSessionV1? completedSession = null;
        if (completed.State == HardwareOperationState.Completed
            && completed.CalibrationResult is { RestartVerified: true })
        {
            ServiceStatus completionStatus = await SendAsync<ServiceStatus>(IpcCommand.GetServiceStatus);
            IpcResponse completeResponse = await SendResponseAsync(
                IpcCommand.CompleteFanCommissioning,
                new FanCommissioningSessionRequest(session.Id),
                completionStatus.StateRevision,
                Guid.NewGuid().ToString("N"));
            completedSession = IpcJson.FromElement<FanCommissioningSessionV1>(completeResponse.Payload)
                ?? throw new InvalidDataException("The service returned an empty completed commissioning session.");
        }

        string summary = completed.State == HardwareOperationState.Completed && completed.CalibrationResult?.RestartVerified == true
            ? "Calibration and repeated restart verification completed; the commissioning session was finalised."
            : completed.State == HardwareOperationState.Completed && completed.CalibrationResult?.StallDutyPercent is null
                ? "Calibration completed and the prior policy was restored, but the fan stayed running at the requested minimum duty. Restart behaviour remains unproven; zero-RPM and curve activation remain disabled."
                : "Calibration did not produce a restart-verified result; commissioning remains incomplete and no curve should be enabled.";
        CalibrationCliResult result = new(
            SchemaVersion: 1,
            SessionId: session.Id,
            CapabilityId: capability.Id,
            RpmSensorId: rpm.SensorId,
            TemperatureSensorId: thermal.SensorId,
            TemperatureLimitCelsius: temperatureLimit,
            Operation: completed,
            CommissioningSession: completedSession,
            Summary: summary);
        Write(result, json, value => Console.WriteLine(value.Summary));
        return completed.State == HardwareOperationState.Completed
            && completed.CalibrationResult?.RestartVerified == true
            && completedSession?.State == FanCommissioningState.Completed
            ? 0
            : 3;
    }

    private static async Task<int> TraceAsync(bool json)
    {
        IReadOnlyList<AdapterTraceEvent> trace = await SendAsync<IReadOnlyList<AdapterTraceEvent>>(IpcCommand.GetAdapterTrace);
        Write(trace, json, events =>
        {
            foreach (AdapterTraceEvent item in events)
            {
                string capability = string.IsNullOrWhiteSpace(item.CapabilityId) ? string.Empty : $" {item.CapabilityId}";
                Console.WriteLine($"{item.Timestamp:O} {(item.Success ? "OK" : "FAIL")} {item.AdapterId} {item.Operation}{capability}: {item.Message}");
            }
        });
        return trace.Any(item => !item.Success) ? 3 : 0;
    }

    private static async Task<int> UserCommandAsync<T>(IpcCommand command, bool json)
    {
        T payload = await SendAsync<T>(ProtocolConstants.UserAgentPipeName, command, payload: null);
        Write(payload, json, value => Console.WriteLine(value));
        return 0;
    }

    private static async Task<int> PreviewAfterburnerAsync(string[] args, bool json)
    {
        string file = RequiredOption(args, "--file");
        string section = Option(args, "--section") ?? "Profile1";
        ProfileImportPreviewV1 preview = await SendAsync<ProfileImportPreviewV1>(
            ProtocolConstants.ServicePipeName,
            IpcCommand.PreviewAfterburnerImport,
            new AfterburnerImportRequest(file, section));
        Write(preview, json, value => Console.WriteLine(
            $"{value.SourceProfile}: {value.Settings.Count(setting => setting.State is ImportMappingState.Mapped or ImportMappingState.ManualOnly)} mapped, "
            + $"{value.Settings.Count(setting => setting.State == ImportMappingState.Unmapped)} unmapped, {value.Warnings.Count} warnings."));
        return preview.Profile?.HardwareActions.Count > 0 ? 0 : 3;
    }

    private static async Task<int> PreviewFanControlAsync(string[] args, bool json)
    {
        string file = RequiredOption(args, "--file");
        CoolingImportPreviewV1 preview = await SendAsync<CoolingImportPreviewV1>(
            ProtocolConstants.ServicePipeName,
            IpcCommand.PreviewFanControlImport,
            new FanControlImportRequest(
                file,
                new Dictionary<string, string>(),
                new Dictionary<string, string>()));
        Write(preview, json, value => Console.WriteLine(
            $"Fan Control preview: {value.Graph?.Nodes.Count ?? 0} graph nodes, {value.Graph?.Outputs.Count ?? 0} mapped outputs, {value.Warnings.Count} warnings."));
        return 0;
    }

    private static async Task<int> InspectAdapterPackAsync(string[] args, bool json)
    {
        string file = RequiredOption(args, "--file");
        AdapterPackInspection inspection = await SendAsync<AdapterPackInspection>(
            ProtocolConstants.ServicePipeName,
            IpcCommand.InspectAdapterPack,
            new InspectAdapterPackRequest(file, AllowDevelopmentTrust: false));
        Write(inspection, json, value => Console.WriteLine(
            $"{(value.Valid ? "Valid" : "Rejected")} adapter pack {value.Manifest?.Id ?? "unknown"} {value.Manifest?.Version ?? string.Empty}: "
            + $"signature={value.SignatureValid}, errors={value.Errors.Count}, warnings={value.Warnings.Count}"));
        return inspection.Valid ? 0 : 3;
    }

    private static async Task<int> StopConflictingProcessesAsync(string[] args, bool json)
    {
        string? only = Option(args, "--id");
        IReadOnlyList<string> ids = only is null ? [] : [only];
        IpcResponse response = await SendResponseAsync(
            IpcCommand.StopConflictingProcesses,
            new StopConflictingProcessesRequestV1(
                StopConflictingProcessesRequestV1.CurrentSchemaVersion, ids, HasFlag(args, "--confirm")));
        StopConflictingProcessesResultV1 result = IpcJson.FromElement<StopConflictingProcessesResultV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(result, json, value => Console.WriteLine(value.Message));
        return result.Results.Count == 0 || result.TerminatedCount == result.Results.Count ? 0 : 3;
    }

    private static async Task<int> SetKrakenPumpAsync(string[] args, bool json)
    {
        if (!int.TryParse(Option(args, "--duty"), out int duty))
        {
            Console.Error.WriteLine("kraken-pump requires --duty <60..100>.");
            return 64;
        }

        IpcResponse response = await SendResponseAsync(
            IpcCommand.SetKrakenPumpDuty,
            new KrakenPumpRequestV1(
                KrakenPumpRequestV1.CurrentSchemaVersion,
                duty,
                HasFlag(args, "--confirm-experimental"),
                Option(args, "--confirm-device")));
        KrakenPumpResultV1 result = IpcJson.FromElement<KrakenPumpResultV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(result, json, value => Console.WriteLine(
            $"Kraken pump: {value.Outcome}. requested={value.RequestedDutyPercent}% observed={value.ObservedDutyPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}% rpm={value.ObservedPumpRpm?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}. {value.Message}"));
        return result.Outcome is KrakenPumpOutcome.ReadBackVerified or KrakenPumpOutcome.WriteIssued ? 0 : 3;
    }

    private static async Task<int> SetKrakenLightingAsync(string[] args, bool json)
    {
        bool off = HasFlag(args, "--off");
        string colour = Option(args, "--colour") ?? string.Empty;
        IpcResponse response = await SendResponseAsync(
            IpcCommand.SetKrakenLighting,
            new KrakenLightingRequestV1(
                KrakenLightingRequestV1.CurrentSchemaVersion,
                colour,
                off,
                HasFlag(args, "--confirm-experimental"),
                Option(args, "--confirm-device")));
        KrakenLightingResultV1 result = IpcJson.FromElement<KrakenLightingResultV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(result, json, value => Console.WriteLine($"Kraken lighting: {value.Outcome}. {value.Message}"));
        return result.Outcome == KrakenLightingOutcome.WriteIssued ? 0 : 3;
    }

    private static async Task<int> SetRazerLightingAsync(string[] args, bool json)
    {
        // Drives the same service IPC path the App's "Apply to all lighting" uses for the
        // Razer O11 (IpcCommand.SetRazerRgb -> SetRazerRgbAsync -> contained --set-razer-custom),
        // so the full round-trip can be exercised from the command line.
        bool off = HasFlag(args, "--off");
        string colour = Option(args, "--colour") ?? string.Empty;
        IpcResponse response = await SendResponseAsync(
            IpcCommand.SetRazerRgb,
            new RazerRgbRequestV1(
                RazerRgbRequestV1.CurrentSchemaVersion,
                colour,
                off,
                HasFlag(args, "--confirm-experimental"),
                Option(args, "--confirm-device")));
        RazerRgbResultV1 result = IpcJson.FromElement<RazerRgbResultV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(result, json, value => Console.WriteLine($"Razer lighting: {value.Outcome}. {value.Message}"));
        return result.Outcome == KrakenLightingOutcome.WriteIssued ? 0 : 3;
    }

    private static async Task<int> SetGpuFanArmedAsync(string[] args, bool json, bool arm)
    {
        bool confirmExperimental = HasFlag(args, "--confirm-experimental");
        string? device = Option(args, "--confirm-device");
        IReadOnlyList<string> confirmedDevices = device is null ? [] : [device];
        IpcResponse response = await SendResponseAsync(
            IpcCommand.SetGpuFanControlArmed,
            new SetGpuFanControlArmedRequest(arm, confirmExperimental, confirmedDevices));
        GpuFanControlStatus status = IpcJson.FromElement<GpuFanControlStatus>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(status, json, value => Console.WriteLine(
            $"GPU fan control: available={value.Available} armed={value.Armed} device={value.DeviceId}. {value.Message}"));
        return status.Available ? 0 : 3;
    }

    private static async Task<int> SetGpuPowerArmedAsync(string[] args, bool json, bool arm)
    {
        bool confirmExperimental = HasFlag(args, "--confirm-experimental");
        string? device = Option(args, "--confirm-device");
        IReadOnlyList<string> confirmedDevices = device is null ? [] : [device];
        IpcResponse response = await SendResponseAsync(
            IpcCommand.SetGpuPowerLimitArmed,
            new SetGpuPowerLimitArmedRequest(arm, confirmExperimental, confirmedDevices));
        GpuPowerLimitStatus status = IpcJson.FromElement<GpuPowerLimitStatus>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(status, json, value => Console.WriteLine(
            $"GPU power limit: available={value.Available} armed={value.Armed} device={value.DeviceId}. {value.Message}"));
        return status.Available ? 0 : 3;
    }

    private static async Task<int> SetGpuClockArmedAsync(string[] args, bool json, bool arm)
    {
        bool confirmExperimental = HasFlag(args, "--confirm-experimental");
        string? device = Option(args, "--confirm-device");
        IReadOnlyList<string> confirmedDevices = device is null ? [] : [device];
        IpcResponse response = await SendResponseAsync(
            IpcCommand.SetGpuClockOffsetArmed,
            new SetGpuClockOffsetArmedRequest(arm, confirmExperimental, confirmedDevices));
        GpuClockOffsetStatus status = IpcJson.FromElement<GpuClockOffsetStatus>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(status, json, value => Console.WriteLine(
            $"GPU clock offset: available={value.Available} armed={value.Armed} device={value.DeviceId}. {value.Message}"));
        return status.Available ? 0 : 3;
    }

    private static async Task<int> SetCpuTuningArmedAsync(string[] args, bool json, bool arm)
    {
        bool confirmExperimental = HasFlag(args, "--confirm-experimental");
        string? device = Option(args, "--confirm-device");
        IReadOnlyList<string> confirmedDevices = device is null ? [] : [device];
        // Unchecked send: arming is refused by the qualification gate on every
        // system today, and the refusal carries a status payload worth printing.
        IpcResponse response = await SendUncheckedResponseAsync(
            ProtocolConstants.ServicePipeName,
            IpcCommand.SetCpuTuningArmed,
            new SetCpuTuningArmedRequest(arm, confirmExperimental, confirmedDevices));
        CpuTuningStatus? status = IpcJson.FromElement<CpuTuningStatus>(response.Payload);
        if (status is null)
        {
            throw new InvalidOperationException($"{response.ErrorCode}: {response.Error}");
        }

        Write(status, json, value => Console.WriteLine(
            $"CPU PBO tuning: available={value.Available} qualified={value.Qualified} armed={value.Armed}. {value.Message}"));
        return response.Success ? 0 : 3;
    }

    private static async Task<int> InstallAdapterPackAsync(string[] args, bool json)
    {
        string file = RequiredOption(args, "--file");
        bool confirmDevelopmentTrust = HasFlag(args, "--confirm-development-trust");
        IpcResponse response = await SendResponseAsync(
            IpcCommand.InstallAdapterPack,
            new InstallAdapterPackRequest(file, confirmDevelopmentTrust));
        JsonElement payload = response.Payload ?? throw new InvalidDataException("Service returned an empty payload.");
        Write(payload, json, value => Console.WriteLine(
            $"Installed adapter pack to {value.GetProperty("installedPath").GetString()}"));
        return 0;
    }

    private static async Task<int> RemoveAdapterPackAsync(string[] args, bool json)
    {
        string packId = RequiredOption(args, "--id");
        string version = RequiredOption(args, "--version");
        IpcResponse response = await SendResponseAsync(
            IpcCommand.RemoveAdapterPack,
            new RemoveAdapterPackRequest(packId, version));
        Write(response.Payload, json, _ => Console.WriteLine($"Removed adapter pack {packId}@{version}."));
        return 0;
    }

    private static async Task<int> ExportReportAsync(string[] args, bool json)
    {
        CompatibilityReportV1 report;
        try
        {
            report = await SendAsync<CompatibilityReportV1>(IpcCommand.ExportReport);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await using AdapterCoordinator coordinator = new(
            [
                new SystemInventoryAdapter(),
                new WindowsPowerAdapter(),
                new NvmlTelemetryAdapter(),
                new IntelGraphicsControlAdapter(),
                new AmdGraphicsControlAdapter(),
                new VendorControlEligibilityAdapter(),
                new WindowsPeripheralInventoryAdapter(),
                new LibreHardwareMonitorAdapter()
            ]);
            HardwareSnapshot snapshot = await coordinator.CaptureAsync(CancellationToken.None);
            report = CompatibilityReportBuilder.Build(
                snapshot,
                "0.6.0-beta.1",
                new Dictionary<string, string>
                {
                    ["framework"] = Environment.Version.ToString(),
                    ["osVersion"] = Environment.OSVersion.VersionString
                },
                [],
                userApproved: false);
        }

        int outputIndex = Array.FindIndex(args, item => string.Equals(item, "--output", StringComparison.OrdinalIgnoreCase));
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
        {
            string path = Path.GetFullPath(args[outputIndex + 1]);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonDefaults.Options));
            Console.WriteLine($"Redacted report preview written to {path}. It has not been uploaded or approved.");
        }
        else
        {
            Write(report, json: true, _ => { });
        }

        return 0;
    }

    private static async Task<int> QualificationAsync(string[] args, bool json)
    {
        string ledger = RequiredOption(args, "--ledger");
        string fullPath = Path.GetFullPath(ledger);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Qualification ledger was not found.", fullPath);
        }

        string content = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
        IReadOnlyList<HardwareQualificationRecordV1>? records = JsonSerializer.Deserialize<IReadOnlyList<HardwareQualificationRecordV1>>(
            content,
            JsonDefaults.Options);
        if (records is null)
        {
            throw new InvalidDataException("Qualification ledger is empty or malformed.");
        }

        QualificationMatrixStatusV1 status = QualificationMatrix.Evaluate(records);
        Write(status, json, PrintQualification);
        return status.CanReleaseV1 ? 0 : 3;
    }

    /// <summary>
    /// Produces an UNSIGNED DRAFT qualification record from the real local
    /// hardware identity plus explicit operator attestations. The builder
    /// hard-codes SignedProductionBuild=false, so a draft can never satisfy the
    /// 18-system 1.0 gate; it exists so community contributors can prepare and
    /// review their evidence before the signed-build pipeline re-captures it.
    /// </summary>
    private static async Task<int> QualificationDraftAsync(string[] args, bool json)
    {
        string output = Path.GetFullPath(RequiredOption(args, "--output"));
        bool confirmWitnessed = HasFlag(args, "--confirm-witnessed");
        QualificationAttestations attestations = new(
            HasFlag(args, "--attest-no-bsod"),
            HasFlag(args, "--attest-no-stuck-fan"),
            HasFlag(args, "--attest-no-unauthorised-write"),
            HasFlag(args, "--attest-rollback-passed"));
        if (!confirmWitnessed || !attestations.AllAttested)
        {
            Console.Error.WriteLine(
                "A qualification draft requires --confirm-witnessed plus all four attestations "
                + "(--attest-no-bsod --attest-no-stuck-fan --attest-no-unauthorised-write --attest-rollback-passed). "
                + "Each one asserts something you personally observed on this system; a record is never fabricated.");
            return 3;
        }

        QualificationSystemIdentity identity = ReadLocalSystemIdentity();
        HardwareQualificationRecordV1 draft = QualificationRecordDraftBuilder.Build(
            identity,
            attestations,
            DateTimeOffset.UtcNow,
            Option(args, "--notes"));

        List<HardwareQualificationRecordV1> ledger = [];
        if (File.Exists(output))
        {
            string existing = await File.ReadAllTextAsync(output).ConfigureAwait(false);
            ledger = JsonSerializer.Deserialize<List<HardwareQualificationRecordV1>>(existing, JsonDefaults.Options)
                ?? throw new InvalidDataException("The existing ledger file is malformed; refusing to overwrite it.");
        }

        ledger.Add(draft);
        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(ledger, LedgerJsonOptions)).ConfigureAwait(false);

        Write(draft, json, value => Console.WriteLine(
            $"Draft {value.ReportId} for {value.SystemId}: {value.ProcessorFamily} + {value.GraphicsFamily} on a {value.MotherboardVendor} board ({value.PlatformFamily}). "
            + $"signedProductionBuild={value.SignedProductionBuild}. Appended to {output}."));
        return 0;
    }

    private static readonly JsonSerializerOptions LedgerJsonOptions = new(JsonDefaults.Options) { WriteIndented = true };

    private static QualificationSystemIdentity ReadLocalSystemIdentity()
    {
        string cpu = FirstWmiValue("Win32_Processor", "Name") ?? string.Empty;
        string boardVendor = FirstWmiValue("Win32_BaseBoard", "Manufacturer") ?? string.Empty;
        string boardProduct = FirstWmiValue("Win32_BaseBoard", "Product") ?? string.Empty;

        // Pick the first display adapter that maps to a qualification family so
        // integrated graphics on a discrete-GPU system do not shadow the result.
        string gpu = string.Empty;
        using (System.Management.ManagementObjectSearcher searcher = new("SELECT Name FROM Win32_VideoController"))
        {
            foreach (System.Management.ManagementBaseObject row in searcher.Get())
            {
                string candidate = row["Name"]?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(gpu))
                {
                    gpu = candidate;
                }

                if (QualificationRecordDraftBuilder.TryClassifyGraphics(candidate) is not null)
                {
                    gpu = candidate;
                    break;
                }
            }
        }

        return new QualificationSystemIdentity(cpu, gpu, boardVendor, boardProduct);
    }

    private static string? FirstWmiValue(string table, string property)
    {
        using System.Management.ManagementObjectSearcher searcher = new($"SELECT {property} FROM {table}");
        foreach (System.Management.ManagementBaseObject row in searcher.Get())
        {
            string? value = row[property]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs LibreHardwareMonitor's Prepare path in the caller's own process.
    /// This is an execution-context diagnostic: it never connects to the
    /// RigPilot service and never calls Apply, Verify, Rollback, or Reset.
    /// It is deliberately used to distinguish a LocalSystem/PawnIO failure
    /// from the private Adapter Host transport.
    /// </summary>
    private static async Task<int> DirectPrepareAsync(string[] args, bool json)
    {
        string capabilityId = RequiredOption(args, "--capability");
        if (!HasFlag(args, "--confirm-no-write"))
        {
            throw new InvalidOperationException(
                "direct-prepare requires --confirm-no-write. It is a diagnostic only and cannot issue a hardware command.");
        }

        await using LibreHardwareMonitorAdapter adapter = new();
        AdapterProbeResult probe = await adapter.ProbeAsync(CancellationToken.None).ConfigureAwait(false);
        CapabilityDescriptor? capability = probe.Capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, capabilityId, StringComparison.Ordinal));
        if (capability is null)
        {
            DirectPrepareCliResult unavailable = new(
                SchemaVersion: 1,
                CapabilityId: capabilityId,
                ProcessIdentity: GetProcessIdentityKind(),
                Prepared: false,
                ApplyIssued: false,
                VerifyIssued: false,
                RollbackIssued: false,
                ResetIssued: false,
                RequestedDutyPercent: null,
                PreviousDutyPercent: null,
                FailureStage: "ProbeCapabilityLookup",
                ExceptionType: "CapabilityUnavailable",
                HResult: null,
                Win32Error: null,
                Summary: "The requested cooling control is unavailable in the direct local probe. No hardware control operation was issued.");
            await WriteDirectPrepareResultAsync(unavailable, args, json).ConfigureAwait(false);
            return 3;
        }

        DirectPrepareCliResult result;
        try
        {
            PreparedAction prepared = await FanCommissioningWorkflow.PrepareIdentificationPulseAsync(
                capability,
                adapter,
                CancellationToken.None).ConfigureAwait(false);
            result = new DirectPrepareCliResult(
                SchemaVersion: 1,
                CapabilityId: capability.Id,
                ProcessIdentity: GetProcessIdentityKind(),
                Prepared: true,
                ApplyIssued: false,
                VerifyIssued: false,
                RollbackIssued: false,
                ResetIssued: false,
                RequestedDutyPercent: prepared.Action.Value.Numeric,
                PreviousDutyPercent: prepared.PreviousValue?.Numeric,
                FailureStage: null,
                ExceptionType: null,
                HResult: null,
                Win32Error: null,
                Summary: "Direct adapter Prepare completed. No hardware control operation was issued.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Exception root = exception.GetBaseException();
            result = new DirectPrepareCliResult(
                SchemaVersion: 1,
                CapabilityId: capability.Id,
                ProcessIdentity: GetProcessIdentityKind(),
                Prepared: false,
                ApplyIssued: false,
                VerifyIssued: false,
                RollbackIssued: false,
                ResetIssued: false,
                RequestedDutyPercent: null,
                PreviousDutyPercent: null,
                FailureStage: exception.Data["PCHelper.AdapterStage"] as string,
                ExceptionType: root.GetType().Name,
                HResult: root.HResult,
                Win32Error: TryGetWin32Error(root.HResult),
                Summary: "Direct adapter Prepare failed. No hardware control operation was issued.");
        }

        await WriteDirectPrepareResultAsync(result, args, json).ConfigureAwait(false);
        return result.Prepared ? 0 : 3;
    }

    private static async Task WriteDirectPrepareResultAsync(
        DirectPrepareCliResult result,
        string[] args,
        bool json)
    {
        int outputIndex = Array.FindIndex(args, item => string.Equals(item, "--output", StringComparison.OrdinalIgnoreCase));
        if (outputIndex >= 0)
        {
            if (outputIndex + 1 >= args.Length)
            {
                throw new ArgumentException("--output requires a file path.", nameof(args));
            }

            string outputPath = Path.GetFullPath(args[outputIndex + 1]);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("--output must resolve to a file in a directory.", nameof(args));
            }

            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(result, JsonDefaults.Options)).ConfigureAwait(false);
        }

        Write(result, json, value => Console.WriteLine(value.Summary));
    }

    private static string GetProcessIdentityKind()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem)
        {
            return "LocalSystem";
        }

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            ? "ElevatedAdministrator"
            : "StandardUser";
    }

    private static int? TryGetWin32Error(int hResult)
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

    /// <summary>
    /// Runs the private Adapter Host Prepare phase only. This command cannot
    /// issue a fan write: it creates no operation and never calls Apply,
    /// Verify, Rollback, or Reset. Its purpose is to diagnose a controller
    /// before a user is asked to authorize another physical pulse.
    /// </summary>
    private static async Task<int> CommissionPreflightAsync(string[] args, bool json)
    {
        string capabilityId = RequiredOption(args, "--capability");
        string rpmSensorId = RequiredOption(args, "--rpm-sensor");
        string headerAlias = RequiredOption(args, "--header").Trim();
        if (!HasFlag(args, "--confirm-experimental") || !HasFlag(args, "--confirm-device"))
        {
            throw new InvalidOperationException(
                "A commissioning preflight requires both --confirm-experimental and --confirm-device.");
        }

        if (!HasFlag(args, "--provisional-case-alias"))
        {
            throw new InvalidOperationException(
                "A generic controller requires --provisional-case-alias. This command does not certify the physical header.");
        }

        if (!FanCommissioningWorkflow.IsDeclaredChassisHeader(headerAlias))
        {
            throw new InvalidOperationException(
                "--header must explicitly identify a chassis header, for example CHA_FAN1. Pump and CPU headers are forbidden.");
        }

        ServiceStatus status = await SendAsync<ServiceStatus>(IpcCommand.GetServiceStatus);
        BeginFanCommissioningRequest begin = new(
            capabilityId,
            rpmSensorId,
            headerAlias,
            IsCpuOrPump: false,
            AllowFanStop: false,
            Notes: "User-declared case-fan alias. This is a no-write controller preflight; physical header mapping remains provisional. AIO_PUMP is excluded.");
        IpcResponse beginResponse = await SendResponseAsync(
            IpcCommand.BeginFanCommissioning,
            begin,
            status.StateRevision,
            Guid.NewGuid().ToString("N"));
        FanCommissioningSessionV1 session = IpcJson.FromElement<FanCommissioningSessionV1>(beginResponse.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning session.");

        IpcResponse response = await SendUncheckedResponseAsync(
            ProtocolConstants.ServicePipeName,
            IpcCommand.PreflightFanCommissioning,
            new PreflightFanCommissioningRequest(
                session.Id,
                ConfirmExperimental: true,
                ConfirmDevice: true),
            beginResponse.StateRevision,
            Guid.NewGuid().ToString("N"));
        FanCommissioningPreflightResultV1 preflight = IpcJson.FromElement<FanCommissioningPreflightResultV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning preflight result.");
        CommissioningPreflightCliResult result = new(
            SchemaVersion: 1,
            CapabilityId: capabilityId,
            RpmSensorId: rpmSensorId,
            ProvisionalHeaderAlias: headerAlias,
            ServiceSucceeded: response.Success,
            ServiceErrorCode: response.ErrorCode,
            ServiceError: response.Error,
            Preflight: preflight,
            PhysicalHeaderCertified: false,
            FanStopEnabled: false,
            Summary: "This was a no-write adapter Prepare diagnostic. Physical header certification and calibration remain blocked.");
        Write(result, json, value => Console.WriteLine(
            $"{value.Preflight.OutcomeCode}: {value.ProvisionalHeaderAlias} remains provisional. {value.Preflight.Summary}"));
        return response.Success
            && preflight.Prepared
            && !preflight.ApplyIssued
            && !preflight.RollbackIssued
            && !preflight.ResetIssued
            ? 0
            : 3;
    }

    /// <summary>
    /// Runs only the bounded identity phase of fan commissioning. It deliberately
    /// does not confirm a physical header, calibrate a fan, enable fan-stop, or
    /// persist a hardware profile. A human-provided alias remains provisional
    /// until it is visually verified outside this command.
    /// </summary>
    private static async Task<int> CommissionPulseAsync(string[] args, bool json)
    {
        string capabilityId = RequiredOption(args, "--capability");
        string rpmSensorId = RequiredOption(args, "--rpm-sensor");
        string headerAlias = RequiredOption(args, "--header").Trim();
        string durationText = RequiredOption(args, "--duration-seconds");
        if (!int.TryParse(durationText, out int durationSeconds) || durationSeconds is < 2 or > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "--duration-seconds must be an integer from 2 through 5.");
        }

        if (!HasFlag(args, "--confirm-experimental") || !HasFlag(args, "--confirm-device"))
        {
            throw new InvalidOperationException(
                "A commissioning pulse requires both --confirm-experimental and --confirm-device.");
        }

        if (!HasFlag(args, "--provisional-case-alias"))
        {
            throw new InvalidOperationException(
                "A generic controller requires --provisional-case-alias. This command does not certify the physical header.");
        }

        if (!FanCommissioningWorkflow.IsDeclaredChassisHeader(headerAlias))
        {
            throw new InvalidOperationException(
                "--header must explicitly identify a chassis header, for example CHA_FAN1. Pump and CPU headers are forbidden.");
        }

        ServiceStatus status = await SendAsync<ServiceStatus>(IpcCommand.GetServiceStatus);
        long revision = status.StateRevision;
        BeginFanCommissioningRequest begin = new(
            capabilityId,
            rpmSensorId,
            headerAlias,
            IsCpuOrPump: false,
            AllowFanStop: false,
            Notes: "User-declared case-fan alias. Physical header mapping remains provisional until visual confirmation. AIO_PUMP is excluded.");
        IpcResponse beginResponse = await SendResponseAsync(
            IpcCommand.BeginFanCommissioning,
            begin,
            revision,
            Guid.NewGuid().ToString("N"));
        FanCommissioningSessionV1 session = IpcJson.FromElement<FanCommissioningSessionV1>(beginResponse.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning session.");

        IpcResponse pulseResponse = await SendResponseAsync(
            IpcCommand.PulseFanCommissioning,
            new PulseFanCommissioningRequest(
                session.Id,
                ConfirmExperimental: true,
                ConfirmDevice: true,
                Duration: TimeSpan.FromSeconds(durationSeconds)),
            beginResponse.StateRevision,
            Guid.NewGuid().ToString("N"));
        HardwareOperationStatus pulse = IpcJson.FromElement<HardwareOperationStatus>(pulseResponse.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning-pulse operation.");

        HardwareOperationStatus completed = await WaitForTerminalOperationAsync(pulse.Id, TimeSpan.FromSeconds(20));
        FanCommissioningObservationV1 observation = await SendAsync<FanCommissioningObservationV1>(
            IpcCommand.ObserveFanCommissioning,
            new FanCommissioningSessionRequest(session.Id));
        CommissioningPulseCliResult result = new(
            SchemaVersion: 1,
            CapabilityId: capabilityId,
            RpmSensorId: rpmSensorId,
            ProvisionalHeaderAlias: headerAlias,
            Session: observation.Session,
            Operation: completed,
            Observation: observation,
            PhysicalHeaderCertified: false,
            FanStopEnabled: false,
            Summary: "The bounded pulse completed. The firmware/default reset outcome is recorded in the operation result. Physical header certification and calibration remain blocked.");
        Write(result, json, value => Console.WriteLine(
            $"{value.Operation.State}: {value.ProvisionalHeaderAlias} remains provisional. {value.Operation.Message}"));
        return completed.State == HardwareOperationState.Completed ? 0 : 3;
    }

    private static Task<T> SendAsync<T>(IpcCommand command) => SendAsync<T>(ProtocolConstants.ServicePipeName, command, payload: null);

    private static Task<T> SendAsync<T>(IpcCommand command, object payload) =>
        SendAsync<T>(ProtocolConstants.ServicePipeName, command, payload);

    private static async Task<T> SendAsync<T>(string pipeName, IpcCommand command, object? payload)
    {
        IpcResponse response = payload is null
            ? await SendResponseAsync(pipeName, command, payload: null).ConfigureAwait(false)
            : await SendResponseAsync(pipeName, command, payload).ConfigureAwait(false);

        return IpcJson.FromElement<T>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
    }

    private static Task<IpcResponse> SendResponseAsync<T>(
        IpcCommand command,
        T payload,
        long? expectedRevision = null,
        string? idempotencyKey = null) => SendResponseAsync(
            ProtocolConstants.ServicePipeName,
            command,
            payload,
            expectedRevision,
            idempotencyKey);

    private static async Task<IpcResponse> SendResponseAsync(
        string pipeName,
        IpcCommand command,
        object? payload,
        long? expectedRevision = null,
        string? idempotencyKey = null)
    {
        IpcResponse response = await SendUncheckedResponseAsync(
            pipeName,
            command,
            payload,
            expectedRevision,
            idempotencyKey).ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException($"{response.ErrorCode}: {response.Error}");
        }

        return response;
    }

    private static async Task<IpcResponse> SendUncheckedResponseAsync(
        string pipeName,
        IpcCommand command,
        object? payload,
        long? expectedRevision = null,
        string? idempotencyKey = null)
    {
        NamedPipeRequestClient client = new(pipeName);
        IpcRequest request = payload is null
            ? NamedPipeRequestClient.CreateRequest(command, expectedRevision, idempotencyKey)
            : NamedPipeRequestClient.CreateRequest(command, payload, expectedRevision, idempotencyKey);
        return await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<HardwareOperationStatus> WaitForTerminalOperationAsync(string operationId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        HardwareOperationStatus? latest = null;
        do
        {
            latest = await SendAsync<HardwareOperationStatus>(
                IpcCommand.GetOperationById,
                new OperationLookupRequest(operationId)).ConfigureAwait(false);
            if (latest.State is HardwareOperationState.Completed
                    or HardwareOperationState.Aborted
                    or HardwareOperationState.Failed
                    or HardwareOperationState.RecoveryRequired)
            {
                return latest;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException(
            $"Commissioning operation '{operationId}' did not reach a terminal state within {timeout.TotalSeconds:0} seconds. "
            + $"Last observed state: {latest?.State.ToString() ?? "unavailable"}.");
    }

    private static string RequiredOption(string[] args, string name) => Option(args, name)
        ?? throw new ArgumentException($"{name} is required.");

    private static int OptionalIntOption(string[] args, string name, int defaultValue, int minimum, int maximum)
    {
        string? value = Option(args, name);
        if (value is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                $"{name} must be an integer from {minimum} through {maximum}.");
        }
        return parsed;
    }

    private static double RequiredDoubleOption(string[] args, string name, double minimum, double maximum)
    {
        string value = RequiredOption(args, name);
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                $"{name} must be a finite number from {minimum.ToString(CultureInfo.InvariantCulture)} through {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }
        return parsed;
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.FindIndex(args, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void Write<T>(T value, bool json, Action<T> humanWriter)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
        }
        else
        {
            humanWriter(value);
        }
    }

    private static void PrintSnapshot(HardwareSnapshot snapshot)
    {
        Console.WriteLine($"RigPilot read-only probe at {snapshot.CapturedAt:O}");
        Console.WriteLine($"Devices: {snapshot.Devices.Count}; sensors: {snapshot.Sensors.Count}; capabilities: {snapshot.Capabilities.Count}");
        foreach (HardwareDevice device in snapshot.Devices)
        {
            Console.WriteLine($"  [{device.Kind}] {device.Name}");
        }

        Console.WriteLine("Adapters:");
        foreach (AdapterHealth health in snapshot.AdapterHealth)
        {
            Console.WriteLine($"  {(health.Healthy ? "OK" : "DEGRADED")} {health.AdapterId}: {health.Message}");
        }

        foreach (DiagnosticWarning warning in snapshot.Warnings)
        {
            Console.WriteLine($"  {warning.Severity} {warning.Code}: {warning.Message}");
        }

        ConflictDescriptor[] running = snapshot.Conflicts.Where(conflict => conflict.IsRunning).ToArray();
        if (running.Length > 0)
        {
            Console.WriteLine("Running controllers:");
            foreach (ConflictDescriptor conflict in running)
            {
                Console.WriteLine($"  {conflict.DisplayName}: {string.Join(", ", conflict.ResourceFamilies)}");
            }
        }
    }

    private static void PrintQualification(QualificationMatrixStatusV1 status)
    {
        Console.WriteLine($"Qualification matrix: {(status.CanReleaseV1 ? "READY" : "BLOCKED")}");
        Console.WriteLine($"Physical systems recorded: {status.PhysicalSystemCount}/18");
        foreach (QualificationRequirementStatusV1 requirement in status.Requirements.Where(item => !item.Passed))
        {
            Console.WriteLine($"  MISSING {requirement.Requirement}: {requirement.Observed}/{requirement.Required}");
        }
        foreach (string defect in status.BlockingDefects)
        {
            Console.WriteLine($"  DEFECT {defect}");
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            RigPilot CLI 0.4 alpha

            pchelper-cli probe [--local] [--json]   Run a read-only hardware and conflict probe.
            pchelper-cli runtime-preflight [--json] Verify that the installed service and client share the write-capable runtime contract.
            pchelper-cli status [--json]            Read service status.
            pchelper-cli profiles [--json]          List stored profiles.
            pchelper-cli capabilities [--json]      List protocol-v2 bounds, hazards, ownership, and boot policy.
            pchelper-cli output-roles [--json]      List persisted physical cooling-output safety roles.
            pchelper-cli set-output-role --capability ID --rpm-sensor ID --header AIO_PUMP --role Pump
                --confirm-safety-role [--json]      Store a typed physical-output safety role; no fan command is sent.
            pchelper-cli commission-sessions [--json]
                                                 List persisted fan-commissioning sessions.
            pchelper-cli confirm-case-fan --session ID --header CASE_FAN_1 --confirm-case-fan [--json]
                                                 Confirm a user-declared generic case-fan session; no fan command is sent.
            pchelper-cli calibrate-case-fan --session ID --capability ID --rpm-sensor ID
                --temperature-sensor ID --temperature-limit 70 --allow-fan-stop
                --confirm-experimental --confirm-device [--settling-seconds 2] [--restart-cycles 2] [--timeout-seconds 300] [--json]
                                                 Run a bounded 0-100% case-fan calibration and two stop/restart cycles.
            pchelper-cli operation [--id OPERATION_ID] [--json]
                                                 Read the current/latest operation, or retrieve one exact durable operation by ID.
            pchelper-cli cooling-reports [--json]   Read cooling qualification evidence.
            pchelper-cli discover-controllers [--json]
                                                 Run a contained USB/AIO controller-discovery probe (read-only inventory).
            pchelper-cli discover-hid [--json]   Run a contained, read-only HID peripheral inventory (keyboard/mouse/RGB/AIO classes).
            pchelper-cli ryzen-smu-feasibility [--json]
                                                 Read PPT/TDC/THM/EDC limit and actual pairs from the Ryzen SMU PM table via signed PawnIO (read-only PBO qualification evidence; no CPU write).
            pchelper-cli close-blockers --confirm [--id CONTROLLER_ID] [--json]
                                                 Terminate the running processes of detected conflicting controllers (Afterburner, CAM, Fan Control, Armoury Crate, ...) so they release device ownership. Curated allowlist only; takes over no hardware.
            pchelper-cli kraken-rgb (--colour RRGGBB | --off) --confirm-experimental --confirm-device nzxt:kraken-x3 [--json]
                                                 Write a fixed colour (or off) to the Kraken X3 ring+logo via RigPilot's native adapter. Lighting only; no read-back, confirm visually.
            pchelper-cli razer-rgb (--colour RRGGBB | --off) --confirm-experimental --confirm-device razer:lianli-o11-dynamic [--json]
                                                 Write a fixed colour (or off) to the Lian Li O11 Dynamic Razer Edition case via RigPilot's native extended-matrix custom-frame path. Lighting only; confirm visually.
            pchelper-cli kraken-pump --duty 60..100 --confirm-experimental --confirm-device nzxt:kraken-x3 [--json]
                                                 Set a fixed Kraken X3 pump duty (hard floor 60%, never stopped) with firmware status read-back.
            pchelper-cli gpu-fan-arm --confirm-experimental --confirm-device DEVICE_ID [--json]
                                                 Arm Experimental GPU fan control after exact-device acknowledgement.
            pchelper-cli gpu-fan-state [--json] Read live GPU fan policy and duty through the service. Read-only.
            pchelper-cli gpu-fan-disarm [--json] Disarm GPU fan control and restore the automatic curve.
            pchelper-cli gpu-power-arm --confirm-experimental --confirm-device DEVICE_ID [--json]
                                                 Arm Experimental GPU power-limit control after exact-device acknowledgement.
            pchelper-cli gpu-power-disarm [--json] Disarm GPU power-limit control and restore the vendor default limit.
            pchelper-cli gpu-clock-arm --confirm-experimental --confirm-device DEVICE_ID [--json]
                                                 Arm Experimental GPU core/memory clock-offset control after exact-device acknowledgement.
            pchelper-cli gpu-clock-disarm [--json] Disarm GPU clock-offset control and return both domains to stock clocks.
            pchelper-cli cpu-tuning-arm --confirm-experimental --confirm-device DEVICE_ID [--json]
                                                 Request arming of CPU PBO tuning. Refused by the qualification gate on every system today.
            pchelper-cli cpu-tuning-disarm [--json] Confirm CPU PBO tuning is disarmed and report the boot-recovery sentinel state.
            pchelper-cli trace [--json]             List bounded, redacted adapter operation diagnostics.
            pchelper-cli profiles-v2 [--json]       List layered hardware/cooling/lighting/OSD profiles.
            pchelper-cli games [--json]             List local games from the signed-in user agent.
            pchelper-cli import-afterburner --file FILE [--section Profile1] [--json]
            pchelper-cli import-fancontrol --file FILE [--json]
            pchelper-cli pack-inspect --file FILE [--json]
            pchelper-cli pack-list [--json]         List installed adapter packs.
            pchelper-cli pack-install --file FILE [--confirm-development-trust] [--json]
                                                 Verify and install a signed .pcha adapter pack.
            pchelper-cli pack-remove --id PACK_ID --version VERSION [--json]
                                                 Remove an installed adapter pack and its inspection record.
            pchelper-cli report [--output FILE]     Generate a redacted, unapproved report preview.
            pchelper-cli direct-prepare --capability ID --confirm-no-write [--output FILE] [--json]
                                                 Run only the direct adapter Prepare path. It cannot issue a hardware write.
            pchelper-cli commission-preflight --capability ID --rpm-sensor ID --header CHA_FAN1
                --confirm-experimental --confirm-device --provisional-case-alias [--json]
                                                 Run only an Adapter Host Prepare diagnostic. It cannot issue a fan write.
            pchelper-cli commission-pulse --capability ID --rpm-sensor ID --header CHA_FAN1 --duration-seconds 2
                --confirm-experimental --confirm-device --provisional-case-alias [--json]
                                                 Run only a bounded 2-5 second case-fan identity pulse. The alias remains provisional.
            pchelper-cli qualification --ledger FILE [--json]
                                                 Evaluate the 18-system 1.0 evidence gate.
            pchelper-cli qualification-draft --output FILE --confirm-witnessed --attest-no-bsod --attest-no-stuck-fan
                --attest-no-unauthorised-write --attest-rollback-passed [--notes TEXT] [--json]
                                                 Append an UNSIGNED DRAFT qualification record for this exact system. Drafts never satisfy the 1.0 gate.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Use 'help'.");
        return 64;
    }

    private sealed record CommissioningPulseCliResult(
        int SchemaVersion,
        string CapabilityId,
        string RpmSensorId,
        string ProvisionalHeaderAlias,
        FanCommissioningSessionV1 Session,
        HardwareOperationStatus Operation,
        FanCommissioningObservationV1 Observation,
        bool PhysicalHeaderCertified,
        bool FanStopEnabled,
        string Summary);

    private sealed record CalibrationCliResult(
        int SchemaVersion,
        string SessionId,
        string CapabilityId,
        string RpmSensorId,
        string TemperatureSensorId,
        double TemperatureLimitCelsius,
        HardwareOperationStatus Operation,
        FanCommissioningSessionV1? CommissioningSession,
        string Summary);

    private sealed record DirectPrepareCliResult(
        int SchemaVersion,
        string CapabilityId,
        string ProcessIdentity,
        bool Prepared,
        bool ApplyIssued,
        bool VerifyIssued,
        bool RollbackIssued,
        bool ResetIssued,
        double? RequestedDutyPercent,
        double? PreviousDutyPercent,
        string? FailureStage,
        string? ExceptionType,
        int? HResult,
        int? Win32Error,
        string Summary);

    private sealed record CommissioningPreflightCliResult(
        int SchemaVersion,
        string CapabilityId,
        string RpmSensorId,
        string ProvisionalHeaderAlias,
        bool ServiceSucceeded,
        string? ServiceErrorCode,
        string? ServiceError,
        FanCommissioningPreflightResultV1 Preflight,
        bool PhysicalHeaderCertified,
        bool FanStopEnabled,
        string Summary);
}
