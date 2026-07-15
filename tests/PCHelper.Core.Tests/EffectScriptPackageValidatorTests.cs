using System.Security.Cryptography;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class EffectScriptPackageValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PCHelperEffectTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcceptsTrustedHashBoundEntryPoint()
    {
        Directory.CreateDirectory(_root);
        string source = "globalThis.render = input => input.leds.map(() => ({ red: 1, green: 2, blue: 3 }));";
        string path = Path.Combine(_root, "effect.js");
        await File.WriteAllTextAsync(path, source);
        EffectScriptManifestV1 manifest = Manifest(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));

        EffectScriptPackageInspection result = await EffectScriptPackageValidator.InspectAsync(manifest, _root, CancellationToken.None);

        Assert.True(result.Validation.IsValid);
        Assert.Equal(source, result.Source);
        Assert.Equal(path, result.EntryPointPath, ignoreCase: true);
    }

    [Fact]
    public async Task RejectsChangedSourceAndEscapingEntryPoint()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "effect.js");
        await File.WriteAllTextAsync(path, "globalThis.render = () => []; // changed");

        EffectScriptPackageInspection changed = await EffectScriptPackageValidator.InspectAsync(
            Manifest(new string('a', 64)),
            _root,
            CancellationToken.None);
        EffectScriptPackageInspection escaped = await EffectScriptPackageValidator.InspectAsync(
            Manifest(new string('a', 64)) with { EntryPoint = "..\\effect.js" },
            _root,
            CancellationToken.None);

        Assert.False(changed.Validation.IsValid);
        Assert.Contains(changed.Validation.Errors, error => error.Contains("changed", StringComparison.OrdinalIgnoreCase));
        Assert.False(escaped.Validation.IsValid);
        Assert.Contains(escaped.Validation.Errors, error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    private static EffectScriptManifestV1 Manifest(string hash) => new(
        EffectScriptManifestV1.CurrentSchemaVersion,
        "effect.test",
        "Test effect",
        "effect.js",
        hash,
        Trusted: true,
        MaximumFramesPerSecond: 30,
        MaximumLedCount: 128);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
