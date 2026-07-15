using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class MacroPlaybackEngineTests
{
    [Fact]
    public async Task PlaysValidatedStepsInOrder()
    {
        FakeSink sink = new();
        MacroPlaybackEngine engine = new(sink, new ImmediateDelay());
        MacroV1 macro = Macro(
        [
            new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero),
            new MacroStepV1(MacroStepKind.MouseMove, 0, 100, 200, 0, TimeSpan.FromMilliseconds(5)),
            new MacroStepV1(MacroStepKind.MouseWheel, 0, 0, 0, 120, TimeSpan.Zero),
            new MacroStepV1(MacroStepKind.KeyUp, 65, 0, 0, 0, TimeSpan.Zero)
        ]);

        MacroExecutionResultV1 result = await engine.ExecuteAsync(macro, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(4, result.ExecutedSteps);
        Assert.Equal(["key-down:65", "move:100,200", "wheel:120", "key-up:65"], sink.Events);
    }

    [Fact]
    public async Task SinkFailureReleasesPressedInputs()
    {
        FakeSink sink = new() { FailMove = true };
        MacroPlaybackEngine engine = new(sink, new ImmediateDelay());
        MacroV1 macro = Macro(
        [
            new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero),
            new MacroStepV1(MacroStepKind.MouseMove, 0, 100, 200, 0, TimeSpan.Zero),
            new MacroStepV1(MacroStepKind.KeyUp, 65, 0, 0, 0, TimeSpan.Zero)
        ]);

        MacroExecutionResultV1 result = await engine.ExecuteAsync(macro, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("input failure", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("key-up:65", sink.Events[^1]);
    }

    private static MacroV1 Macro(IReadOnlyList<MacroStepV1> steps) => new(
        MacroV1.CurrentSchemaVersion,
        "macro.test",
        "Test macro",
        steps);

    private sealed class ImmediateDelay : IMacroDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSink : IMacroInputSink
    {
        public List<string> Events { get; } = [];
        public bool FailMove { get; init; }
        public void KeyDown(int code) => Events.Add($"key-down:{code}");
        public void KeyUp(int code) => Events.Add($"key-up:{code}");
        public void MouseButtonDown(int code) => Events.Add($"button-down:{code}");
        public void MouseButtonUp(int code) => Events.Add($"button-up:{code}");
        public void MouseMove(int x, int y)
        {
            if (FailMove) throw new InvalidOperationException("input failure");
            Events.Add($"move:{x},{y}");
        }
        public void MouseWheel(int delta) => Events.Add($"wheel:{delta}");
        public void MediaKey(int code) => Events.Add($"media:{code}");
    }
}
