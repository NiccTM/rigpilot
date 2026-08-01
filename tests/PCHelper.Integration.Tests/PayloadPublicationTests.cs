using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Publishing must never destroy the last known-good payload.
///
/// <para>It used to clear the destination and then build into it. On any machine
/// where a RigPilot process was running out of that directory — the ordinary case
/// during development — the recursive delete removed part of the tree, hit the
/// locked file, and aborted, leaving a half-deleted payload that later
/// <c>-SkipPublish</c> installer builds could not use. The previous payload was
/// gone and no replacement had been built.</para>
///
/// <para>These tests drive the real <c>scripts\publish.ps1</c> and the promotion
/// helper it delegates to, holding a file open exactly as a running dashboard
/// does, and assert the previous payload survives byte for byte.</para>
/// </summary>
public sealed class PayloadPublicationTests : IDisposable
{
    // publish.ps1 refuses to write outside the repository, which is a guard worth
    // keeping, so the fixture lives under the ignored artifacts tree rather than in
    // the system temp directory.
    private readonly string _root = Path.Combine(
        RepositoryBuildOutput.FindRepositoryRoot(),
        "artifacts",
        $"test-publish-{Guid.NewGuid():N}");

    private static string RepositoryRoot => RepositoryBuildOutput.FindRepositoryRoot();

    private static string PublishScript => Path.Combine(RepositoryRoot, "scripts", "publish.ps1");

    private static string PromotionScript => Path.Combine(RepositoryRoot, "scripts", "PayloadPromotion.ps1");

