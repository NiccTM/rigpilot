using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class ServiceStartupArgumentsTests
{
    [Fact]
    public void AbsoluteDataDirectoryIsNormalisedForAnIsolatedServiceRun()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RigPilot", Guid.NewGuid().ToString("N"));

        bool parsed = ServiceStartupArguments.TryParse(
            [ServiceStartupArguments.DataDirectoryArgument, directory],
            out ServiceStartupOptions options,
            out string? error);

        Assert.True(parsed, error);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(directory), options.DataDirectory);
    }

    [Theory]
    [InlineData()]
    [InlineData("relative-state")]
    public void MissingOrRelativeDataDirectoryIsRejected(params string[] values)
    {
        string[] args = [ServiceStartupArguments.DataDirectoryArgument, .. values];

        bool parsed = ServiceStartupArguments.TryParse(args, out _, out string? error);

        Assert.False(parsed);
        Assert.Contains("absolute directory path", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateDataDirectoryArgumentsAreRejected()
    {
        bool parsed = ServiceStartupArguments.TryParse(
            [
                ServiceStartupArguments.DataDirectoryArgument, "C:\\RigPilot\\one",
                ServiceStartupArguments.DataDirectoryArgument, "C:\\RigPilot\\two"
            ],
            out _,
            out string? error);

        Assert.False(parsed);
        Assert.Contains("only once", error, StringComparison.OrdinalIgnoreCase);
    }
}
