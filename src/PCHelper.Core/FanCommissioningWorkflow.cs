using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Pure commissioning transitions shared by the service and contract tests.
/// Hardware writes remain in the operation engine; this workflow only guards
/// persisted identity and recovery state.
/// </summary>
public static class FanCommissioningWorkflow
{
    /// <summary>
    /// Returns true only for a physical case/chassis header designation, not a
    /// generic telemetry label such as "Fan #1".  An unknown controller may
    /// receive an identification pulse only after the operator has checked the
    /// motherboard wiring and declares this exact, non-critical header.
    /// </summary>
    public static bool IsDeclaredChassisHeader(string? headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return false;
        }

        string normalised = headerName.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        return normalised.StartsWith("CHA_FAN", StringComparison.Ordinal)
            || normalised.StartsWith("CHASSIS_FAN", StringComparison.Ordinal)
            || normalised.StartsWith("CASE_FAN", StringComparison.Ordinal)
            || normalised.StartsWith("SYS_FAN", StringComparison.Ordinal);
    }

    /// <summary>
    /// Identification pulses are deliberately limited to a declared chassis
    /// header. CPU and pump sessions can retain their non-zero calibration
    /// safeguards, but cannot use an ambiguity-resolving pulse.
    /// </summary>
    public static bool CanIssueIdentificationPulse(FanCommissioningSessionV1 session, out string? reason)
    {
        if (session.State != FanCommissioningState.AwaitingIdentification)
        {
            reason = "Identification pulses are available only while the commissioning session is awaiting physical header identification.";
            return false;
        }

        if (session.IsCpuOrPump)
        {
            reason = "Identification pulses are blocked for CPU fans and pumps. Select a confirmed chassis header instead.";
            return false;
        }

        if (!IsDeclaredChassisHeader(session.HeaderName))
        {
            reason = "Identification pulses require an explicit physical chassis-header label such as CHA_FAN1; generic labels such as Fan #1 are not sufficient.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// A full calibration is longer and changes a wider duty range than an
    /// identification pulse. It therefore requires the persisted observation
    /// flag, not merely a user-declared header alias.
    /// </summary>
    public static bool CanRunCalibration(FanCommissioningSessionV1 session, out string? reason)
    {
        if (session.State != FanCommissioningState.ReadyForCalibration)
        {
            reason = "Calibration is available only after the commissioning session is ready.";
            return false;
        }

        if (!session.HeaderConfirmed || !session.PhysicalHeaderObserved)
        {
            reason = "Visually observe and confirm the exact physical header before calibration; a declared alias is not sufficient.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Closes a commissioning session when its no-write controller preflight
    /// fails. A caller must create a new session after correcting the adapter
    /// or execution-context fault; it may not retry into a physical pulse from
    /// the failed evidence record.
    /// </summary>
    public static FanCommissioningSessionV1 FailNoWritePreflight(
        FanCommissioningSessionV1 session,
        string error,
        DateTimeOffset now)
    {
        if (session.State != FanCommissioningState.AwaitingIdentification)
        {
            throw new InvalidOperationException("Only an awaiting-identification session can fail a no-write preflight.");
        }
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A no-write preflight failure reason is required.", nameof(error));
        }

        return session with
        {
            State = FanCommissioningState.Failed,
            UpdatedAt = now,
            Error = error.Trim()
        };
    }

    public static SuiteValidationResult Validate(FanCommissioningSessionV1 session)
    {
        List<string> errors = [];
        if (session.SchemaVersion != FanCommissioningSessionV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported commissioning schema {session.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(session.Id)
            || string.IsNullOrWhiteSpace(session.CapabilityId)
            || string.IsNullOrWhiteSpace(session.RpmSensorId)
            || string.IsNullOrWhiteSpace(session.HeaderName)
            || session.HeaderName.Trim().Length > 80)
        {
            errors.Add("Commissioning identity, RPM sensor, and a header name up to 80 characters are required.");
        }
        if (session.IsCpuOrPump && session.AllowFanStop)
        {
            errors.Add("CPU fans and pumps cannot allow fan stop.");
        }
        if (session.UpdatedAt < session.StartedAt)
        {
            errors.Add("Commissioning update time cannot precede its start time.");
        }
        if (session.State == FanCommissioningState.Completed
            && (string.IsNullOrWhiteSpace(session.CalibrationId)
                || !session.HeaderConfirmed
                || !session.PhysicalHeaderObserved))
        {
            errors.Add("A completed session requires observed physical identity and a calibration reference.");
        }
        if (session.State == FanCommissioningState.ReadyForCalibration && !session.HeaderConfirmed)
        {
            errors.Add("A ready-for-calibration session requires physical header confirmation.");
        }
        return new SuiteValidationResult(errors.Count == 0, errors, []);
    }

    public static FanCommissioningSessionV1 Confirm(
        FanCommissioningSessionV1 session,
        bool headerConfirmed,
        bool physicalHeaderObserved,
        string headerName,
        string? notes,
        DateTimeOffset now)
    {
        if (session.State != FanCommissioningState.AwaitingIdentification)
        {
            throw new InvalidOperationException("Only an awaiting-identification session can be confirmed.");
        }
        if (string.IsNullOrWhiteSpace(headerName) || headerName.Trim().Length > 80)
        {
            throw new InvalidOperationException("A header name up to 80 characters is required.");
        }
        return session with
        {
            HeaderName = headerName.Trim(),
            HeaderConfirmed = headerConfirmed,
            PhysicalHeaderObserved = headerConfirmed && physicalHeaderObserved,
            State = headerConfirmed ? FanCommissioningState.ReadyForCalibration : FanCommissioningState.Failed,
            Notes = string.IsNullOrWhiteSpace(notes) ? session.Notes : notes.Trim(),
            UpdatedAt = now,
            Error = headerConfirmed ? null : "The user did not confirm the physical fan/header identity."
        };
    }

    public static FanCommissioningSessionV1 Complete(
        FanCommissioningSessionV1 session,
        string calibrationId,
        DateTimeOffset now)
    {
        if (session.State != FanCommissioningState.ReadyForCalibration
            || !session.HeaderConfirmed
            || !session.PhysicalHeaderObserved)
        {
            throw new InvalidOperationException("A visually observed, confirmed session must be ready for calibration before it can be completed.");
        }
        if (string.IsNullOrWhiteSpace(calibrationId))
        {
            throw new InvalidOperationException("A completed session requires a calibration reference.");
        }
        return session with
        {
            State = FanCommissioningState.Completed,
            CalibrationId = calibrationId,
            UpdatedAt = now,
            Error = null
        };
    }

    public static FanCommissioningSessionV1 Cancel(FanCommissioningSessionV1 session, DateTimeOffset now) => session with
    {
        State = FanCommissioningState.Cancelled,
        UpdatedAt = now,
        Error = null
    };

    /// <summary>
    /// Returns a visible, non-zero duty for an explicit physical-header pulse.
    /// The caller must still use prepare/apply/verify/reset around the pulse.
    /// </summary>
    public static double GetIdentificationPulseDuty(NumericRange range)
    {
        if (!double.IsFinite(range.Minimum)
            || !double.IsFinite(range.Maximum)
            || !double.IsFinite(range.Step)
            || range.Minimum > range.Maximum
            || range.Step <= 0
            || range.Maximum <= 0)
        {
            throw new InvalidOperationException("The controller does not expose valid positive bounds for an identification pulse.");
        }

        double bounded = Math.Clamp(60, range.Minimum, range.Maximum);
        double steps = Math.Round((bounded - range.Minimum) / range.Step, MidpointRounding.AwayFromZero);
        double aligned = range.Minimum + (steps * range.Step);
        return Math.Clamp(aligned, range.Minimum, range.Maximum);
    }

    /// <summary>
    /// Builds and prepares an identity pulse without applying it. This is the
    /// final no-write gate before a service may reserve a physical operation.
    /// </summary>
    public static Task<PreparedAction> PrepareIdentificationPulseAsync(
        CapabilityDescriptor capability,
        IHardwareAdapter adapter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(adapter);
        if (capability.Range is null)
        {
            throw new InvalidOperationException("A bounded cooling control is required for an identification preflight.");
        }

        double duty = GetIdentificationPulseDuty(capability.Range);
        ProfileAction action = new(
            $"commission-preflight:{Guid.NewGuid():N}",
            capability.AdapterId,
            capability.Id,
            ControlValue.FromNumeric(duty),
            Required: true,
            Order: 0);
        return adapter.PrepareAsync(action, cancellationToken);
    }
}