    /// <summary>
    /// The destination is proven replaceable before anything is built, so a locked
    /// destination fails in seconds rather than after a multi-minute publish. That
    /// is what makes driving the real script from a test practical.
    /// </summary>
    [Fact]
    public void PublishRefusesALockedDestinationAndLeavesEveryExistingFileIntact()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);
        string lockedFile = Path.Combine(destination, "app", "PCHelper.App.dll");

        // Hold the file the way a loaded assembly does: readable by others, but not
        // deletable. This is the exact condition that broke the old script.
        using (FileStream _ = new(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ScriptResult result = RunPowerShell(
                $"& '{PublishScript}' -Version 0.8.0-beta.1 -OutputDirectory '{destination}'");

            Assert.False(result.ExitCode == 0, $"Publish should have failed.{Environment.NewLine}{result.Output}");
            Assert.Contains("cannot be replaced", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("has NOT been modified", result.Output, StringComparison.OrdinalIgnoreCase);
            // The message has to name the file, or the operator cannot act on it.
            Assert.Contains("PCHelper.App.dll", result.Output, StringComparison.OrdinalIgnoreCase);
        }

        AssertPayloadUnchanged(destination, before);
        AssertNoStagingResidue(_root);
    }

    /// <summary>
    /// The refusal must come before the build, not after it: a publish that spends
    /// minutes compiling and only then discovers it cannot promote has already
    /// wasted the work, and every second it runs is a second in which the old
    /// payload could be damaged.
    /// </summary>
    [Fact]
    public void PublishRefusesALockedDestinationWithoutBuildingAnything()
    {
        string destination = Path.Combine(_root, "publish");
        CreateExistingPayload(destination);
        string lockedFile = Path.Combine(destination, "app", "PCHelper.App.dll");

        using FileStream _ = new(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stopwatch stopwatch = Stopwatch.StartNew();
        ScriptResult result = RunPowerShell(
            $"& '{PublishScript}' -Version 0.8.0-beta.1 -OutputDirectory '{destination}'");
        stopwatch.Stop();

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Determining projects to restore", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"The lock check must precede the build; it took {stopwatch.Elapsed}.");
    }

    /// <summary>A clean, unlocked destination is replaced completely.</summary>
    [Fact]
    public void PromotionReplacesAnUnlockedDestinationCompletely()
    {
        string destination = Path.Combine(_root, "publish");
        CreateExistingPayload(destination);
        File.WriteAllText(Path.Combine(destination, "stale-only-in-old.txt"), "old");

        string staging = Path.Combine(_root, "staged");
        Directory.CreateDirectory(Path.Combine(staging, "app"));
        File.WriteAllText(Path.Combine(staging, "app", "PCHelper.App.dll"), "new-body");
        File.WriteAllText(Path.Combine(staging, "runtime-contract.json"), "{\"new\":true}");

        ScriptResult result = RunPowerShell(
            $". '{PromotionScript}'; Complete-PayloadPromotion -StagingRoot '{staging}' -DestinationRoot '{destination}'");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("new-body", File.ReadAllText(Path.Combine(destination, "app", "PCHelper.App.dll")));
        Assert.Equal("{\"new\":true}", File.ReadAllText(Path.Combine(destination, "runtime-contract.json")));
        // The old payload is gone, not merged into the new one.
        Assert.False(File.Exists(Path.Combine(destination, "stale-only-in-old.txt")));
        Assert.False(Directory.Exists(staging));
        AssertNoStagingResidue(_root);
    }

    /// <summary>
    /// A payload that fails staging validation must never become the published
    /// payload, and must not cost the operator the one they already had.
    /// </summary>
    [Fact]
    public void FailedStagingValidationPreservesTheDestinationAndRemovesStaging()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);

        ScriptResult result = RunPowerShell($@"
. '{PromotionScript}'
Invoke-PayloadReplacement -DestinationRoot '{destination}' `
    -BuildStaging {{ param($s) New-Item -ItemType Directory -Path (Join-Path $s 'app') -Force | Out-Null; Set-Content -LiteralPath (Join-Path $s 'app\PCHelper.App.dll') -Value 'candidate' }} `
    -ValidateStaging {{ param($s) throw 'runtime payload validation failed' }}
");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runtime payload validation failed", result.Output, StringComparison.OrdinalIgnoreCase);
        AssertPayloadUnchanged(destination, before);
        AssertNoStagingResidue(_root);
    }

    /// <summary>
    /// A build that throws part-way leaves an incomplete staging tree. It must be
    /// removed, and the destination must not have been touched.
    /// </summary>
    [Fact]
    public void FailedStagingBuildPreservesTheDestinationAndRemovesStaging()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);

        ScriptResult result = RunPowerShell($@"
. '{PromotionScript}'
Invoke-PayloadReplacement -DestinationRoot '{destination}' `
    -BuildStaging {{ param($s) Set-Content -LiteralPath (Join-Path $s 'partial.txt') -Value 'half'; throw 'dotnet publish failed' }} `
    -ValidateStaging {{ param($s) }}
");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dotnet publish failed", result.Output, StringComparison.OrdinalIgnoreCase);
        AssertPayloadUnchanged(destination, before);
        AssertNoStagingResidue(_root);
    }

    /// <summary>
    /// Publishing into a destination that does not exist yet is the first-run case
    /// and must work without a previous payload to move aside.
    /// </summary>
    [Fact]
    public void PromotionSucceedsWhenNoPreviousPayloadExists()
    {
        string destination = Path.Combine(_root, "publish");
        string staging = Path.Combine(_root, "staged");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "runtime-contract.json"), "{}");

        ScriptResult result = RunPowerShell(
            $". '{PromotionScript}'; Complete-PayloadPromotion -StagingRoot '{staging}' -DestinationRoot '{destination}'");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(File.Exists(Path.Combine(destination, "runtime-contract.json")));
        AssertNoStagingResidue(_root);
    }

    /// <summary>The lock probe must not report a quiescent payload as unreplaceable.</summary>
    [Fact]
    public void ReplaceabilityCheckPassesForAnIdlePayload()
    {
        string destination = Path.Combine(_root, "publish");
        CreateExistingPayload(destination);

        ScriptResult result = RunPowerShell(
            $". '{PromotionScript}'; Assert-PayloadDestinationReplaceable -DestinationRoot '{destination}'; Write-Output 'replaceable'");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("replaceable", result.Output, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Gap 2: the promotion could fail AND the restoration of the previous
    // payload could fail. The previous payload survives either way, but the
    // operator has to be told where it went.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Forces the exact three-step failure: rename-aside succeeds, promotion
    /// fails, restoration fails. PowerShell resolves functions before cmdlets, so
    /// a counting <c>Move-Item</c> shim in the caller's scope drives the sequence
    /// deterministically without any test seam in the production script.
    /// </summary>
    [Fact]
    public void FailedRestorationNamesTheRecoveryPathAndKeepsThePreviousPayload()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);

        ScriptResult result = RunPowerShell($@"
$script:moves = 0
function Move-Item {{
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination)
    $script:moves++
    if ($script:moves -eq 1) {{
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
        return
    }}
    throw ""simulated move failure #$script:moves""
}}
. '{PromotionScript}'
Invoke-PayloadReplacement -DestinationRoot '{destination}' `
    -BuildStaging {{ param($s) Set-Content -LiteralPath (Join-Path $s 'app.dll') -Value 'candidate' }} `
    -ValidateStaging {{ param($s) }}
");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("IS NOT BACK IN PLACE", result.Output, StringComparison.OrdinalIgnoreCase);
        // Both causes survive: the promotion failure and the restoration failure.
        Assert.Contains("simulated move failure #2", result.Output, StringComparison.Ordinal);
        Assert.Contains("simulated move failure #3", result.Output, StringComparison.Ordinal);
        // The message has to carry a command the operator can run, not just paths.
        Assert.Contains("Move-Item -LiteralPath", result.Output, StringComparison.Ordinal);

        // The previous payload is intact under its generated recovery name, and
        // the error must name that exact path — it is the only way back.
        string[] recovered = Directory
            .GetDirectories(_root, "publish.previous-*", SearchOption.TopDirectoryOnly);
        string recoveryPath = Assert.Single(recovered);
        AssertOutputNamesPath(result.Output, recoveryPath);
        AssertPayloadUnchanged(recoveryPath, before);

        // Staging is deliberately retained for diagnosis, and the message says so.
        Assert.Contains("left at", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(Directory.GetDirectories(_root, "*-staging-*", SearchOption.TopDirectoryOnly));
    }

    /// <summary>
    /// The check-to-rename race: the rename-aside succeeds, something recreates
    /// the destination, and promotion then fails. Restoration is deliberately not
    /// attempted — moving the previous payload onto an existing directory would
    /// nest it inside rather than put it back — so this is the one path where the
    /// operator is left with an intact payload under a name they have never seen.
    ///
    /// <para>This branch used to fall through to a bare rethrow: the payload
    /// survived, but the error named neither it nor the staged tree, and staging
    /// was deleted underneath the operator. Reachability is not the standard for
    /// publication code; recoverability is.</para>
    /// </summary>
    [Fact]
    public void PromotionFailureWithARecreatedDestinationStillNamesTheRecoveryPath()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);

        ScriptResult result = RunPowerShell($@"
$script:moves = 0
function Move-Item {{
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination)
    $script:moves++
    if ($script:moves -eq 1) {{
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
        # The race: the destination reappears before staging can be moved in.
        New-Item -ItemType Directory -Path $LiteralPath -Force | Out-Null
        return
    }}
    throw ""simulated promotion failure #$script:moves""
}}
. '{PromotionScript}'
Invoke-PayloadReplacement -DestinationRoot '{destination}' `
    -BuildStaging {{ param($s) Set-Content -LiteralPath (Join-Path $s 'app.dll') -Value 'candidate' }} `
    -ValidateStaging {{ param($s) }}
