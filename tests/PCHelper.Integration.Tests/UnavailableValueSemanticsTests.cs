using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Integration.Tests;

/// <summary>
/// One rule, gathered in one place because it was broken four separate ways in a single
/// review: <b>an unavailable value must never be rendered as a plausible one.</b>
///
/// <para>Each of these was a real defect, and each survived a green suite because the wrong
/// answer looked like a legitimate reading:</para>
/// <list type="bullet">
/// <item>CPU denied for LocalSystem processes was recorded as <c>0</c>, so a 24-hour soak
/// reported 0.000% mean CPU - a missing measurement wearing the costume of an excellent
/// result.</item>
/// <item>An AdapterHost whose command line could not be read was labelled <c>general</c>,
/// which silently classified the service itself as an adapter host.</item>
/// <item>GPU clock sliders showed <c>0 MHz</c> - a real offset meaning stock - on a card
/// enforcing +20/+50, because the live read had not landed when they were built.</item>
/// <item>A device whose name was forty NUL characters rendered as a blank tree row, because
/// NUL is a control character and <c>IsNullOrWhiteSpace</c> answers false for it.</item>
/// </list>
///
/// <para>The shared shape: zero, empty, and "the default" are all legitimate VALUES, so none
/// of them can double as "no answer". Anything that can be unavailable must say so in a way
/// the next layer cannot mistake for data.</para>
/// </summary>
public sealed class UnavailableValueSemanticsTests
{
    /// <summary>
    /// GPU live state: an unavailable read seeds nothing, because 0 MHz is stock and stock is
    /// a measurement.
    /// </summary>
    [Fact]
    public void AnUnavailableGpuReadingIsNotSeededAsStock()
    {
        Assert.Null(MainViewModel.LiveGpuOcValue(
            new GpuOcLiveStateV1(false, null, null, null, "unavailable"), "gpuclock.core:"));
        Assert.Null(MainViewModel.LiveGpuOcValue(null, "gpuclock.core:"));

        // A domain the driver could not report is unavailable even when the read succeeded.
        Assert.Null(MainViewModel.LiveGpuOcValue(
            new GpuOcLiveStateV1(true, 20, null, 385_000, "ok"), "gpuclock.memory:"));

        // ...while a genuine zero IS an answer and must survive as one.
        Assert.Equal(0d, MainViewModel.LiveGpuOcValue(
            new GpuOcLiveStateV1(true, 0, 0, 385_000, "ok"), "gpuclock.core:"));
    }

    /// <summary>
    /// The slider layer repeats the rule: an unavailable state leaves existing values alone
    /// rather than overwriting them with zeros.
    /// </summary>
    [Fact]
    public void AnUnavailableGpuReadingDoesNotOverwriteDisplayedValues()
    {
        MainViewModel.GpuControlSlider core = new()
        {
            CapabilityId = "gpuclock.core:0",
            Name = "core",
            Minimum = -1000,
            Maximum = 1000,
            Value = 20
        };

        Assert.False(MainViewModel.ApplyLiveStateToSliders(
            [core], new GpuOcLiveStateV1(false, null, null, null, "unavailable")));
        Assert.Equal(20, core.Value);
    }

    /// <summary>
    /// Device identity: a label with no visible characters is not a label. Whitespace is the
    /// obvious case; control characters are the one that actually shipped.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("\0")]
    [InlineData("\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0")]
    public void ALabelWithNoVisibleCharactersIsTreatedAsAbsent(string reported)
    {
        Assert.Null(SensorTree.NormaliseLabel(reported));
    }

    /// <summary>And a real label survives, including one that is merely dirty.</summary>
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3090", "NVIDIA GeForce RTX 3090")]
    [InlineData("  Sabrent Rocket 4.0 1TB  ", "Sabrent Rocket 4.0 1TB")]
    [InlineData("\0Sabrent\0", "Sabrent")]
    public void AVisibleLabelSurvivesNormalisation(string reported, string expected)
    {
        Assert.Equal(expected, SensorTree.NormaliseLabel(reported));
    }

    /// <summary>
    /// The fallback chain must be TOTAL. Stopping one step short is what left a device
    /// labelled only by its sensor count when the identifier was blank too.
    /// </summary>
    [Fact]
    public void TheDeviceLabelChainAlwaysEndsSomewhereVisible()
    {
        IReadOnlyList<SensorTreeDevice> tree = SensorTree.Build(
            [new HardwareDevice("", "   ", DeviceKind.Unknown, null, null, null, new Dictionary<string, string>())],
            [new SensorSample("s", "adapter", "", "Temperature", DateTimeOffset.UnixEpoch, 41, "°C", SensorQuality.Good, TimeSpan.Zero)]);

        SensorTreeDevice device = Assert.Single(tree);
        Assert.Equal(SensorTree.UnnamedDeviceLabel, device.DeviceName);
        Assert.True(
            device.DeviceName.Any(character => !char.IsWhiteSpace(character) && !char.IsControl(character)),
            "A rendered label must contain something a person can see.");
    }

    /// <summary>
    /// The footprint tool's CPU and role fields follow the same rule, asserted here against
    /// the script text so the contract is visible in this one place rather than only in the
    /// tool's own tests. A denied read must not become 0, and an unreadable role must not
    /// become a real role.
    /// </summary>
    [Fact]
    public void TheFootprintToolDocumentsUnavailableAsDistinctFromZero()
    {
        string script = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "Measure-RuntimeFootprint.ps1")));

        // CPU: blank unless the read set is complete, and never a partial sum.
        Assert.Contains("CpuReadsSucceeded", script, StringComparison.Ordinal);
        Assert.Contains("$succeeded -eq $processes.Count", script, StringComparison.Ordinal);
        Assert.Contains("N/A", script, StringComparison.Ordinal);

        // Role: unknown when the command line cannot be read, never inferred.
        Assert.Contains("'unknown'", script, StringComparison.Ordinal);
        Assert.Contains("role=", script, StringComparison.Ordinal);

        // Schema version, so a parser never infers which fields to expect.
        Assert.Contains("SchemaVersion", script, StringComparison.Ordinal);
    }
}
