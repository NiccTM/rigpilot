using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class DeclarativeEffectEngineTests
{
    [Fact]
    public void GradientUsesPhysicalLedCoordinates()
    {
        EffectGraphV1 graph = Graph(
            [Node("gradient", EffectNodeKind.Gradient, text: new Dictionary<string, string> { ["start"] = "#000000", ["end"] = "#FFFFFF" })],
            "gradient");

        IReadOnlyList<RgbColour> frame = DeclarativeEffectEngine.Render(graph, Input());

        Assert.Equal(new RgbColour(0, 0, 0), frame[0]);
        Assert.Equal(new RgbColour(128, 128, 128), frame[1]);
        Assert.Equal(new RgbColour(255, 255, 255), frame[2]);
    }

    [Fact]
    public void TemperatureClampsBetweenConfiguredColours()
    {
        EffectGraphV1 graph = Graph(
            [Node(
                "temperature",
                EffectNodeKind.Temperature,
                numeric: new Dictionary<string, double> { ["minimum"] = 40, ["maximum"] = 80 },
                text: new Dictionary<string, string> { ["sensorId"] = "cpu", ["cold"] = "0000FF", ["hot"] = "FF0000" })],
            "temperature");

        IReadOnlyList<RgbColour> frame = DeclarativeEffectEngine.Render(
            graph,
            Input(sensors: new Dictionary<string, double> { ["cpu"] = 60 }));

        Assert.All(frame, colour => Assert.Equal(new RgbColour(128, 0, 128), colour));
    }

    [Fact]
    public void AudioSpectrumScalesColourByLocalFrequencyBin()
    {
        EffectGraphV1 graph = Graph(
            [Node("audio", EffectNodeKind.AudioSpectrum, text: new Dictionary<string, string> { ["colour"] = "00FF00" })],
            "audio");

        IReadOnlyList<RgbColour> frame = DeclarativeEffectEngine.Render(graph, Input(audio: [0, 0.5, 1]));

        Assert.Equal(new RgbColour(0, 0, 0), frame[0]);
        Assert.Equal(new RgbColour(0, 128, 0), frame[1]);
        Assert.Equal(new RgbColour(0, 255, 0), frame[2]);
    }

    [Fact]
    public void BlendCombinesTwoDeclarativeInputs()
    {
        EffectGraphV1 graph = Graph(
        [
            Node("red", EffectNodeKind.Solid, text: new Dictionary<string, string> { ["colour"] = "FF0000" }),
            Node("blue", EffectNodeKind.Solid, text: new Dictionary<string, string> { ["colour"] = "0000FF" }),
            Node("blend", EffectNodeKind.Blend, inputs: ["red", "blue"], numeric: new Dictionary<string, double> { ["amount"] = 0.25 })
        ], "blend");

        IReadOnlyList<RgbColour> frame = DeclarativeEffectEngine.Render(graph, Input());

        Assert.All(frame, colour => Assert.Equal(new RgbColour(191, 0, 64), colour));
    }

    private static EffectGraphV1 Graph(IReadOnlyList<EffectNodeV1> nodes, string output) => new(
        EffectGraphV1.CurrentSchemaVersion,
        "effect.test",
        "Test effect",
        nodes,
        output,
        60);

    private static EffectNodeV1 Node(
        string id,
        EffectNodeKind kind,
        IReadOnlyList<string>? inputs = null,
        IReadOnlyDictionary<string, double>? numeric = null,
        IReadOnlyDictionary<string, string>? text = null) => new(
            id,
            kind,
            inputs ?? [],
            numeric ?? new Dictionary<string, double>(),
            text ?? new Dictionary<string, string>());

    private static EffectFrameInput Input(
        IReadOnlyDictionary<string, double>? sensors = null,
        IReadOnlyList<double>? audio = null) => new(
            TimeSpan.FromSeconds(1),
            [new LedCoordinate(0, 0, 0), new LedCoordinate(1, 0.5, 0.5), new LedCoordinate(2, 1, 1)],
            sensors ?? new Dictionary<string, double>(),
            audio ?? [],
            new Dictionary<int, RgbColour>(),
            new Dictionary<string, RgbColour>());
}
