using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class WindowsTakeoverPlatformTests
{
    [Fact]
    public void StartupParserAcceptsOnlyDirectExactExecutableCommands()
    {
        string executable = Path.Combine(Path.GetTempPath(), "RigPilotTest", "Controller.exe");

        Assert.True(WindowsStartupCommandLine.MatchesExactExecutable(
            $"\"{executable}\" --minimized",
            executable));
        Assert.True(WindowsStartupCommandLine.MatchesExactExecutable(
            $"{executable} /silent",
            executable));
        Assert.False(WindowsStartupCommandLine.MatchesExactExecutable(
            $"cmd.exe /c \"{executable}\"",
            executable));
        Assert.False(WindowsStartupCommandLine.MatchesExactExecutable(
            $"\"{executable}.bak\"",
            executable));
    }

    [Fact]
    public void TakeoverGateRejectsAServiceImageThatCannotBeVerified()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "PCHelper.Service.dll");

        TakeoverExecutionStatusV1 status = new WindowsTakeoverExecutionGate(missing).GetStatus();

        Assert.False(status.CanExecute);
        Assert.Contains("cannot be verified", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedWindowsImageYieldsAReadableExactIdentity()
    {
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(notepad));

        TakeoverProcessIdentity? identity = await WindowsTakeoverProcessController.TryReadIdentityAsync(
            notepad,
            "Notepad",
            "notepad",
            ["Testing"],
            CancellationToken.None);

        Assert.NotNull(identity);
        Assert.NotEqual("Unsigned", identity.Publisher);
        Assert.False(string.IsNullOrWhiteSpace(identity.SignerThumbprint));
        Assert.Equal(64, identity.Sha256.Length);
    }
}
