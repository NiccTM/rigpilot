using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the "close blockers" terminator against a fake process control. No
/// real process is enumerated or killed. The guarantees under test are the hard
/// safety boundaries: allowlist-only, confirmation-required, self-protection.
/// </summary>
public sealed class ConflictProcessTerminatorTests
{
    [Fact]
    public void TerminatesOnlyAllowlistedConflictProcesses()
    {
        FakeProcessControl control = new(
            new RunningProcessInfo(10, "NZXT CAM"),
            new RunningProcessInfo(11, "MSIAfterburner"),
            new RunningProcessInfo(12, "notepad"),          // unrelated — must be left alone
            new RunningProcessInfo(13, "explorer"));
        ConflictProcessTerminator terminator = new(control);

        StopConflictingProcessesResultV1 result = terminator.Terminate(
            new StopConflictingProcessesRequestV1(StopConflictingProcessesRequestV1.CurrentSchemaVersion, [], Confirm: true));

        Assert.Equal([10, 11], control.Killed.Order());
        Assert.DoesNotContain(12, control.Killed);
        Assert.DoesNotContain(13, control.Killed);
        Assert.Equal(2, result.TerminatedCount);
    }

    [Fact]
    public void RefusesWithoutConfirmation()
    {
        FakeProcessControl control = new(new RunningProcessInfo(10, "NZXT CAM"));
        ConflictProcessTerminator terminator = new(control);

        StopConflictingProcessesResultV1 result = terminator.Terminate(
            new StopConflictingProcessesRequestV1(StopConflictingProcessesRequestV1.CurrentSchemaVersion, [], Confirm: false));

        Assert.Empty(control.Killed);
        Assert.Contains("confirmation", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NeverTerminatesRigPilotsOwnProcesses()
    {
        FakeProcessControl control = new(
            new RunningProcessInfo(1, "PCHelper.Service"),
            new RunningProcessInfo(2, "PCHelper.App"),
            new RunningProcessInfo(3, "RigPilot"));
        ConflictProcessTerminator terminator = new(control);

        StopConflictingProcessesResultV1 result = terminator.Terminate(
            new StopConflictingProcessesRequestV1(StopConflictingProcessesRequestV1.CurrentSchemaVersion, [], Confirm: true));

        Assert.Empty(control.Killed);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void HonoursASingleRequestedConflictIdOnly()
    {
        FakeProcessControl control = new(
            new RunningProcessInfo(10, "NZXT CAM"),
            new RunningProcessInfo(11, "FanControl"));
        ConflictProcessTerminator terminator = new(control);

        StopConflictingProcessesResultV1 result = terminator.Terminate(
            new StopConflictingProcessesRequestV1(StopConflictingProcessesRequestV1.CurrentSchemaVersion, ["fan-control"], Confirm: true));

        Assert.Equal([11], control.Killed);              // only Fan Control, not CAM
        Assert.Equal(1, result.TerminatedCount);
    }

    [Fact]
    public void TerminatesAControllerMatchedByInstallPathWhenItsProcessNameIsGeneric()
    {
        // NZXT CAM's background service runs as a generic "service.exe"; it is on the allowlist
        // only via its install-path hint. The close action must still terminate it, while an
        // unrelated service.exe on a different path is left alone (the hint is specific, not a
        // blanket "kill every service.exe").
        FakeProcessControl control = new(
            new RunningProcessInfo(20, "service", @"C:\Program Files\NZXT CAM\service.exe"),
            new RunningProcessInfo(21, "service", @"C:\Windows\System32\unrelated\service.exe"));
        ConflictProcessTerminator terminator = new(control);

        StopConflictingProcessesResultV1 result = terminator.Terminate(
            new StopConflictingProcessesRequestV1(StopConflictingProcessesRequestV1.CurrentSchemaVersion, ["nzxt-cam"], Confirm: true));

        Assert.Equal([20], control.Killed);
        Assert.DoesNotContain(21, control.Killed);
        Assert.Equal(1, result.TerminatedCount);
    }

    [Fact]
    public void ReportsAKillFailureWithoutClaimingSuccess()
    {
        FakeProcessControl control = new(new RunningProcessInfo(10, "NZXT CAM")) { KillError = "access denied" };
        ConflictProcessTerminator terminator = new(control);

        StopConflictingProcessesResultV1 result = terminator.Terminate(
            new StopConflictingProcessesRequestV1(StopConflictingProcessesRequestV1.CurrentSchemaVersion, [], Confirm: true));

        TerminatedProcessV1 entry = Assert.Single(result.Results);
        Assert.False(entry.Terminated);
        Assert.Equal("access denied", entry.Error);
        Assert.Equal(0, result.TerminatedCount);
    }

    [Fact]
    public void KnownControllersIncludeTheMajorVendorSuites()
    {
        // The allowlist must cover the suites the owner named (ASUS, AORUS, MSI).
        Assert.Contains("armoury-crate", ConflictDetector.KnownControllerIds);
        Assert.Contains("rgb-fusion", ConflictDetector.KnownControllerIds);
        Assert.Contains("mystic-light", ConflictDetector.KnownControllerIds);
        Assert.Contains("AorusEngine", ConflictDetector.ProcessNamesFor("rgb-fusion"));
        Assert.Empty(ConflictDetector.ProcessNamesFor("not-a-real-id"));
    }

    private sealed class FakeProcessControl(params RunningProcessInfo[] processes) : IProcessControl
    {
        public List<int> Killed { get; } = [];

        public string? KillError { get; init; }

        public IReadOnlyList<RunningProcessInfo> List() => processes;

        public string? TryKill(int processId)
        {
            if (KillError is not null)
            {
                return KillError;
            }

            Killed.Add(processId);
            return null;
        }
    }
}
