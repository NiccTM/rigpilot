using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

public sealed class EffectHostTests
{
    [Fact]
    public async Task HashBoundEffectRendersInsideHostProcess()
    {
        EffectRenderResultV1 result = await RunHostAsync(
            "globalThis.render = input => input.leds.map((_, i) => ({ red: i + 1, green: 2, blue: 3 }));",
            watchdogMilliseconds: 2000);

        Assert.True(result.Completed, result.Error);
        Assert.Equal(2, result.Colours.Count);
        Assert.Equal((byte)1, result.Colours[0].Red);
        Assert.Equal((byte)2, result.Colours[1].Red);
    }

    [Fact]
    public async Task NetworkRequestsAreDeniedInsideEffectSandbox()
    {
        EffectRenderResultV1 result = await RunHostAsync(
            "globalThis.render = async input => { try { await fetch('https://example.com/'); return input.leds.map(() => ({ red: 255, green: 0, blue: 0 })); } catch { return input.leds.map(() => ({ red: 0, green: 255, blue: 0 })); } };",
            watchdogMilliseconds: 2000);

        Assert.True(result.Completed, result.Error);
        Assert.All(result.Colours, colour => Assert.Equal((byte)255, colour.Green));
    }

    [Fact]
    public async Task RunawayEffectTripsWatchdog()
    {
        EffectRenderResultV1 result = await RunHostAsync(
            "globalThis.render = () => { while (true) {} };",
            watchdogMilliseconds: 100);

        Assert.False(result.Completed);
        Assert.True(result.TimedOut);
        Assert.Contains("watchdog", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<EffectRenderResultV1> RunHostAsync(string source, int watchdogMilliseconds)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pchelper-effect-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "effect.js");
            await File.WriteAllTextAsync(sourcePath, source);
            string hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
            EffectRenderRequestV1 request = new(
                EffectRenderRequestV1.CurrentSchemaVersion,
                new EffectScriptManifestV1(
                    EffectScriptManifestV1.CurrentSchemaVersion,
                    "effect.integration",
                    "Integration effect",
                    "effect.js",
                    hash,
                    Trusted: true,
                    MaximumFramesPerSecond: 30,
                    MaximumLedCount: 8),
                directory,
                new EffectRenderInputV1(
                    ElapsedMilliseconds: 10,
                    [new EffectLedCoordinateV1(0, 0, 0), new EffectLedCoordinateV1(1, 1, 0)],
                    new Dictionary<string, double>(),
                    [],
                    new Dictionary<int, EffectColourV1>(),
                    new Dictionary<string, EffectColourV1>()),
                watchdogMilliseconds);
            string requestPath = Path.Combine(directory, "request.json");
            string responsePath = Path.Combine(directory, "response.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonDefaults.Options));
            string? configuredHost = Environment.GetEnvironmentVariable("PCHELPER_EFFECT_HOST_PATH");
            string host = string.IsNullOrWhiteSpace(configuredHost)
                ? Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..", "..",
                    "src", "PCHelper.EffectHost", "bin", "Release", "net10.0-windows10.0.19041.0", "PCHelper.EffectHost.exe"))
                : Path.GetFullPath(configuredHost);
            Assert.True(File.Exists(host), $"Effect Host build output is missing: {host}");
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = host,
                Arguments = $"--request \"{requestPath}\" --response \"{responsePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Effect Host did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            string json = File.Exists(responsePath) ? await File.ReadAllTextAsync(responsePath) : await output;
            string diagnostics = await error;
            EffectRenderResultV1? result;
            try { result = JsonSerializer.Deserialize<EffectRenderResultV1>(json, JsonDefaults.Options); }
            catch (JsonException) { result = null; }
            Assert.True(result is not null, $"Effect Host produced no result (exit {process.ExitCode}). stderr: {diagnostics}");
            return result;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
