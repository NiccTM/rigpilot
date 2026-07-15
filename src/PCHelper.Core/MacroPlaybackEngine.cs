using System.Diagnostics;
using PCHelper.Contracts;

namespace PCHelper.Core;

public interface IMacroInputSink
{
    void KeyDown(int code);
    void KeyUp(int code);
    void MouseButtonDown(int code);
    void MouseButtonUp(int code);
    void MouseMove(int x, int y);
    void MouseWheel(int delta);
    void MediaKey(int code);
}

public interface IMacroDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemMacroDelay : IMacroDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class MacroPlaybackEngine(IMacroInputSink input, IMacroDelay delay)
{
    public async Task<MacroExecutionResultV1> ExecuteAsync(MacroV1 macro, CancellationToken cancellationToken)
    {
        SuiteValidationResult validation = MacroValidator.Validate(macro);
        if (!validation.IsValid)
        {
            return new MacroExecutionResultV1(1, macro.Id, false, 0, TimeSpan.Zero, string.Join(" ", validation.Errors));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        HashSet<int> pressedKeys = [];
        HashSet<int> pressedButtons = [];
        int executed = 0;
        string? error = null;
        try
        {
            foreach (MacroStepV1 step in macro.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Delay > TimeSpan.Zero)
                {
                    await delay.WaitAsync(step.Delay, cancellationToken).ConfigureAwait(false);
                }
                switch (step.Kind)
                {
                    case MacroStepKind.KeyDown:
                        input.KeyDown(step.Code);
                        pressedKeys.Add(step.Code);
                        break;
                    case MacroStepKind.KeyUp:
                        input.KeyUp(step.Code);
                        pressedKeys.Remove(step.Code);
                        break;
                    case MacroStepKind.MouseButtonDown:
                        input.MouseButtonDown(step.Code);
                        pressedButtons.Add(step.Code);
                        break;
                    case MacroStepKind.MouseButtonUp:
                        input.MouseButtonUp(step.Code);
                        pressedButtons.Remove(step.Code);
                        break;
                    case MacroStepKind.MouseMove:
                        input.MouseMove(step.X, step.Y);
                        break;
                    case MacroStepKind.MouseWheel:
                        input.MouseWheel(step.Delta);
                        break;
                    case MacroStepKind.MediaKey:
                        input.MediaKey(step.Code);
                        break;
                    case MacroStepKind.Delay:
                        break;
                }
                executed++;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.Message;
        }
        finally
        {
            foreach (int code in pressedKeys)
            {
                try { input.KeyUp(code); } catch { }
            }
            foreach (int code in pressedButtons)
            {
                try { input.MouseButtonUp(code); } catch { }
            }
            stopwatch.Stop();
        }
        return new MacroExecutionResultV1(
            MacroExecutionResultV1.CurrentSchemaVersion,
            macro.Id,
            error is null && executed == macro.Steps.Count,
            executed,
            stopwatch.Elapsed,
            error);
    }
}
