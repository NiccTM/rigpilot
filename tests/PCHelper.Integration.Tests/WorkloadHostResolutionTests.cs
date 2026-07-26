using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the workload-host launchability check. Auto OC V3 could not run on any
/// deployed payload because the dashboard directory receives a stray
/// PCHelper.WorkloadHost.exe from a project reference *without* the managed
/// assembly it launches; testing the apphost alone accepted that unusable copy
/// and skipped the real published component.
/// </summary>
public sealed class WorkloadHostResolutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"rigpilot-workload-{Guid.NewGuid():N}");

    public WorkloadHostResolutionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void AnApphostWithoutItsManagedAssemblyIsNotLaunchable()
    {
        // Exactly the deployed app/ directory: apphost present, .dll absent.
        string executable = Path.Combine(_directory, "PCHelper.WorkloadHost.exe");
        File.WriteAllText(executable, "apphost");

        Assert.False(WorkloadHostSession.IsLaunchable(executable));
    }

    [Fact]
    public void AnApphostBesideItsManagedAssemblyIsLaunchable()
    {
        string executable = Path.Combine(_directory, "PCHelper.WorkloadHost.exe");
        File.WriteAllText(executable, "apphost");
        File.WriteAllText(Path.ChangeExtension(executable, ".dll"), "assembly");

        Assert.True(WorkloadHostSession.IsLaunchable(executable));
    }

    [Fact]
    public void AMissingApphostIsNotLaunchable()
    {
        Assert.False(WorkloadHostSession.IsLaunchable(
            Path.Combine(_directory, "PCHelper.WorkloadHost.exe")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}