");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("IS NOT BACK IN PLACE", result.Output, StringComparison.OrdinalIgnoreCase);
        // The original failure is preserved, not swallowed by the recovery text.
        Assert.Contains("simulated promotion failure #2", result.Output, StringComparison.Ordinal);
        // Restoration was refused rather than attempted blindly, and says why.
        Assert.Contains("Restoration was not attempted", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("simulated promotion failure #3", result.Output, StringComparison.Ordinal);
        // A command the operator can actually run.
        Assert.Contains("Move-Item -LiteralPath", result.Output, StringComparison.Ordinal);

        string recoveryPath = Assert.Single(
            Directory.GetDirectories(_root, "publish.previous-*", SearchOption.TopDirectoryOnly));
        AssertOutputNamesPath(result.Output, recoveryPath);
        AssertOutputNamesPath(result.Output, destination);
        AssertPayloadUnchanged(recoveryPath, before);

        // Both trees are preserved until the operator recovers explicitly.
        string stagingPath = Assert.Single(
            Directory.GetDirectories(_root, "*-staging-*", SearchOption.TopDirectoryOnly));
        AssertOutputNamesPath(result.Output, stagingPath);
    }

    /// <summary>
    /// A promotion failure that IS restored must not report a recovery path: the
    /// previous payload is back at the destination, so naming a
    /// <c>.previous-*</c> directory would send the operator after a tree that no
    /// longer exists.
    /// </summary>
    [Fact]
    public void PromotionFailureThatRestoresCleanlyReportsOnlyTheOriginalFailure()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);

        ScriptResult result = RunPowerShell($@"
$script:moves = 0
function Move-Item {{
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination)
    $script:moves++
    if ($script:moves -eq 2) {{ throw ""simulated promotion failure"" }}
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}}
. '{PromotionScript}'
Invoke-PayloadReplacement -DestinationRoot '{destination}' `
    -BuildStaging {{ param($s) Set-Content -LiteralPath (Join-Path $s 'app.dll') -Value 'candidate' }} `
    -ValidateStaging {{ param($s) }}
