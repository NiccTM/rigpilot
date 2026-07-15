using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using PCHelper.Contracts;

namespace PCHelper.Core;

public sealed class AdapterPackManager
{
    private const int MaximumArchiveBytes = 64 * 1024 * 1024;
    private const long MaximumExpandedBytes = 128L * 1024 * 1024;
    private const int MaximumEntries = 256;
    private const string ManifestName = "manifest.json";
    private const string SignatureName = "signature.ed25519";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _installRoot;
    private readonly IReadOnlyDictionary<string, byte[]> _publisherKeys;
    private readonly IReadOnlySet<string> _developmentPackageHashes;

    public AdapterPackManager(
        string installRoot,
        IReadOnlyDictionary<string, byte[]> publisherKeys,
        IReadOnlySet<string>? developmentPackageHashes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        _installRoot = Path.GetFullPath(installRoot);
        _publisherKeys = publisherKeys ?? throw new ArgumentNullException(nameof(publisherKeys));
        _developmentPackageHashes = developmentPackageHashes
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AdapterPackInspection> InspectAsync(string packagePath, CancellationToken cancellationToken)
    {
        byte[] package = await ReadPackageAsync(packagePath, cancellationToken).ConfigureAwait(false);
        return Inspect(package);
    }

    public async Task<string> InstallAsync(string packagePath, CancellationToken cancellationToken)
    {
        byte[] package = await ReadPackageAsync(packagePath, cancellationToken).ConfigureAwait(false);
        AdapterPackInspection inspection = Inspect(package);
        if (!inspection.Valid || inspection.Manifest is not AdapterPackManifestV1 manifest)
        {
            throw new InvalidDataException(string.Join(" ", inspection.Errors));
        }

        string destination = GetContainedPath(_installRoot, manifest.Id, manifest.Version);
        string stagingRoot = GetContainedPath(_installRoot, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using MemoryStream stream = new(package, writable: false);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = NormaliseEntryPath(entry.FullName);
                if (relativePath.EndsWith('/'))
                {
                    continue;
                }

                string outputPath = GetContainedPath(stagingRoot, relativePath.Split('/'));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using Stream input = entry.Open();
                await using FileStream output = new(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (Directory.Exists(destination))
            {
                throw new IOException($"Adapter pack {manifest.Id} {manifest.Version} is already installed.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(stagingRoot, destination);
            return destination;
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }

            throw;
        }
    }

    public bool Remove(string packId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!IsIdentifier(packId))
        {
            throw new InvalidDataException("Adapter-pack ID must contain only lower-case letters, digits, dots, and hyphens.");
        }

        // GetContainedPath re-validates each segment and rejects any path that
        // escapes the installation root, so a crafted version string cannot
        // traverse outside the managed pack directory.
        string installed = GetContainedPath(_installRoot, packId, version);
        if (!Directory.Exists(installed))
        {
            return false;
        }

        Directory.Delete(installed, recursive: true);

        // Remove the now-empty per-pack parent directory, but never the install root itself.
        string parent = Path.GetDirectoryName(installed)!;
        string rootPath = Path.GetFullPath(_installRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(parent, rootPath, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(parent)
            && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }

        return true;
    }

    private AdapterPackInspection Inspect(byte[] package)
    {
        List<string> errors = [];
        List<string> warnings = [];
        string packageHash = Convert.ToHexStringLower(SHA256.HashData(package));
        bool developmentTrust = _developmentPackageHashes.Contains(packageHash);
        AdapterPackManifestV1? manifest = null;
        bool signatureValid = false;

        try
        {
            using MemoryStream stream = new(package, writable: false);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is 0 or > MaximumEntries)
            {
                errors.Add($"Adapter pack must contain 1-{MaximumEntries} entries.");
            }

            Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string normalised = NormaliseEntryPath(entry.FullName);
                if (!entries.TryAdd(normalised, entry))
                {
                    errors.Add($"Duplicate archive entry '{normalised}'.");
                }

                if (entry.Length < 0 || entry.Length > MaximumExpandedBytes)
                {
                    errors.Add($"Archive entry '{normalised}' exceeds the expanded-size limit.");
                }

                expandedBytes += entry.Length;
                if (expandedBytes > MaximumExpandedBytes)
                {
                    errors.Add("Adapter pack exceeds the total expanded-size limit.");
                    break;
                }
            }

            if (!entries.TryGetValue(ManifestName, out ZipArchiveEntry? manifestEntry))
            {
                errors.Add($"Missing {ManifestName}.");
            }
            else
            {
                byte[] manifestBytes = ReadEntry(manifestEntry, 1024 * 1024);
                manifest = JsonSerializer.Deserialize<AdapterPackManifestV1>(manifestBytes, JsonOptions);
                ValidateManifest(manifest, entries, errors);

                if (manifest is not null
                    && entries.TryGetValue(SignatureName, out ZipArchiveEntry? signatureEntry)
                    && _publisherKeys.TryGetValue(manifest.PublisherKeyId, out byte[]? publicKey))
                {
                    byte[] signature = DecodeSignature(ReadEntry(signatureEntry, 4096));
                    if (publicKey.Length == 32 && signature.Length == 64)
                    {
                        PublicKey importedKey = PublicKey.Import(
                            SignatureAlgorithm.Ed25519,
                            publicKey,
                            KeyBlobFormat.RawPublicKey);
                        signatureValid = SignatureAlgorithm.Ed25519.Verify(importedKey, manifestBytes, signature);
                    }
                    if (!signatureValid)
                    {
                        errors.Add("Adapter-pack signature is invalid.");
                    }
                }
                else if (manifest is not null)
                {
                    warnings.Add("No trusted publisher key matched this adapter pack.");
                }
            }

            if (manifest is not null)
            {
                ValidatePayloads(manifest, entries, errors);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or ArgumentException)
        {
            errors.Add(exception.Message);
        }

        if (developmentTrust && !signatureValid)
        {
            warnings.Add("Adapter pack is trusted only by an explicit development hash; firmware updates must remain disabled.");
        }

        if (!signatureValid && !developmentTrust)
        {
            errors.Add("Adapter pack is neither publisher-signed nor explicitly allowlisted for development.");
        }

        return new AdapterPackInspection(
            manifest,
            packageHash,
            errors.Count == 0 && (signatureValid || developmentTrust),
            signatureValid,
            developmentTrust,
            errors.Distinct(StringComparer.Ordinal).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateManifest(
        AdapterPackManifestV1? manifest,
        Dictionary<string, ZipArchiveEntry> entries,
        List<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Adapter-pack manifest is empty.");
            return;
        }

        if (manifest.SchemaVersion != AdapterPackManifestV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported adapter-pack schema {manifest.SchemaVersion}.");
        }

        if (!IsIdentifier(manifest.Id))
        {
            errors.Add("Adapter-pack ID must contain only lower-case letters, digits, dots, and hyphens.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.Publisher)
            || string.IsNullOrWhiteSpace(manifest.PublisherKeyId)
            || string.IsNullOrWhiteSpace(manifest.Licence))
        {
            errors.Add("Adapter-pack identity and licence fields are required.");
        }

        if (manifest.MinimumProtocolVersion > ProtocolConstants.Version
            || manifest.MaximumProtocolVersion < ProtocolConstants.Version
            || manifest.MinimumProtocolVersion <= 0
            || manifest.MaximumProtocolVersion < manifest.MinimumProtocolVersion)
        {
            errors.Add($"Adapter pack does not support protocol {ProtocolConstants.Version}.");
        }

        string entryPoint = NormaliseEntryPath(manifest.EntryPoint);
        if (!entries.ContainsKey(entryPoint))
        {
            errors.Add($"Adapter-pack entry point '{entryPoint}' is missing.");
        }

        if (manifest.SupportedHardwareIds.Count == 0)
        {
            errors.Add("Adapter pack must declare at least one exact or wildcard hardware ID.");
        }

        if (manifest.Permissions == AdapterPackAccess.None)
        {
            errors.Add("Adapter pack must declare its required permissions.");
        }
    }

    private static void ValidatePayloads(
        AdapterPackManifestV1 manifest,
        Dictionary<string, ZipArchiveEntry> entries,
        List<string> errors)
    {
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string expectedHash) in manifest.PayloadHashes)
        {
            string normalised = NormaliseEntryPath(path);
            declared.Add(normalised);
            if (normalised is ManifestName or SignatureName)
            {
                errors.Add($"'{normalised}' cannot be listed as a payload.");
                continue;
            }

            if (!entries.TryGetValue(normalised, out ZipArchiveEntry? entry))
            {
                errors.Add($"Declared payload '{normalised}' is missing.");
                continue;
            }

            if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            {
                errors.Add($"Payload '{normalised}' has an invalid SHA-256 value.");
                continue;
            }

            byte[] payload = ReadEntry(entry, MaximumExpandedBytes);
            string actual = Convert.ToHexStringLower(SHA256.HashData(payload));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actual),
                    Encoding.ASCII.GetBytes(expectedHash.ToLowerInvariant())))
            {
                errors.Add($"Payload hash mismatch for '{normalised}'.");
            }
        }

