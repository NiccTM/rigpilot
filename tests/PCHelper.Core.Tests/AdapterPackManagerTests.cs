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

    private static void Write(ZipArchive archive, string name, byte[] value)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
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
