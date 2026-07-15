using PCHelper.Contracts;

namespace PCHelper.Core;

public interface ICaptureBackend
{
    Task StartAsync(CaptureSessionV1 session, CancellationToken cancellationToken);

    Task<CaptureMetricsV1> StopAsync(string sessionId, CancellationToken cancellationToken);

    Task AbortAsync(string sessionId, CancellationToken cancellationToken);
}

public interface ICaptureSessionJournal
{
    Task SaveAsync(CaptureSessionV1 session, CancellationToken cancellationToken);
}

public sealed class CaptureSessionCoordinator(ICaptureBackend backend, ICaptureSessionJournal journal)
{
    public async Task<CaptureSessionV1> StartAsync(
        CapturePresetV1 preset,
        CaptureTargetV1 target,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Validate(preset, target, outputPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CaptureSessionV1 session = new(
            CaptureSessionV1.CurrentSchemaVersion,
            $"capture.{Guid.NewGuid():N}",
            preset,
            target,
            Path.GetFullPath(outputPath),
            CaptureSessionState.Planned,
            now,
            now,
            new CaptureMetricsV1(0, 0, TimeSpan.Zero, 0),
            null);
        await journal.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        session = await TransitionAsync(session, CaptureSessionState.Starting, cancellationToken).ConfigureAwait(false);
        try
        {
            await backend.StartAsync(session, cancellationToken).ConfigureAwait(false);
            return await TransitionAsync(session, CaptureSessionState.Recording, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await backend.AbortAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            session = session with { Error = "Capture start was cancelled." };
            await TransitionAsync(session, CaptureSessionState.Failed, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            session = session with { Error = exception.Message };
            return await TransitionAsync(session, CaptureSessionState.Failed, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<CaptureSessionV1> StopAsync(CaptureSessionV1 session, CancellationToken cancellationToken)
    {
        if (session.State != CaptureSessionState.Recording)
        {
            throw new InvalidOperationException("Only an active capture can be stopped.");
        }
        session = await TransitionAsync(session, CaptureSessionState.Stopping, cancellationToken).ConfigureAwait(false);
        try
        {
            CaptureMetricsV1 metrics = await backend.StopAsync(session.Id, cancellationToken).ConfigureAwait(false);
            session = session with { Metrics = metrics };
            return await TransitionAsync(session, CaptureSessionState.Completed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await backend.AbortAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            session = session with { Error = "Capture stop was cancelled." };
            await TransitionAsync(session, CaptureSessionState.Failed, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await backend.AbortAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            session = session with { Error = exception.Message };
            return await TransitionAsync(session, CaptureSessionState.Failed, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<CaptureSessionV1> FailAsync(CaptureSessionV1 session, string reason, CancellationToken cancellationToken)
    {
        if (session.State is CaptureSessionState.Completed or CaptureSessionState.Failed)
        {
            return session;
        }
        await backend.AbortAsync(session.Id, cancellationToken).ConfigureAwait(false);
        session = session with { Error = reason };
        return await TransitionAsync(session, CaptureSessionState.Failed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CaptureSessionV1> TransitionAsync(
        CaptureSessionV1 session,
        CaptureSessionState state,
        CancellationToken cancellationToken)
    {
        CaptureSessionV1 updated = session with { State = state, UpdatedAt = DateTimeOffset.UtcNow };
        await journal.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static void Validate(CapturePresetV1 preset, CaptureTargetV1 target, string outputPath)
    {
        if (preset.SchemaVersion != CapturePresetV1.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(preset.Id)
            || preset.FramesPerSecond is < 1 or > 240
            || preset.VideoBitrateKbps is < 500 or > 200_000
            || preset.Container is not ("mp4" or "mkv"))
        {
            throw new InvalidDataException("Capture preset is invalid.");
        }
        if (string.IsNullOrWhiteSpace(target.StableId) || target.Kind != preset.TargetKind)
        {
            throw new InvalidDataException("Capture target does not match the preset.");
        }
        if (!Path.IsPathFullyQualified(outputPath)
            || !Path.GetExtension(outputPath).Equals($".{preset.Container}", StringComparison.OrdinalIgnoreCase)
            || File.Exists(outputPath))
        {
            throw new InvalidDataException("Capture output must be a new absolute path with the preset container extension.");
        }
    }
}
