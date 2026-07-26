using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// The anti-cheat classifier is what stops RigPilot from synthesizing input while a
/// protected game is up, so its matching rules are pinned: it recognises the documented
/// clients regardless of case or file extension, and it never fires on ordinary software.
/// </summary>
public sealed class AntiCheatDetectionTests
{
    [Theory]
    [InlineData("BEService.exe", "BEService")]
    [InlineData("beservice", "BEService")]
    [InlineData("EasyAntiCheat_EOS.exe", "EasyAntiCheat_EOS")]
    [InlineData("vgc", "vgc")]
    [InlineData("vgk.sys", "vgk")]
    [InlineData("GameMon.des", "GameMon")]
    [InlineData("MHYPROT2.SYS", "mhyprot2")]
    public void RecognisesDocumentedAntiCheatProcessesRegardlessOfCaseOrExtension(string running, string expected)
    {
        string? found = AntiCheatDetection.FindActiveProtection(["explorer", running, "chrome"]);

        Assert.Equal(expected, found);
    }

    [Fact]
    public void ReturnsNullWhenNoAntiCheatIsRunning()
    {
        Assert.Null(AntiCheatDetection.FindActiveProtection(
            ["explorer", "chrome", "PCHelper.App", "PCHelper.Service", "devenv", "code"]));
    }

    [Fact]
    public void DoesNotFalsePositiveOnASubstringOfAnAntiCheatName()
    {
        // "vgc" is Vanguard, but "vgcore" or "avgconsole" must not match — the rule is a
        // full-stem match, not a contains, so ordinary software is never blocked.
        Assert.Null(AntiCheatDetection.FindActiveProtection(["vgcore", "avgconsole", "servicehub"]));
    }

    [Fact]
    public void HandlesAnEmptyProcessListAndBlankNames()
    {
        Assert.Null(AntiCheatDetection.FindActiveProtection([]));
        Assert.Null(AntiCheatDetection.FindActiveProtection(["", "   ", ".exe"]));
    }
}
