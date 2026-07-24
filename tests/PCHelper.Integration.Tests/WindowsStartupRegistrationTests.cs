using PCHelper.App;

namespace PCHelper.Integration.Tests;

public sealed class WindowsStartupRegistrationTests
{
    [Fact]
    public void OfficialAppExecutableBuildsQuotedTrayCommand()
    {
        Assert.Equal(
            "\"C:\\Program Files\\RigPilot\\PCHelper.App.exe\" --tray",
            WindowsStartupRegistration.BuildCommand(
                @"C:\Program Files\RigPilot\PCHelper.App.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Temp\testhost.exe")]
    [InlineData(@"C:\Temp\PCHelper.App.dll")]
    public void NonProductHostsCannotBecomeStartupCommands(string? executablePath)
    {
        Assert.Null(WindowsStartupRegistration.BuildCommand(executablePath));
    }

    [Theory]
    [InlineData(
        "\"C:\\PROGRAM FILES\\RIGPILOT\\PCHELPER.APP.EXE\" --tray",
        "\"C:\\Program Files\\RigPilot\\PCHelper.App.exe\" --tray",
        true)]
    [InlineData(
        "  \"C:\\Program Files\\RigPilot\\PCHelper.App.exe\" --tray  ",
        "\"C:\\Program Files\\RigPilot\\PCHelper.App.exe\" --tray",
        true)]
    [InlineData(
        "\"C:\\Program Files\\RigPilot\\PCHelper.App.exe\"",
        "\"C:\\Program Files\\RigPilot\\PCHelper.App.exe\" --tray",
        false)]
    [InlineData(
        "\"C:\\Other\\PCHelper.App.exe\" --tray",
        "\"C:\\Program Files\\RigPilot\\PCHelper.App.exe\" --tray",
        false)]
    public void EnabledStateRequiresTheExactTrayCommand(
        string registered,
        string expected,
        bool enabled)
    {
        Assert.Equal(
            enabled,
            WindowsStartupRegistration.IsRegisteredCommand(registered, expected));
    }

    [Fact]
    public void PortableModeCannotEnableSignInStartup()
    {
        using MainViewModel viewModel = new() { IsPortableMode = true };

        viewModel.StartWithWindows = true;

        Assert.False(viewModel.CanStartWithWindows);
        Assert.False(viewModel.StartWithWindows);
    }
}
