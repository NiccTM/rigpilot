using PCHelper.Adapters;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Covers the pure evaluation of <see cref="PawnIoRuntimeProbe"/> — the read-only presence
/// evidence that makes the AMD Zen feasibility card report the real PawnIO runtime state
/// instead of asserting it from CPU-name recognition alone. Detection never unlocks a write.
/// </summary>
public sealed class PawnIoRuntimeProbeTests
{
    [Fact]
    public void AvailableOnlyWhenLibraryPresentAndDriverRunning()
    {
        PawnIoRuntimeStatus status = PawnIoRuntimeProbe.Evaluate(
            libraryPath: @"C:\Program Files\PawnIO\PawnIOLib.dll",
            driverState: "Running");

        Assert.True(status.LibraryPresent);
        Assert.True(status.DriverRunning);
        Assert.True(status.Available);
        Assert.Equal("the signed PawnIO driver is present and running", status.Describe());
    }

    [Fact]
    public void LibraryPresentButDriverStoppedIsNotAvailable()
    {
        PawnIoRuntimeStatus status = PawnIoRuntimeProbe.Evaluate(
            libraryPath: @"C:\Program Files\PawnIO\PawnIOLib.dll",
            driverState: "Stopped");

        Assert.True(status.LibraryPresent);
        Assert.False(status.DriverRunning);
        Assert.False(status.Available);
        Assert.Contains("not running", status.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLibraryIsNotAvailableRegardlessOfDriverState()
    {
        PawnIoRuntimeStatus status = PawnIoRuntimeProbe.Evaluate(libraryPath: null, driverState: "Running");

        Assert.False(status.LibraryPresent);
        Assert.False(status.Available);
        Assert.Equal("the signed PawnIO runtime is not installed", status.Describe());
    }

    [Theory]
    [InlineData("Running", true)]
    [InlineData("running", true)]
    [InlineData("Stopped", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DriverStateComparisonIsCaseInsensitiveAndExact(string? driverState, bool expectedRunning)
    {
        PawnIoRuntimeStatus status = PawnIoRuntimeProbe.Evaluate(
            libraryPath: @"C:\PawnIO\PawnIOLib.dll",
            driverState: driverState);

        Assert.Equal(expectedRunning, status.DriverRunning);
    }
}
