using System.Security.Cryptography;
using System.Text;
using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed record EffectScriptPackageInspection(
    SuiteValidationResult Validation,
    string? EntryPointPath,
    string? Source);

public static class EffectScriptPackageValidator
{
    public const int MaximumSourceBytes = 1024 * 1024;
    public const int MaximumLedCount = 4096;
    public const int MaximumFramesPerSecond = 60;

    public static async Task<EffectScriptPackageInspection> InspectAsync(
        EffectScriptManifestV1 manifest,
        string packageRoot,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];
        if (manifest.SchemaVersion != EffectScriptManifestV1.CurrentSchemaVersion)
        {
            errors.Add("Unsupported effect-script manifest schema.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add("Effect script ID and name are required.");
        }
        if (!manifest.Trusted)
        {
            errors.Add("Effect script has not been trusted for its current hash.");
        }
        if (manifest.MaximumFramesPerSecond is < 1 or > MaximumFramesPerSecond)
        {
            errors.Add($"Effect frame rate must be 1-{MaximumFramesPerSecond} FPS.");
        }
        if (manifest.MaximumLedCount is < 1 or > MaximumLedCount)
        {
            errors.Add($"Effect LED limit must be 1-{MaximumLedCount}.");
        }
        if (string.IsNullOrWhiteSpace(packageRoot) || !Path.IsPathFullyQualified(packageRoot))
        {
            errors.Add("Effect package root must be an absolute path.");
            return Result(errors, null, null);
        }
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint) || Path.IsPathFullyQualified(manifest.EntryPoint))
        {
            errors.Add("Effect entry point must be a relative package path.");
            return Result(errors, null, null);
        }

        string root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootPrefix = root + Path.DirectorySeparatorChar;
        string entryPoint = Path.GetFullPath(Path.Combine(root, manifest.EntryPoint));
        if (!entryPoint.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Effect entry point escapes its imported package.");
            return Result(errors, null, null);
        }
        if (!entryPoint.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || !File.Exists(entryPoint))
        {
            errors.Add("Effect entry point must be an existing JavaScript file.");
            return Result(errors, null, null);
        }
        FileInfo file = new(entryPoint);
        if (file.Length is <= 0 or > MaximumSourceBytes)
        {
            errors.Add($"Effect source must be 1 byte to {MaximumSourceBytes} bytes.");
            return Result(errors, null, null);
        }
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
        {
            errors.Add("Effect manifest SHA-256 is invalid.");
            return Result(errors, null, null);
        }

        byte[] bytes = await File.ReadAllBytesAsync(entryPoint, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actualHash),
                System.Text.Encoding.ASCII.GetBytes(manifest.Sha256.ToLowerInvariant())))
        {
            errors.Add("Effect source changed after trust was granted.");
            return Result(errors, entryPoint, null);
        }
        string source;
        try
        {
            source = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            errors.Add("Effect source must be valid UTF-8.");
            return Result(errors, entryPoint, null);
        }
        return Result(errors, entryPoint, source);
    }

    private static EffectScriptPackageInspection Result(
        List<string> errors,
        string? entryPoint,
        string? source) => new(
            new SuiteValidationResult(errors.Count == 0, errors, []),
            entryPoint,
            source);
}
