using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// The user-agent owns this interface. Implementations must be dormant until
/// the user explicitly starts a visible recording session and must discard
/// captured input when cancellation is requested.
/// </summary>
public interface IMacroRecorder : IAsyncDisposable
{
    bool IsRecording { get; }

    Task StartAsync(TimeSpan maximumDuration, CancellationToken cancellationToken);

    Task<IReadOnlyList<MacroStepV1>> StopAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public static class MacroRecordingValidator
{
    public static SuiteValidationResult Validate(MacroRecordingSessionV1 session)
    {
        List<string> errors = [];
        if (session.SchemaVersion != MacroRecordingSessionV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported macro-recording schema {session.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.Name))
        {
            errors.Add("Recording identity and name are required.");
        }
        if (session.MaximumDuration < TimeSpan.FromSeconds(1) || session.MaximumDuration > TimeSpan.FromMinutes(10))
        {
            errors.Add("Recording duration must be between 1 second and 10 minutes.");
        }
        if (session.StepCount < 0 || session.StepCount > 10_000)
        {
            errors.Add("Recording step count is outside the supported range.");
        }
        if (session.State == MacroRecordingState.Completed && string.IsNullOrWhiteSpace(session.MacroId))
        {
            errors.Add("A completed recording must reference its saved macro.");
        }
        return new SuiteValidationResult(errors.Count == 0, errors, []);
    }
}
