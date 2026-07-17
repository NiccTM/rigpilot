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
    private async Task RefreshFanCommissioningAsync(CancellationToken cancellationToken)
    {
        try
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetFanCommissioningSessions),
                cancellationToken);
            if (!response.Success)
            {
                _fanCalibrationsByCapability.Clear();
                return;
            }
            string? selectedId = SelectedFanCommissioningSession?.Id;
            FanCommissioningSessionV1[] sessions = (IpcJson.FromElement<IReadOnlyList<FanCommissioningSessionV1>>(response.Payload) ?? [])
                .OrderByDescending(session => session.UpdatedAt)
                .ToArray();
            IpcResponse calibrationResponse = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetFanCalibrations),
                cancellationToken);
            _fanCalibrationsByCapability.Clear();
            if (calibrationResponse.Success)
            {
                IReadOnlyList<FanCalibrationV2> calibrations = IpcJson.FromElement<IReadOnlyList<FanCalibrationV2>>(calibrationResponse.Payload) ?? [];
                foreach (FanCalibrationV2 calibration in calibrations.Where(calibration => calibration.SchemaVersion is > 0 and <= FanCalibrationV2.CurrentSchemaVersion))
                {
                    _fanCalibrationsByCapability[calibration.CapabilityId] = calibration;
                }
            }
            Replace(FanCommissioningSessions, sessions);
            FanCommissioningSessionV1? next = sessions.FirstOrDefault(session => session.Id == selectedId);
            if (next is not null)
            {
                _selectedFanCommissioningSession = next;
                OnPropertyChanged(nameof(SelectedFanCommissioningSession));
            }
            else
            {
                SelectCommissioningForTarget();
            }
            NotifyCommissioningProperties();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // The service can be upgraded independently. Calibration remains
            // unavailable until the commissioning protocol is present.
            CommissioningObservation = "The connected service does not expose the 0.4 commissioning protocol.";
            Replace(FanCommissioningSessions, []);
            _fanCalibrationsByCapability.Clear();
            NotifyCommissioningProperties();
        }
    }

    private async Task RefreshCoolingOutputAssignmentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            IpcResponse response = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.GetCoolingOutputAssignments),
                cancellationToken);
            if (!response.Success)
            {
                return;
            }

            CoolingOutputAssignmentV1[] assignments = (IpcJson.FromElement<IReadOnlyList<CoolingOutputAssignmentV1>>(response.Payload) ?? [])
                .Where(assignment => assignment.SchemaVersion == CoolingOutputAssignmentV1.CurrentSchemaVersion)
                .OrderBy(assignment => assignment.HeaderName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Replace(CoolingOutputAssignments, assignments);
            ApplyCoolingOutputAssignmentForTarget();
            RebuildExperimentalControlCenter();
            UpdateSafetySummary();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // This registry is an additive 0.4 safety feature. A compatible
            // older service stays usable, but it cannot persist a physical
            // role until its matching runtime is installed.
            Replace(CoolingOutputAssignments, []);
            ApplyCoolingOutputAssignmentForTarget();
            RebuildExperimentalControlCenter();
            UpdateSafetySummary();
        }
    }

    private async Task SaveCoolingOutputAssignmentCoreAsync()
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedCalibrationTarget
            ?? throw new InvalidOperationException("Select a cooling output before assigning its physical role.");
        CoolingOutputAssignmentV1 assignment = new(
            CoolingOutputAssignmentV1.CurrentSchemaVersion,
            target.Descriptor.Id,
            target.Descriptor.Id,
            target.Descriptor.AdapterId,
            target.Descriptor.DeviceId,
            target.RpmSensorId,
            CoolingOutputHeaderName.Trim(),
            SelectedCoolingOutputRole,
            DateTimeOffset.UtcNow,
            "User-confirmed physical cooling-output role.");
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.SaveCoolingOutputAssignment,
            new CoolingOutputAssignmentUpdateRequest(assignment, RemoveCoolingSafetyProtectionAcknowledged),
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        CoolingOutputAssignmentSaveResultV1 result = IpcJson.FromElement<CoolingOutputAssignmentSaveResultV1>(response.Payload)
            ?? throw new InvalidDataException("The service returned an empty cooling-output assignment result.");
        UpdateStateRevision(response);
        CoolingOutputAssignmentV1? existing = CoolingOutputAssignments.FirstOrDefault(item =>
            string.Equals(item.Id, result.Assignment.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            CoolingOutputAssignments.Remove(existing);
        }
        if (!result.Removed)
        {
            CoolingOutputAssignments.Add(result.Assignment);
        }
        RemoveCoolingSafetyProtectionAcknowledged = false;
        ApplyCoolingOutputAssignmentForTarget();
        RebuildExperimentalControlCenter();
        UpdateSafetySummary();
        CommissioningObservation = result.Removed
            ? "Physical safety role cleared. Generic labels still do not authorise a fan pulse. No fan command was sent."
            : $"Stored {SplitWords(result.Assignment.Role.ToString())} role for {result.Assignment.HeaderName}. No fan command was sent.";
        ShowNotice(
            result.Removed ? "Cooling-output safety role cleared." : "Cooling-output safety role saved.",
            result.Removed ? "Warning" : "Success");
    }


    private async Task BeginFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedCalibrationTarget
            ?? throw new InvalidOperationException("Select a cooling control before starting commissioning.");
        if (target.RpmSensorId is null)
        {
            throw new InvalidOperationException("This control has no RPM sensor from the same exact device.");
        }

        BeginFanCommissioningRequest payload = new(
            target.Descriptor.Id,
            target.RpmSensorId,
            CommissioningHeaderName.Trim(),
            IsSelectedCoolingOutputProtected
                || target.Descriptor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || target.Descriptor.Name.Contains("pump", StringComparison.OrdinalIgnoreCase),
            AllowCaseFanStop,
            string.IsNullOrWhiteSpace(CommissioningNotes) ? null : CommissioningNotes.Trim());
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.BeginFanCommissioning,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 session = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(session, select: true);
        CommissioningHeaderConfirmed = false;
        CommissioningObservation = "Identity check is pending. Use a short, bounded pulse only while watching the physical fan, then explicitly confirm the header.";
        ShowNotice("Commissioning session started. No fan speed changed during setup.", "Info");
    }

    private async Task PulseFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Start a commissioning session before issuing a header-identification pulse.");
        PulseFanCommissioningRequest payload = new(
            session.Id,
            ConfirmExperimental: AdvancedWritesAcknowledged,
            ConfirmDevice: CalibrationDeviceAcknowledged,
            Duration: TimeSpan.FromSeconds(2));
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.PulseFanCommissioning,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        HardwareOperationStatus status = IpcJson.FromElement<HardwareOperationStatus>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty header-pulse operation.");
        UpdateStateRevision(response);
        _operation = status;
        NotifyOperationProperties();
        CommissioningObservation = "A 2-second bounded identification pulse is running. Watch the physical fan, then use Observe and explicitly confirm its header. Firmware/default control will be restored automatically.";
        ShowNotice("Header-identification pulse started. Calibration remains locked until you confirm the physical fan.", "Info");
    }

    private async Task RunInteractiveFanPreflightCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select the failed commissioning session before running the elevated diagnostic.");
        if (!CanRunInteractiveFanPreflight)
        {
            throw new InvalidOperationException("This session is not eligible for the explicit elevated no-write diagnostic.");
        }

        IpcResponse response = await _userAgentClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.RunInteractiveFanPreflight,
                new InteractiveFanPreflightRequestV1(
                    InteractiveFanPreflightRequestV1.CurrentSchemaVersion,
                    session.CapabilityId),
                idempotencyKey: Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        InteractiveFanPreflightResultV1 result = IpcJson.FromElement<InteractiveFanPreflightResultV1>(response.Payload)
            ?? throw new InvalidDataException("The elevated diagnostic returned an empty result.");
        CommissioningObservation = result.Prepared
            ? "Elevated user-session Prepare passed with no hardware command. The LocalSystem service remains blocked; this result cannot enable a pulse or calibration."
            : result.Summary;
        ShowNotice(
            result.Prepared
                ? "Elevated no-write diagnostic passed. Service fan control remains locked."
                : "Elevated no-write diagnostic did not pass; no hardware command was issued.",
            result.Prepared ? "Info" : "Warning");
    }

    private async Task ObserveFanCommissioningCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(IpcCommand.ObserveFanCommissioning, new FanCommissioningSessionRequest(session.Id)),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningObservationV1 observation = IpcJson.FromElement<FanCommissioningObservationV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty commissioning observation.");
        UpsertCommissioningSession(observation.Session, select: true);
        string rpm = observation.RpmSample?.Value is double value
            ? $" Paired RPM: {value:0} RPM."
            : " Paired RPM is unavailable.";
        string thermal = observation.ThermalSamples.Count == 0
            ? string.Empty
            : $" Observing {observation.ThermalSamples.Count} local thermal sensor(s).";
        CommissioningObservation = observation.Guidance + rpm + thermal;
    }

    private async Task ConfirmFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Start a commissioning session first.");
        ConfirmFanCommissioningRequest payload = new(
            session.Id,
            CommissioningHeaderConfirmed,
            CommissioningHeaderName.Trim(),
            string.IsNullOrWhiteSpace(CommissioningNotes) ? null : CommissioningNotes.Trim(),
            PhysicalHeaderObserved: CommissioningHeaderConfirmed);
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.ConfirmFanCommissioning,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 updated = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty confirmed commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(updated, select: true);
        CommissioningObservation = "Header identity is confirmed. The bounded calibration control is now enabled; CPU fans and pumps still cannot stop.";
        ShowNotice("Header identity confirmed. Review the experimental-write acknowledgement before calibration.", "Success");
    }

    private async Task CompleteFanCommissioningCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.CompleteFanCommissioning,
                new FanCommissioningSessionRequest(session.Id),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 completed = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty completed commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(completed, select: true);
        bool zeroRpmVerified = _operation?.CalibrationResult is { } calibration
            && FanCalibrationPolicy.SupportsVerifiedFanStop(calibration);
        CommissioningObservation = zeroRpmVerified
            ? "Qualification report saved. Zero-RPM remains limited to this exact verified stop/restart path; the control stays Experimental until broader independent hardware evidence exists."
            : "Qualification report saved. This exact output is approved only for a calibrated nonzero curve; zero-RPM remains disabled.";
        ShowNotice(zeroRpmVerified ? "Fan commissioning report saved." : "Nonzero-only fan commissioning report saved.", "Success");
    }

    private async Task CreateAdaptiveCoolingProfileCoreAsync()
    {
        EnsureServiceWritesAvailable();
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Save a completed commissioning report before creating a cooling profile.");
        await SaveCoolingProfileDraftAsync(
            CreateAdaptiveCoolingDraft(session),
            "Saving adaptive cooling curve");
    }

    private async Task SaveCustomCoolingCurveCoreAsync()
    {
        EnsureServiceWritesAvailable();
        if (!TryReadCustomCoolingCurve(out CustomCoolingCurveDefinition? definition, out string error))
        {
            throw new InvalidOperationException(error);
        }

        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Save a completed commissioning report before creating a cooling profile.");
        await SaveCoolingProfileDraftAsync(
            CreateCustomCoolingDraft(session, definition!),
            "Saving manual cooling curve");
    }

    private AdaptiveCoolingProfileDraft CreateAdaptiveCoolingDraft(FanCommissioningSessionV1 session)
    {
        ValidateCompletedCommissioningSession(session);
        CapabilityDescriptor output = GetCommissionedCoolingOutput(session);
        if (_operation is { CapabilityId: var operationCapabilityId, CalibrationResult: { } operationCalibration }
            && string.Equals(operationCapabilityId, session.CapabilityId, StringComparison.Ordinal)
            && FanCalibrationPolicy.SupportsNonZeroCurve(operationCalibration))
        {
            return AdaptiveCoolingProfileFactory.Create(output, operationCalibration, session.HeaderName, _snapshot?.Sensors ?? []);
        }
        if (GetCommissionedCalibration(session) is { } persistedCalibration
            && FanCalibrationPolicy.SupportsNonZeroCurve(persistedCalibration))
        {
            return AdaptiveCoolingProfileFactory.Create(output, persistedCalibration, session.HeaderName, _snapshot?.Sensors ?? []);
        }

        throw new InvalidOperationException("No stable calibration linked to this commissioning report is available.");
    }

    private AdaptiveCoolingProfileDraft CreateCustomCoolingDraft(
        FanCommissioningSessionV1 session,
        CustomCoolingCurveDefinition definition)
    {
        ValidateCompletedCommissioningSession(session);
        CapabilityDescriptor output = GetCommissionedCoolingOutput(session);
        if (_operation is { CapabilityId: var operationCapabilityId, CalibrationResult: { } operationCalibration }
            && string.Equals(operationCapabilityId, session.CapabilityId, StringComparison.Ordinal)
            && FanCalibrationPolicy.SupportsNonZeroCurve(operationCalibration))
        {
            return CustomCoolingCurveFactory.Create(output, operationCalibration, session.HeaderName, _snapshot?.Sensors ?? [], definition);
        }
        if (GetCommissionedCalibration(session) is { } persistedCalibration
            && FanCalibrationPolicy.SupportsNonZeroCurve(persistedCalibration))
        {
            return CustomCoolingCurveFactory.Create(output, persistedCalibration, session.HeaderName, _snapshot?.Sensors ?? [], definition);
        }

        throw new InvalidOperationException("No stable calibration linked to this commissioning report is available.");
    }

    private string? GetCustomCoolingCurveDraftError(CustomCoolingCurveDefinition definition)
    {
        if (SelectedFanCommissioningSession is not FanCommissioningSessionV1 session)
        {
            return "Save a physically observed commissioning report before creating a manual curve.";
        }

        try
        {
            _ = CreateCustomCoolingDraft(session, definition);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    private static void ValidateCompletedCommissioningSession(FanCommissioningSessionV1 session)
    {
        if (session.State != FanCommissioningState.Completed || !session.PhysicalHeaderObserved)
        {
            throw new InvalidOperationException("A physically observed completed commissioning report is required before creating a cooling profile.");
        }
    }

    private CapabilityDescriptor GetCommissionedCoolingOutput(FanCommissioningSessionV1 session) =>
        _snapshot?.Capabilities.FirstOrDefault(capability => capability.Id == session.CapabilityId)
            ?? throw new InvalidOperationException("The commissioned cooling output is no longer present in the current inventory.");

    private async Task SaveCoolingProfileDraftAsync(AdaptiveCoolingProfileDraft draft, string busyMessage)
    {
        BusyMessage = busyMessage;
        IsBusy = true;
        try
        {
            IpcResponse graphResponse = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SaveCoolingGraph,
                    draft.Graph,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(graphResponse);
            UpdateStateRevision(graphResponse);
            _coolingGraphsById[draft.Graph.Id] = draft.Graph;

            IpcResponse profileResponse = await _client.SendAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.SaveProfileV2,
                    draft.Profile,
                    _status?.StateRevision,
                    Guid.NewGuid().ToString("N")),
                _lifetime.Token);
            EnsureSuccess(profileResponse);
            UpdateStateRevision(profileResponse);
            await RefreshAsync(full: true, userInitiated: false);
            ShowNotice(
                $"Saved '{draft.Profile.Name}'. It is not active; review the exact-device acknowledgement, then apply it from Profiles.",
                "Success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryReadCustomCoolingCurve(
        out CustomCoolingCurveDefinition? definition,
        out string error)
    {
        definition = null;
        string name = CustomCoolingCurveName.Trim();
        if (name.Length is 0 or > 48)
        {
            error = "Enter a curve name from 1 to 48 characters.";
            return false;
        }

        List<CurvePoint> points = [];
        string[] lines = CustomCoolingCurvePoints.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 2 or > 8)
        {
            error = "Provide two to eight points, one per line, as temperature:duty (for example 70:70).";
            return false;
        }
        foreach (string line in lines)
        {
            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double duty))
            {
                error = $"'{line}' is not a valid temperature:duty point.";
                return false;
            }
            points.Add(new CurvePoint(temperature, duty));
        }

        if (!TryReadCurveValue(CustomCoolingCurveHysteresisUpText, "rise hysteresis", out double hysteresisUp, out error)
            || !TryReadCurveValue(CustomCoolingCurveHysteresisDownText, "fall hysteresis", out double hysteresisDown, out error)
            || !TryReadCurveValue(CustomCoolingCurveResponseUpSecondsText, "rise response time", out double responseUp, out error)
            || !TryReadCurveValue(CustomCoolingCurveResponseDownSecondsText, "fall response time", out double responseDown, out error))
        {
            return false;
        }

        definition = new CustomCoolingCurveDefinition(
            name,
            points,
            hysteresisUp,
            hysteresisDown,
            responseUp,
            responseDown);
        error = string.Empty;
        return true;
    }

    private static bool TryReadCurveValue(
        string text,
        string label,
        out double value,
        out string error)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || !double.IsFinite(value))
        {
            error = $"Enter a finite {label} value.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private (double MinimumDuty, double MaximumDuty) GetCustomCoolingCurveDutyRange()
    {
        string? capabilityId = SelectedFanCommissioningSession?.CapabilityId
            ?? SelectedCalibrationTarget?.Descriptor.Id;
        NumericRange? range = capabilityId is null
            ? null
            : _snapshot?.Capabilities.FirstOrDefault(capability => capability.Id == capabilityId)?.Range;
        return range is { } numeric && numeric.Maximum > numeric.Minimum
            ? (numeric.Minimum, numeric.Maximum)
            : (0, 100);
    }

    private bool HasAdaptiveCoolingCalibration(FanCommissioningSessionV1 session) =>
        (_operation is { CapabilityId: var operationCapabilityId, CalibrationResult: { } operationCalibration }
            && string.Equals(operationCapabilityId, session.CapabilityId, StringComparison.Ordinal)
            && FanCalibrationPolicy.SupportsNonZeroCurve(operationCalibration))
        || (GetCommissionedCalibration(session) is { } persistedCalibration
            && FanCalibrationPolicy.SupportsNonZeroCurve(persistedCalibration));

    private FanCalibrationV2? GetCommissionedCalibration(FanCommissioningSessionV1 session) =>
        _fanCalibrationsByCapability.TryGetValue(session.CapabilityId, out FanCalibrationV2? calibration)
        && string.Equals(calibration.CommissioningSessionId, session.Id, StringComparison.Ordinal)
            ? calibration
            : null;

    private async Task CancelFanCommissioningCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.CancelFanCommissioning,
                new FanCommissioningSessionRequest(session.Id),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 cancelled = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty cancelled commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(cancelled, select: true);
        CommissioningObservation = "Commissioning was cancelled. No active calibration was interrupted; firmware/default control remains responsible for the fan.";
        ShowNotice("Commissioning session cancelled.", "Info");
    }

    private async Task RecoverFanCommissioningCoreAsync()
    {
        FanCommissioningSessionV1 session = SelectedFanCommissioningSession
            ?? throw new InvalidOperationException("Select a commissioning session requiring recovery first.");
        IpcResponse response = await _client.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.RecoverFanCommissioning,
                new FanCommissioningSessionRequest(session.Id),
                _status?.StateRevision,
                Guid.NewGuid().ToString("N")),
            _lifetime.Token);
        EnsureSuccess(response);
        FanCommissioningSessionV1 recovered = IpcJson.FromElement<FanCommissioningSessionV1>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty recovered commissioning session.");
        UpdateStateRevision(response);
        UpsertCommissioningSession(recovered, select: true);
        CommissioningObservation = "Firmware/default recovery completed. Start a new commissioning session before another calibration.";
        ShowNotice("Recovered the controller to its firmware/default policy.", "Success");
    }


    private async Task StartCalibrationCoreAsync()
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedCalibrationTarget
            ?? throw new InvalidOperationException("Select a cooling control first.");
        if (target.RpmSensorId is null)
        {
            throw new InvalidOperationException("The selected control has no matching RPM sensor.");
        }

        HardwareOperationEligibility eligibility = GetCalibrationEligibility();
        if (!eligibility.Eligible)
        {
            throw new InvalidOperationException(eligibility.Reason);
        }

        BusyMessage = "Starting fan calibration";
        IsBusy = true;
        try
        {
            StartCalibrationRequest payload = new(
                target.Descriptor.Id,
                target.RpmSensorId,
                ConfirmExperimental: AdvancedWritesAcknowledged,
                ConfirmDevice: CalibrationDeviceAcknowledged,
                AllowFanStop: AllowCaseFanStop && !IsSelectedCoolingOutputProtected,
                SettlingTime: TimeSpan.FromSeconds(CalibrationSettlingSeconds),
                StableSampleCount: 3,
                MaximumSampleCount: 15,
                SampleInterval: TimeSpan.FromMilliseconds(500),
                StabilityTolerancePercent: 10,
                RestartVerificationCycles: CalibrationRestartCycleCount,
                TemperatureLimits: BuildCalibrationTemperatureLimits(target.Descriptor),
                CommissioningSessionId: SelectedFanCommissioningSession?.Id);
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.StartCalibration,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            _operation = IpcJson.FromElement<HardwareOperationStatus>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty operation response.");
            NotifyOperationProperties();
            ShowNotice("Calibration started. RigPilot will restore the prior policy when it finishes or is cancelled.", "Warning");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartTuneCoreAsync(int refinementCandidates = 0, double safetyMargin = 0)
    {
        EnsureServiceWritesAvailable();
        OperationTargetDisplay target = SelectedTuneTarget
            ?? throw new InvalidOperationException("Select a bounded tuning control first.");
        HardwareOperationEligibility eligibility = GetTuneEligibility();
        if (!eligibility.Eligible)
        {
            throw new InvalidOperationException(eligibility.Reason);
        }

        if (!TryReadTuneLimits(out double temperatureCeiling, out double? powerCeiling))
        {
            throw new InvalidOperationException("Enter a temperature ceiling from 40 to 100 °C and an optional positive power ceiling.");
        }

        TunePlan plan = CreateTunePlan(target, temperatureCeiling, powerCeiling);
        TuneDirection direction = target.Descriptor.Domain is ControlDomain.Cooling or ControlDomain.CoolingSafety
            ? TuneDirection.Minimize
            : SelectedTuneObjective == TuningObjective.Performance
            ? TuneDirection.Maximize
            : TuneDirection.Minimize;
        BusyMessage = "Starting bounded auto-tuning";
        IsBusy = true;
        try
        {
            StartTuneRequest payload = new(
                plan,
                target.Descriptor.Id,
                direction,
                AdvancedWritesAcknowledged,
                TuneDeviceAcknowledged,
                CandidateScreeningTime: TimeSpan.FromSeconds(30),
                MaximumCandidates: 12,
                RefinementCandidates: refinementCandidates,
                SafetyMargin: safetyMargin);
            IpcRequest request = NamedPipeRequestClient.CreateRequest(
                IpcCommand.StartTune,
                payload,
                _status?.StateRevision,
                Guid.NewGuid().ToString("N"));
            IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
            EnsureSuccess(response);
            _operation = IpcJson.FromElement<HardwareOperationStatus>(response.Payload)
                ?? throw new InvalidDataException("Service returned an empty operation response.");
            NotifyOperationProperties();
            ShowNotice("Auto-tuning started. Candidates are bounded and the prior state will be restored after screening.", "Warning");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AbortOperationCoreAsync()
    {
        IpcRequest request = NamedPipeRequestClient.CreateRequest(
            IpcCommand.AbortOperation,
            (string?)null,
            _status?.StateRevision,
            Guid.NewGuid().ToString("N"));
        IpcResponse response = await _client.SendAsync(request, _lifetime.Token);
        EnsureSuccess(response);
        _operation = IpcJson.FromElement<HardwareOperationStatus>(response.Payload) ?? _operation;
        NotifyOperationProperties();
        ShowNotice("Cancellation requested. Hardware restoration is still in progress.", "Warning");
    }

    private CoolingOutputAssignmentV1? GetSelectedCoolingOutputAssignment() =>
        SelectedCalibrationTarget is OperationTargetDisplay target
            ? GetCoolingOutputAssignment(target.Descriptor)
            : null;

    private CoolingOutputAssignmentV1? GetCoolingOutputAssignment(CapabilityDescriptor capability) =>
        CoolingOutputAssignments.FirstOrDefault(assignment =>
            string.Equals(assignment.CapabilityId, capability.Id, StringComparison.Ordinal));

    private void ApplyCoolingOutputAssignmentForTarget()
    {
        CoolingOutputAssignmentV1? assignment = GetSelectedCoolingOutputAssignment();
        OperationTargetDisplay? target = SelectedCalibrationTarget;
        _selectedCoolingOutputRole = assignment?.Role ?? CoolingOutputRole.Unknown;
        _coolingOutputHeaderName = assignment?.HeaderName ?? target?.Descriptor.Name ?? string.Empty;
        _removeCoolingSafetyProtectionAcknowledged = false;
        if (assignment is { IsSafetyCritical: true })
        {
            _allowCaseFanStop = false;
            OnPropertyChanged(nameof(AllowCaseFanStop));
        }

        OnPropertyChanged(nameof(SelectedCoolingOutputRole));
        OnPropertyChanged(nameof(CoolingOutputHeaderName));
        OnPropertyChanged(nameof(RemoveCoolingSafetyProtectionAcknowledged));
        NotifyCoolingOutputAssignmentProperties();
    }

    private void NotifyCoolingOutputAssignmentProperties()
    {
        OnPropertyChanged(nameof(IsSelectedCoolingOutputProtected));
        OnPropertyChanged(nameof(CanAllowCaseFanStop));
        OnPropertyChanged(nameof(CanSaveCoolingOutputAssignment));
        OnPropertyChanged(nameof(CoolingOutputRoleStatus));
        OnPropertyChanged(nameof(CommissioningTargetSummary));
        OnPropertyChanged(nameof(CommissioningPreflight));
        _saveCoolingOutputAssignmentCommand.RaiseCanExecuteChanged();
        NotifyOperationEligibility();
    }

    private void SelectCommissioningForTarget()
    {
        string? capabilityId = SelectedCalibrationTarget?.Descriptor.Id;
        FanCommissioningSessionV1? next = capabilityId is null
            ? null
            : FanCommissioningSessions
                .Where(session => string.Equals(session.CapabilityId, capabilityId, StringComparison.Ordinal))
                .OrderByDescending(session => session.UpdatedAt)
                .FirstOrDefault();
        _selectedFanCommissioningSession = next;
        if (next is null && SelectedCalibrationTarget is OperationTargetDisplay target)
        {
            _commissioningHeaderName = target.Descriptor.Name;
            _commissioningNotes = string.Empty;
            _commissioningHeaderConfirmed = false;
            OnPropertyChanged(nameof(CommissioningHeaderName));
            OnPropertyChanged(nameof(CommissioningNotes));
            OnPropertyChanged(nameof(CommissioningHeaderConfirmed));
        }
        else if (next is not null)
        {
            _commissioningHeaderName = next.HeaderName;
            _commissioningNotes = next.Notes ?? string.Empty;
            _commissioningHeaderConfirmed = next.PhysicalHeaderObserved;
            OnPropertyChanged(nameof(CommissioningHeaderName));
            OnPropertyChanged(nameof(CommissioningNotes));
            OnPropertyChanged(nameof(CommissioningHeaderConfirmed));
        }
        OnPropertyChanged(nameof(SelectedFanCommissioningSession));
        NotifyCommissioningProperties();
    }

    private void UpsertCommissioningSession(FanCommissioningSessionV1 session, bool select)
    {
        FanCommissioningSessionV1? existing = FanCommissioningSessions.FirstOrDefault(item => item.Id == session.Id);
        if (existing is not null)
        {
            FanCommissioningSessions.Remove(existing);
        }
        FanCommissioningSessions.Add(session);
        if (select)
        {
            SelectedFanCommissioningSession = session;
        }
        NotifyCommissioningProperties();
    }
}
