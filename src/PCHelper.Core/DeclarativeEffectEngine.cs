using System.Globalization;
using PCHelper.Contracts;

namespace PCHelper.Core;

public readonly record struct LedCoordinate(int Index, double X, double Y);

public sealed record EffectFrameInput(
    TimeSpan Elapsed,
    IReadOnlyList<LedCoordinate> Leds,
    IReadOnlyDictionary<string, double> Sensors,
    IReadOnlyList<double> AudioBins,
    IReadOnlyDictionary<int, RgbColour> ScreenColours,
    IReadOnlyDictionary<string, RgbColour> Events);

public static class DeclarativeEffectEngine
{
    private const int MaximumLedCount = 4096;

    public static IReadOnlyList<RgbColour> Render(EffectGraphV1 graph, EffectFrameInput input)
    {
        SuiteValidationResult validation = EffectGraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        }
        if (input.Leds.Count is 0 or > MaximumLedCount)
        {
            throw new ArgumentOutOfRangeException(nameof(input), $"Effect frames require 1-{MaximumLedCount} LEDs.");
        }
        if (input.Leds.Select(led => led.Index).Distinct().Count() != input.Leds.Count
            || input.Leds.Any(led => led.Index < 0 || !double.IsFinite(led.X) || !double.IsFinite(led.Y)))
        {
            throw new ArgumentException("LED indices and coordinates must be unique, non-negative, and finite.", nameof(input));
        }

        Dictionary<string, EffectNodeV1> nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        Dictionary<string, RgbColour[]> cache = new(StringComparer.Ordinal);
        return Evaluate(graph.OutputNodeId);

