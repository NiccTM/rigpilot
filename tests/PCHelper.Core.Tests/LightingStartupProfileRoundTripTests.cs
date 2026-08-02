using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.Core.Tests;

/// <summary>
/// The saved lighting profile is written by one call and read back by another, in a
/// different process lifetime, and the only thing standing between them is that the two
/// agree on property names and the schema version.
///
/// <para>This exists because they did not. The writer was configured with
/// <see cref="JsonSerializerDefaults.Web"/>, which emits camelCase, while the reader used
/// default options, which are case-sensitive. Every save reported success and wrote a real
/// file; every read then saw <c>SchemaVersion = 0</c>, failed the version gate, and
/// returned null. The colour was never restored, and nothing anywhere said so. A round trip
/// is the cheapest possible guard against that whole class of failure.</para>
/// </summary>
public sealed class LightingStartupProfileRoundTripTests
{
    /// <summary>The exact options the service writes with.</summary>
    private static readonly JsonSerializerOptions WriterOptions = new() { WriteIndented = true };

    private static LightingStartupProfileV1 Sample => new(
        LightingStartupProfileV1.CurrentSchemaVersion,
        "7A2BFF",
        DateTimeOffset.UtcNow,
        ["native:aura", "native:razer"]);

    [Fact]
    public void SurvivesTheServiceWriteAndReadPath()
    {
        string json = JsonSerializer.Serialize(Sample, WriterOptions);

        // Deserialized exactly as the service does it: no options at all.
        LightingStartupProfileV1? read = JsonSerializer.Deserialize<LightingStartupProfileV1>(json);

        Assert.NotNull(read);
        Assert.Equal(LightingStartupProfileV1.CurrentSchemaVersion, read.SchemaVersion);
        Assert.Equal("7A2BFF", read.Colour);
        Assert.Equal(["native:aura", "native:razer"], read.RouteIds);
    }

    /// <summary>
    /// The reader gates on <c>SchemaVersion == CurrentSchemaVersion</c>. That check is only
    /// meaningful if the field actually survives the trip, which is the exact thing that
    /// was broken: a camelCase payload left it at zero and the gate rejected every profile.
    /// </summary>
    [Fact]
    public void CarriesASchemaVersionThatPassesTheServiceVersionGate()
    {
        string json = JsonSerializer.Serialize(Sample, WriterOptions);
        LightingStartupProfileV1? read = JsonSerializer.Deserialize<LightingStartupProfileV1>(json);

        Assert.True(read is { SchemaVersion: LightingStartupProfileV1.CurrentSchemaVersion });
    }

    /// <summary>A profile from a future schema must be refused, not read at the wrong shape.</summary>
    [Fact]
    public void AnUnknownSchemaVersionFailsTheVersionGate()
    {
        string json = JsonSerializer.Serialize(Sample with { SchemaVersion = 99 }, WriterOptions);
        LightingStartupProfileV1? read = JsonSerializer.Deserialize<LightingStartupProfileV1>(json);

        Assert.False(read is { SchemaVersion: LightingStartupProfileV1.CurrentSchemaVersion });
    }

    /// <summary>
    /// The colour is stored normalised, so what comes back off disk must still satisfy the
    /// policy that will re-drive it at boot.
    /// </summary>
    [Fact]
    public void TheStoredColourIsStillAcceptedByThePolicyOnTheWayBack()
    {
        string json = JsonSerializer.Serialize(Sample, WriterOptions);
        LightingStartupProfileV1 read = JsonSerializer.Deserialize<LightingStartupProfileV1>(json)!;

        Assert.Equal("7A2BFF", LightingStartupPolicy.NormaliseColour(read.Colour));
        Assert.Null(LightingStartupPolicy.ValidateEnable(read.Colour, read.RouteIds));
    }

    /// <summary>
    /// The same guard for the GPU overclock profile, which shares the write-then-read
    /// design and would fail the same way if its serializer were ever "tidied up".
    /// </summary>
    [Fact]
    public void TheGpuOverclockProfileSurvivesTheSameRoundTrip()
    {
        GpuOcStartupProfileV1 profile = new(
            GpuOcStartupProfileV1.CurrentSchemaVersion,
            "nvidia:gpu-0",
            "nvidia:gpu-0",
            DateTimeOffset.UtcNow,
            [new GpuOcStartupOutputV1("gpuclock.core:0", 49)]);

        string json = JsonSerializer.Serialize(profile, WriterOptions);
        GpuOcStartupProfileV1? read = JsonSerializer.Deserialize<GpuOcStartupProfileV1>(json);

        Assert.True(read is { SchemaVersion: GpuOcStartupProfileV1.CurrentSchemaVersion });
        Assert.Equal("nvidia:gpu-0", read!.DeviceId);
        Assert.Equal(49, Assert.Single(read.Outputs).Value);
    }
}
