using System.Security.Cryptography;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class SuiteSafetyTests
{
    [Fact]
    public async Task ScriptTrustIsInvalidatedWhenFileContentsChange()
    {
        string script = Path.Combine(Path.GetTempPath(), $"pchelper-script-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(script, "Write-Output 'safe'");
            string hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(script)));
            ScriptActionV1 action = new(
                ScriptActionV1.CurrentSchemaVersion,
                "script.test",
                "Test script",
                Environment.ProcessPath!,
                script,
                string.Empty,
                hash,
                Trusted: true,
                TimeSpan.FromSeconds(30),
                RequestElevation: false);

            Assert.True((await ScriptActionValidator.ValidateFileAsync(action, CancellationToken.None)).IsValid);
            await File.AppendAllTextAsync(script, Environment.NewLine + "Write-Output 'changed'");
            SuiteValidationResult changed = await ScriptActionValidator.ValidateFileAsync(action, CancellationToken.None);

            Assert.False(changed.IsValid);
            Assert.Contains(changed.Errors, error => error.Contains("invalidated", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void MacroRejectsUnbalancedAndDuplicateInputEvents()
    {
        MacroV1 valid = new(
            MacroV1.CurrentSchemaVersion,
            "macro.valid",
            "Valid",
            [
                new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero),
                new MacroStepV1(MacroStepKind.KeyUp, 65, 0, 0, 0, TimeSpan.FromMilliseconds(10))
            ]);
        MacroV1 invalid = valid with
        {
            Steps =
            [
                new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero),
                new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero)
            ]
        };

        Assert.True(MacroValidator.Validate(valid).IsValid);
        Assert.False(MacroValidator.Validate(invalid).IsValid);
    }

    [Fact]
    public void EffectGraphRejectsCyclesAndUntrustedScriptReferences()
    {
        EffectGraphV1 graph = new(
            EffectGraphV1.CurrentSchemaVersion,
            "effect.test",
            "Test",
            [
                new EffectNodeV1("a", EffectNodeKind.Blend, ["b"], new Dictionary<string, double>(), new Dictionary<string, string>()),
                new EffectNodeV1("b", EffectNodeKind.Script, ["a"], new Dictionary<string, double>(), new Dictionary<string, string>())
            ],
            "a",
            60);

        SuiteValidationResult result = EffectGraphValidator.Validate(graph);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("trusted manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FirmwareUpdateRequiresExactDeviceRecoveryAndProductionTrust()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        UpdateCandidateV1 candidate = new(
            UpdateCandidateV1.CurrentSchemaVersion,
            "update.bios",
            UpdateKind.Bios,
            "board.asus-x570-e",
            "5031",
            "5101",
            new Uri("https://dlcdnets.asus.com/pub/ASUS/mb/file.zip"),
            hash,
            "ASUSTeK COMPUTER INC.",
            RequiresReboot: true,
            RequiresBitLockerSuspension: true,
            RecoveryMethod: "USB BIOS FlashBack");
        UpdatePlanV1 plan = new(
            UpdatePlanV1.CurrentSchemaVersion,
            "plan.bios",
            candidate,
            "C:\\ProgramData\\PCHelper\\Updates\\file.zip",
            [],
            ["USB BIOS FlashBack"],
            UserConfirmed: true);
        UpdateValidationContext safe = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dlcdnets.asus.com" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { candidate.DeviceId },
            hash,
            candidate.ExpectedPublisher,
            PackageSignatureValid: true,
            StablePower: true,
            BitLockerRecoveryKeyAvailable: true,
            DeveloperBuild: false);

        Assert.True(UpdatePlanValidator.Validate(plan, safe).IsValid);
        Assert.False(UpdatePlanValidator.Validate(plan, safe with { DeveloperBuild = true }).IsValid);
        Assert.False(UpdatePlanValidator.Validate(plan, safe with { ExactDeviceIds = new HashSet<string>() }).IsValid);
        Assert.True(UpdatePlanValidator.CanTransition(UpdateTransactionState.Applying, UpdateTransactionState.PendingReboot));
        Assert.False(UpdatePlanValidator.CanTransition(UpdateTransactionState.Planned, UpdateTransactionState.Completed));
    }
}