        RgbColour[] Evaluate(string id)
        {
            if (cache.TryGetValue(id, out RgbColour[]? cached))
            {
                return cached;
            }
            EffectNodeV1 node = nodes[id];
            RgbColour[] frame = node.Kind switch
            {
                EffectNodeKind.Solid => Solid(node, input.Leds.Count),
                EffectNodeKind.Gradient => Gradient(node, input.Leds),
                EffectNodeKind.Wave => Wave(node, input),
                EffectNodeKind.Breathing => Breathing(node, input),
                EffectNodeKind.Spectrum => Spectrum(node, input),
                EffectNodeKind.Temperature => Temperature(node, input),
                EffectNodeKind.Notification or EffectNodeKind.GameEvent => Event(node, input),
                EffectNodeKind.AudioSpectrum => Audio(node, input),
                EffectNodeKind.ScreenAmbience => Screen(input),
                EffectNodeKind.Blend => Blend(node, Evaluate, input.Leds.Count),
                EffectNodeKind.Script => throw new InvalidOperationException("Script effect nodes must run in PCHelper.EffectHost."),
                _ => throw new ArgumentOutOfRangeException(nameof(graph), $"Unsupported effect node kind {node.Kind}.")
            };
            cache[id] = frame;
            return frame;
        }
    }

    private static RgbColour[] Solid(EffectNodeV1 node, int count)
    {
        RgbColour colour = Colour(node, "colour", new RgbColour(105, 173, 255));
        return Enumerable.Repeat(colour, count).ToArray();
    }

    private static RgbColour[] Gradient(EffectNodeV1 node, IReadOnlyList<LedCoordinate> leds)
    {
        RgbColour start = Colour(node, "start", new RgbColour(105, 173, 255));
        RgbColour end = Colour(node, "end", new RgbColour(80, 214, 160));
        bool vertical = Text(node, "axis", "x").Equals("y", StringComparison.OrdinalIgnoreCase);
        return leds.Select(led => RgbColour.Blend(start, end, Math.Clamp(vertical ? led.Y : led.X, 0, 1))).ToArray();
    }

    private static RgbColour[] Wave(EffectNodeV1 node, EffectFrameInput input)
    {
        RgbColour start = Colour(node, "start", new RgbColour(105, 173, 255));
        RgbColour end = Colour(node, "end", new RgbColour(80, 214, 160));
        double speed = Number(node, "speed", 0.25);
        double width = Math.Max(0.01, Number(node, "width", 1));
        double phase = input.Elapsed.TotalSeconds * speed;
        return input.Leds.Select(led =>
        {
            double position = ((led.X / width) + phase) % 1;
            if (position < 0) position += 1;
            double triangle = 1 - Math.Abs((position * 2) - 1);
            return RgbColour.Blend(start, end, triangle);
        }).ToArray();
    }

    private static RgbColour[] Breathing(EffectNodeV1 node, EffectFrameInput input)
    {
        RgbColour colour = Colour(node, "colour", new RgbColour(105, 173, 255));
        double speed = Math.Max(0, Number(node, "speed", 0.5));
        double minimum = Math.Clamp(Number(node, "minimum", 0.05), 0, 1);
        double level = minimum + ((1 - minimum) * ((Math.Sin(input.Elapsed.TotalSeconds * speed * Math.Tau) + 1) / 2));
        return Enumerable.Repeat(colour.Scale(level), input.Leds.Count).ToArray();
    }

    private static RgbColour[] Spectrum(EffectNodeV1 node, EffectFrameInput input)
    {
        double speed = Number(node, "speed", 0.1);
        return input.Leds.Select(led => FromHsv(((led.X + (input.Elapsed.TotalSeconds * speed)) % 1 + 1) % 1, 1, 1)).ToArray();
    }

    private static RgbColour[] Temperature(EffectNodeV1 node, EffectFrameInput input)
    {
        string sensor = Text(node, "sensorId", string.Empty);
        if (!input.Sensors.TryGetValue(sensor, out double value) || !double.IsFinite(value))
        {
            throw new InvalidDataException($"Temperature effect sensor '{sensor}' is unavailable.");
        }
        double minimum = Number(node, "minimum", 30);
        double maximum = Number(node, "maximum", 90);
        if (maximum <= minimum)
        {
            throw new InvalidDataException("Temperature effect maximum must exceed minimum.");
        }
        RgbColour cold = Colour(node, "cold", new RgbColour(65, 145, 255));
        RgbColour hot = Colour(node, "hot", new RgbColour(255, 90, 70));
        RgbColour result = RgbColour.Blend(cold, hot, (value - minimum) / (maximum - minimum));
        return Enumerable.Repeat(result, input.Leds.Count).ToArray();
    }

    private static RgbColour[] Event(EffectNodeV1 node, EffectFrameInput input)
    {
        string eventId = Text(node, "eventId", string.Empty);
        RgbColour fallback = Colour(node, "fallback", new RgbColour(0, 0, 0));
        RgbColour result = input.Events.TryGetValue(eventId, out RgbColour active) ? active : fallback;
        return Enumerable.Repeat(result, input.Leds.Count).ToArray();
    }

    private static RgbColour[] Audio(EffectNodeV1 node, EffectFrameInput input)
    {
        RgbColour colour = Colour(node, "colour", new RgbColour(80, 214, 160));
        if (input.AudioBins.Count == 0)
        {
            return Enumerable.Repeat(new RgbColour(0, 0, 0), input.Leds.Count).ToArray();
        }
        return input.Leds.Select(led =>
        {
            int bin = Math.Clamp((int)Math.Floor(Math.Clamp(led.X, 0, 0.999999) * input.AudioBins.Count), 0, input.AudioBins.Count - 1);
            return colour.Scale(input.AudioBins[bin]);
        }).ToArray();
    }

    private static RgbColour[] Screen(EffectFrameInput input) => input.Leds
        .Select(led => input.ScreenColours.TryGetValue(led.Index, out RgbColour colour) ? colour : new RgbColour(0, 0, 0))
        .ToArray();

    private static RgbColour[] Blend(
        EffectNodeV1 node,
        Func<string, RgbColour[]> evaluate,
        int count)
    {
        if (node.InputNodeIds.Count != 2)
        {
            throw new InvalidDataException($"Blend node '{node.Id}' requires exactly two inputs.");
        }
        RgbColour[] left = evaluate(node.InputNodeIds[0]);
        RgbColour[] right = evaluate(node.InputNodeIds[1]);
        double amount = Number(node, "amount", 0.5);
        return Enumerable.Range(0, count).Select(index => RgbColour.Blend(left[index], right[index], amount)).ToArray();
    }

    private static double Number(EffectNodeV1 node, string key, double fallback) =>
        node.NumericParameters.TryGetValue(key, out double value) && double.IsFinite(value) ? value : fallback;

    private static string Text(EffectNodeV1 node, string key, string fallback) =>
        node.TextParameters.TryGetValue(key, out string? value) ? value : fallback;

    private static RgbColour Colour(EffectNodeV1 node, string key, RgbColour fallback)
    {
        string text = Text(node, key, string.Empty).TrimStart('#');
        return text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? new RgbColour((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;
    }

    private static RgbColour FromHsv(double hue, double saturation, double value)
    {
        double scaled = hue * 6;
        int sector = (int)Math.Floor(scaled);
        double fraction = scaled - sector;
        double p = value * (1 - saturation);
        double q = value * (1 - (fraction * saturation));
        double t = value * (1 - ((1 - fraction) * saturation));
        (double red, double green, double blue) = (sector % 6) switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return new RgbColour((byte)Math.Round(red * 255), (byte)Math.Round(green * 255), (byte)Math.Round(blue * 255));
    }
}