        foreach (string entry in entries.Keys.Where(path =>
                     path is not ManifestName and not SignatureName
                     && !path.EndsWith('/')))
        {
            if (!declared.Contains(entry))
            {
                errors.Add($"Undeclared payload '{entry}'.");
            }
        }
    }

    private static byte[] DecodeSignature(byte[] encoded)
    {
        if (encoded.Length == 64)
        {
            return encoded;
        }

        string text = Encoding.ASCII.GetString(encoded).Trim();
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("signature.ed25519 must contain a raw or base64 Ed25519 signature.", exception);
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long maximumBytes)
    {
        if (entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"Archive entry '{entry.FullName}' exceeds its size limit.");
        }

        using Stream input = entry.Open();
        using MemoryStream output = new((int)Math.Min(entry.Length, int.MaxValue));
        input.CopyTo(output);
        if (output.Length > maximumBytes)
        {
            throw new InvalidDataException($"Archive entry '{entry.FullName}' expanded beyond its size limit.");
        }

        return output.ToArray();
    }

    private static async Task<byte[]> ReadPackageAsync(string packagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        string fullPath = Path.GetFullPath(packagePath);
        FileInfo file = new(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Adapter pack does not exist.", fullPath);
        }

        if (!string.Equals(file.Extension, ".pcha", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Adapter packs must use the .pcha extension.");
        }

        if (file.Length is <= 0 or > MaximumArchiveBytes)
        {
            throw new InvalidDataException($"Adapter pack must be between 1 byte and {MaximumArchiveBytes} bytes.");
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    private static string NormaliseEntryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalised = path.Replace('\\', '/');
        if (normalised.StartsWith('/')
            || Path.IsPathRooted(normalised)
            || normalised.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe adapter-pack path '{path}'.");
        }

        return normalised;
    }

    private static string GetContainedPath(string root, params string[] segments)
    {
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string combined = rootPath;
        foreach (string segment in segments)
        {
            combined = Path.Combine(combined, segment);
        }

        string result = Path.GetFullPath(combined);
        if (!result.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Adapter-pack path escapes the installation root.");
        }

        return result;
    }

    private static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value)
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '-');

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