");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("simulated promotion failure", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NOT BACK IN PLACE", result.Output, StringComparison.OrdinalIgnoreCase);
        // The previous payload is back where it belongs, and nothing is left over.
        AssertPayloadUnchanged(destination, before);
        AssertNoStagingResidue(_root);
    }

    // ---------------------------------------------------------------------
    // Gap 5: a reparse point inside any tree the publisher probes, hashes or
    // deletes could take the operation outside the publication directory.
    // ---------------------------------------------------------------------

    [Fact]
    public void JunctionInsideTheExistingDestinationIsRefused()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);
        string outside = CreateSentinelOutsideThePayload();
        CreateJunction(Path.Combine(destination, "linked"), outside);

        ScriptResult result = RunPowerShell(
            $". '{PromotionScript}'; Assert-PayloadDestinationReplaceable -DestinationRoot '{destination}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing has been modified", result.Output, StringComparison.OrdinalIgnoreCase);
        AssertSentinelIntact(outside);
        // Everything that was in the payload is still there.
        foreach (string relativePath in before.Keys)
        {
            Assert.True(File.Exists(Path.Combine(destination, relativePath)));
        }
    }

    [Fact]
    public void JunctionCreatedByTheBuildInsideStagingIsRefused()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);
        string outside = CreateSentinelOutsideThePayload();

        ScriptResult result = RunPowerShell($@"
. '{PromotionScript}'
Invoke-PayloadReplacement -DestinationRoot '{destination}' `
    -BuildStaging {{ param($s) Set-Content -LiteralPath (Join-Path $s 'app.dll') -Value 'candidate'; cmd /c mklink /J ""$s\linked"" ""{outside}"" | Out-Null }} `
    -ValidateStaging {{ param($s) }}
");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
        AssertSentinelIntact(outside);
        AssertPayloadUnchanged(destination, before);
        AssertNoStagingResidue(_root);
    }

    [SymbolicLinkFact]
    public void SymbolicLinkedFileInTheDestinationIsRefused()
    {
        string destination = Path.Combine(_root, "publish");
        CreateExistingPayload(destination);
        string outside = CreateSentinelOutsideThePayload();
        File.CreateSymbolicLink(
            Path.Combine(destination, "linked.txt"),
            Path.Combine(outside, "sentinel.txt"));

        ScriptResult result = RunPowerShell(
            $". '{PromotionScript}'; Assert-PayloadDestinationReplaceable -DestinationRoot '{destination}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
        AssertSentinelIntact(outside);
    }

    /// <summary>
    /// The destructive case. A junction inside the moved-aside tree must be
    /// unlinked, never followed: Remove-Item -Recurse would delete the sentinel
    /// files that live outside the publication directory entirely.
    /// </summary>
    [Fact]
    public void CleanupOfAPreviousPayloadUnlinksAJunctionInsteadOfDeletingItsTarget()
    {
        string outside = CreateSentinelOutsideThePayload();
        string previous = Path.Combine(_root, "publish.previous-simulated");
        Directory.CreateDirectory(Path.Combine(previous, "app"));
        File.WriteAllText(Path.Combine(previous, "app", "PCHelper.App.dll"), "old");
        CreateJunction(Path.Combine(previous, "linked"), outside);

        ScriptResult result = RunPowerShell(
            $". '{PromotionScript}'; Remove-PayloadTree -Path '{previous}'; Write-Output 'removed'");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("removed", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(previous), "The previous payload tree should be gone.");
        // The whole point: the junction target and its contents survive.
        AssertSentinelIntact(outside);
    }

    [Fact]
    public void CleanupOfStagingUnlinksAJunctionInsteadOfDeletingItsTarget()
    {
        string outside = CreateSentinelOutsideThePayload();
        string staging = Path.Combine(_root, "publish-staging-simulated");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "partial.txt"), "half");
        CreateJunction(Path.Combine(staging, "linked"), outside);

        ScriptResult result = RunPowerShell(
            $". '{PromotionScript}'; Remove-PayloadStagingDirectory -StagingRoot '{staging}'; Write-Output 'removed'");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(staging));
        AssertSentinelIntact(outside);
    }

    /// <summary>
    /// A link introduced after staging validation must still be caught, so the
    /// check is repeated immediately before the swap.
    /// </summary>
    [Fact]
    public void JunctionIntroducedAfterValidationIsCaughtAtThePromotionStep()
    {
        string destination = Path.Combine(_root, "publish");
        Dictionary<string, string> before = CreateExistingPayload(destination);
        string outside = CreateSentinelOutsideThePayload();

        // The validate block passes, then plants the link — the window between
        // validation and promotion.
        ScriptResult result = RunPowerShell($@"
. '{PromotionScript}'
Invoke-PayloadReplacement -DestinationRoot '{destination}' `
    -BuildStaging {{ param($s) Set-Content -LiteralPath (Join-Path $s 'app.dll') -Value 'candidate' }} `
    -ValidateStaging {{ param($s) cmd /c mklink /J ""$s\linked"" ""{outside}"" | Out-Null }}
");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("promotion step", result.Output, StringComparison.OrdinalIgnoreCase);
        AssertSentinelIntact(outside);
        AssertPayloadUnchanged(destination, before);
        AssertNoStagingResidue(_root);
    }

    /// <summary>
    /// The publish script signs and hashes by walking staging with
    /// <c>Get-ChildItem -Recurse</c>, which follows junctions and directory
    /// symbolic links. The staged tree must therefore be proven link-free before
    /// the first such walk, not merely before promotion — otherwise signtool and
    /// Get-FileHash could reach files outside the payload, and SHA256SUMS.txt
    /// would describe them as though they were part of it.
    ///
    /// <para>This is a source-ordering guard, not a behavioural one: it proves
    /// the validator is called before the traversals, which is the property that
    /// a future edit could silently reverse. The behavioural proof that a linked
    /// staging tree is rejected lives in
    /// <see cref="JunctionCreatedByTheBuildInsideStagingIsRefused"/>.</para>
    /// </summary>
    [Fact]
    public void PublishValidatesForReparsePointsBeforeItWalksTheStagedTree()
    {
        string script = File.ReadAllText(PublishScript);
        int buildBlock = script.IndexOf("$buildStaging = {", StringComparison.Ordinal);
        Assert.True(buildBlock >= 0, "The staged-build block was renamed; update this guard with it.");

        int firstAssert = script.IndexOf(
            "Assert-PayloadFreeOfReparsePoint",
            buildBlock,
            StringComparison.Ordinal);
        int firstRecursiveWalk = script.IndexOf(
            "Get-ChildItem -LiteralPath $stagingRoot -Recurse",
            buildBlock,
            StringComparison.Ordinal);

        Assert.True(firstAssert >= 0, "The staged build must assert the payload is free of reparse points.");
        Assert.True(firstRecursiveWalk >= 0, "The staged build no longer walks staging; update this guard.");
        Assert.True(
            firstAssert < firstRecursiveWalk,
            "The reparse-point check must run before the first recursive walk of staging, "
                + "or signing and hashing can follow a link out of the payload.");
    }

    /// <summary>A sentinel tree outside the publication directory, used to prove
    /// nothing reached beyond it.</summary>
    private string CreateSentinelOutsideThePayload()
    {
        string outside = Path.Combine(_root, "outside-the-payload");
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "must survive");
        File.WriteAllText(Path.Combine(outside, "nested", "deep-sentinel.txt"), "must also survive");
        return outside;
    }

    /// <summary>
    /// Asserts the output names a path, ignoring the console's line wrapping. The
    /// PowerShell host breaks long lines at the buffer width, so a path can be
    /// split across two lines even though the message contains it verbatim.
    /// Windows paths carry no whitespace here, so removing whitespace from both
    /// sides compares the path itself rather than how it was rendered.
    /// </summary>
    private static void AssertOutputNamesPath(string output, string path)
    {
        string flattened = new(output.Where(character => !char.IsWhiteSpace(character)).ToArray());
        string expected = new(path.Where(character => !char.IsWhiteSpace(character)).ToArray());
        Assert.True(
            flattened.Contains(expected, StringComparison.OrdinalIgnoreCase),
            $"The message must name {path}, but it did not.{Environment.NewLine}{output}");
    }

    private static void AssertSentinelIntact(string outside)
    {
        Assert.True(Directory.Exists(outside), $"The directory outside the payload was removed: {outside}");
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.Equal("must also survive", File.ReadAllText(Path.Combine(outside, "nested", "deep-sentinel.txt")));
    }

    private static void CreateJunction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("mklink did not start.");
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "The junction was not created.");
        Assert.True(
            new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint),
            "The created directory is not a reparse point.");
    }

    private static Dictionary<string, string> CreateExistingPayload(string destination)
    {
        Directory.CreateDirectory(Path.Combine(destination, "app"));
        Directory.CreateDirectory(Path.Combine(destination, "service"));
        File.WriteAllText(Path.Combine(destination, "app", "PCHelper.App.dll"), "previous-app-body");
        File.WriteAllText(Path.Combine(destination, "app", "e_sqlite3.dll"), "previous-native-body");
        File.WriteAllText(Path.Combine(destination, "service", "PCHelper.Service.dll"), "previous-service-body");
        File.WriteAllText(Path.Combine(destination, "runtime-contract.json"), "{\"productVersion\":\"0.8.0-beta.1\"}");
        File.WriteAllText(Path.Combine(destination, "SHA256SUMS.txt"), "previous-sums");
        return HashTree(destination);
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            hashes[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(stream));
        }

        return hashes;
    }

    private static void AssertPayloadUnchanged(string destination, Dictionary<string, string> before)
    {
        Assert.True(Directory.Exists(destination), "The previous payload directory was removed.");
        Dictionary<string, string> after = HashTree(destination);
        Assert.Equal(before.Count, after.Count);
        foreach ((string relativePath, string hash) in before)
        {
            Assert.True(after.ContainsKey(relativePath), $"Previous payload file disappeared: {relativePath}");
            Assert.Equal(hash, after[relativePath]);
        }
    }

    /// <summary>
    /// No staging or moved-aside directory may survive a failed publish. Staging
    /// and the moved-aside payload are always siblings of the destination, so this
    /// looks only at the top level — recursing would follow any junction a test
    /// planted and search outside the fixture.
    /// </summary>
    private static void AssertNoStagingResidue(string searchRoot)
    {
        string[] residue = Directory
            .EnumerateDirectories(searchRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(directory =>
            {
                string name = Path.GetFileName(directory);
                return name.Contains("-staging-", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(".previous-", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        Assert.True(residue.Length == 0, $"Leftover staging directories: {string.Join(", ", residue)}");
    }

    private static ScriptResult RunPowerShell(string script)
    {
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The publish script did not finish within 180 seconds.");
        }

        StringBuilder combined = new();
        combined.AppendLine(output.GetAwaiter().GetResult());
        combined.AppendLine(error.GetAwaiter().GetResult());
        return new ScriptResult(process.ExitCode, combined.ToString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ScriptResult(int ExitCode, string Output);
}
