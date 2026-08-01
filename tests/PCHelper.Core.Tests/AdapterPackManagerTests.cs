using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class AdapterPackManagerTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task SignedPackIsVerifiedAndInstalledWithoutPathEscape()
    {
        using Key key = Key.Create(SignatureAlgorithm.Ed25519);
        byte[] publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        using TemporaryDirectory temporary = new();
        string packagePath = CreatePack(temporary.Path, key, tamperPayload: false);
        AdapterPackManager manager = new(
            System.IO.Path.Combine(temporary.Path, "installed"),
            new Dictionary<string, byte[]> { ["test-key"] = publicKey });

        AdapterPackInspection inspection = await manager.InspectAsync(packagePath, CancellationToken.None);
        string installed = await manager.InstallAsync(packagePath, CancellationToken.None);

        Assert.True(inspection.Valid);
        Assert.True(inspection.SignatureValid);
        Assert.False(inspection.DevelopmentTrust);
        Assert.Equal("org.pchelper.test", inspection.Manifest!.Id);
        Assert.True(File.Exists(System.IO.Path.Combine(installed, "adapter.dll")));
        Assert.StartsWith(System.IO.Path.GetFullPath(temporary.Path), System.IO.Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedManifestDoesNotPermitTamperedPayload()
    {
        using Key key = Key.Create(SignatureAlgorithm.Ed25519);
        using TemporaryDirectory temporary = new();
        string packagePath = CreatePack(temporary.Path, key, tamperPayload: true);
        AdapterPackManager manager = new(
            System.IO.Path.Combine(temporary.Path, "installed"),
            new Dictionary<string, byte[]> { ["test-key"] = key.PublicKey.Export(KeyBlobFormat.RawPublicKey) });

        AdapterPackInspection inspection = await manager.InspectAsync(packagePath, CancellationToken.None);

        Assert.False(inspection.Valid);
        Assert.True(inspection.SignatureValid);
        Assert.Contains(inspection.Errors, error => error.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnsignedPackRequiresExactDevelopmentHashAndWarnsAboutFirmware()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = CreatePack(temporary.Path, signingKey: null, tamperPayload: false);
        byte[] package = await File.ReadAllBytesAsync(packagePath);
        string hash = Convert.ToHexStringLower(SHA256.HashData(package));
        AdapterPackManager untrusted = new(
            System.IO.Path.Combine(temporary.Path, "untrusted"),
            new Dictionary<string, byte[]>());
        AdapterPackManager allowlisted = new(
            System.IO.Path.Combine(temporary.Path, "trusted"),
            new Dictionary<string, byte[]>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hash });

        AdapterPackInspection rejected = await untrusted.InspectAsync(packagePath, CancellationToken.None);
        AdapterPackInspection accepted = await allowlisted.InspectAsync(packagePath, CancellationToken.None);

        Assert.False(rejected.Valid);
        Assert.True(accepted.Valid);
        Assert.True(accepted.DevelopmentTrust);
        Assert.Contains(accepted.Warnings, warning => warning.Contains("firmware", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstalledPackCanBeRemovedAndItsDirectoryCleanedUp()
    {
        using Key key = Key.Create(SignatureAlgorithm.Ed25519);
        using TemporaryDirectory temporary = new();
        string installRoot = System.IO.Path.Combine(temporary.Path, "installed");
        string packagePath = CreatePack(temporary.Path, key, tamperPayload: false);
        AdapterPackManager manager = new(
            installRoot,
            new Dictionary<string, byte[]> { ["test-key"] = key.PublicKey.Export(KeyBlobFormat.RawPublicKey) });

        string installed = await manager.InstallAsync(packagePath, CancellationToken.None);
        Assert.True(Directory.Exists(installed));

        bool removed = manager.Remove("org.pchelper.test", "1.0.0");

        Assert.True(removed);
        Assert.False(Directory.Exists(installed));
        // The now-empty per-pack parent directory is cleaned up, but the install root remains.
        Assert.False(Directory.Exists(System.IO.Path.Combine(installRoot, "org.pchelper.test")));
        Assert.True(Directory.Exists(installRoot));
    }

    [Fact]
    public void RemovingAPackThatIsNotInstalledReturnsFalse()
    {
        using TemporaryDirectory temporary = new();
        AdapterPackManager manager = new(
            System.IO.Path.Combine(temporary.Path, "installed"),
            new Dictionary<string, byte[]>());

        Assert.False(manager.Remove("org.pchelper.test", "9.9.9"));
    }

    [Fact]
    public void RemovingWithAnInvalidIdentityIsRejectedBeforeAnyDeletion()
    {
        using TemporaryDirectory temporary = new();
        AdapterPackManager manager = new(
            System.IO.Path.Combine(temporary.Path, "installed"),
            new Dictionary<string, byte[]>());

        Assert.Throws<InvalidDataException>(() => manager.Remove("../escape", "1.0.0"));
        Assert.Throws<InvalidDataException>(() => manager.Remove("org.pchelper.test", ".."));
    }

    /// <summary>
    /// Inspection runs inside the privileged service, before any signature is
    /// trusted, on a file path the caller chose — so a forged ZIP directory is an
    /// attacker-controlled input to it. The expanded-size gate reads the declared
    /// uncompressed size, which the archive controls; this pins down that
    /// understating it cannot smuggle a payload through. .NET stops the entry
    /// stream at the declared length, so the content that reaches the hash check
    /// is truncated and fails it: the pack is rejected and never extracted.
    /// </summary>
    [Fact]
    public async Task PayloadWithAForgedUncompressedSizeCannotBeValidatedOrInstalled()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = CreateUnderstatedSizePack(temporary.Path);
        string installRoot = System.IO.Path.Combine(temporary.Path, "installed");
        AdapterPackManager manager = new(installRoot, new Dictionary<string, byte[]>());

        AdapterPackInspection inspection = await manager.InspectAsync(packagePath, CancellationToken.None);

        Assert.False(inspection.Valid);
        Assert.Contains(
            inspection.Errors,
            error => error.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.InstallAsync(packagePath, CancellationToken.None));
        Assert.False(Directory.Exists(System.IO.Path.Combine(installRoot, "org.pchelper.test")));
    }

    /// <summary>
    /// Builds a pack whose payload really contains 1 MiB but whose ZIP directory
    /// claims a single byte. The payload hash is the hash of the real content, so
    /// nothing else can fail first and mask the size check.
    /// </summary>
    private static string CreateUnderstatedSizePack(string root)
    {
        byte[] payload = new byte[1024 * 1024];
        AdapterPackManifestV1 manifest = new(
            AdapterPackManifestV1.CurrentSchemaVersion,
            "org.pchelper.test",
            "Test adapter",
            "1.0.0",
            "PC Helper tests",
            "test-key",
            "GPL-3.0-only",
            ProtocolConstants.Version,
            ProtocolConstants.Version,
            "adapter.dll",
            ["PCI\\VEN_1234&DEV_5678"],
            AdapterPackAccess.Telemetry,
            new Dictionary<string, string>
            {
                ["adapter.dll"] = Convert.ToHexStringLower(SHA256.HashData(payload))
            });
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        byte[] archiveBytes;
        using (MemoryStream buffer = new())
        {
            using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, "manifest.json", manifestBytes);
                Write(archive, "adapter.dll", payload, CompressionLevel.SmallestSize);
            }

            archiveBytes = buffer.ToArray();
        }

        OverwriteCentralDirectoryUncompressedSize(archiveBytes, "adapter.dll", 1);
        string packagePath = System.IO.Path.Combine(root, $"{Guid.NewGuid():N}.pcha");
        File.WriteAllBytes(packagePath, archiveBytes);
        return packagePath;
    }

    /// <summary>
    /// Rewrites the uncompressed-size field of one central-directory record, which
    /// is what <see cref="ZipArchiveEntry.Length"/> reports.
    /// </summary>
    private static void OverwriteCentralDirectoryUncompressedSize(byte[] archive, string entryName, uint size)
    {
        ReadOnlySpan<byte> signature = [0x50, 0x4B, 0x01, 0x02];
        byte[] name = System.Text.Encoding.UTF8.GetBytes(entryName);
        for (int index = 0; index + 46 <= archive.Length; index++)
        {
            if (!archive.AsSpan(index, 4).SequenceEqual(signature))
            {
                continue;
            }

            int nameLength = BitConverter.ToUInt16(archive, index + 28);
            if (nameLength != name.Length
                || !archive.AsSpan(index + 46, nameLength).SequenceEqual(name))
            {
                continue;
            }

            BitConverter.GetBytes(size).CopyTo(archive, index + 24);
            return;
        }

        throw new InvalidOperationException($"Central-directory record for '{entryName}' was not found.");
    }

    private static string CreatePack(string root, Key? signingKey, bool tamperPayload)
    {
        byte[] declaredPayload = [1, 2, 3, 4];
        byte[] archivePayload = tamperPayload ? [1, 2, 3, 5] : declaredPayload;
        AdapterPackManifestV1 manifest = new(
            AdapterPackManifestV1.CurrentSchemaVersion,
            "org.pchelper.test",
            "Test adapter",
            "1.0.0",
            "PC Helper tests",
            "test-key",
            "GPL-3.0-only",
            ProtocolConstants.Version,
            ProtocolConstants.Version,
            "adapter.dll",
            ["PCI\\VEN_1234&DEV_5678"],
            AdapterPackAccess.Telemetry,
            new Dictionary<string, string>
            {
                ["adapter.dll"] = Convert.ToHexStringLower(SHA256.HashData(declaredPayload))
            });
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        string packagePath = System.IO.Path.Combine(root, $"{Guid.NewGuid():N}.pcha");
        using FileStream file = File.Create(packagePath);
        using ZipArchive archive = new(file, ZipArchiveMode.Create);
        Write(archive, "manifest.json", manifestBytes);
        Write(archive, "adapter.dll", archivePayload);
        if (signingKey is not null)
        {
            Write(archive, "signature.ed25519", SignatureAlgorithm.Ed25519.Sign(signingKey, manifestBytes));
        }
        return packagePath;
    }

    private static void Write(
        ZipArchive archive,
        string name,
        byte[] value,
        CompressionLevel compression = CompressionLevel.NoCompression)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, compression);
        using Stream output = entry.Open();
        output.Write(value);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pchelper-pack-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
