using System.Diagnostics;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Retention for staged LocalAlpha runtimes.
///
/// <para>Every deployment stages a full payload plus a pre-start copy of state.db, so each
/// costs roughly half a gigabyte and nothing removed them: 145 runtimes / 63.3 GB before a
/// manual prune on 2026-07-26, and back to 6 / 2.8 GB by 2026-08-09. A manual prune is not
/// a policy. These tests pin the properties that make an automatic one safe - above all
/// that the ACTIVE runtime is never deleted and that an unresolvable active path deletes
/// nothing at all.</para>
///
/// <para>The script is driven with an explicit active-image path so the classification runs
/// against a synthetic tree; production resolves it from the running service instead.</para>
/// </summary>
public sealed class LocalAlphaRetentionTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "Invoke-LocalAlphaRetention.ps1"));

    [Fact]
    public void TheScriptIsPresentWhereTheTestsExpectIt() => Assert.True(File.Exists(ScriptPath), ScriptPath);

    [Fact]
    public void TheActiveRuntimeIsNeverDeletedAndSupersededOnesAre()
    {
        using TemporaryRoot root = new();
        root.AddRuntime("0.8.0-alpha-20260101-000000", deployed: true, deployedAt: "2026-01-01T00:00:00+00:00");
        root.AddRuntime("0.8.0-alpha-20260102-000000", deployed: true, deployedAt: "2026-01-02T00:00:00+00:00");
        root.AddRuntime("0.8.0-alpha-20260103-000000", deployed: true, deployedAt: "2026-01-03T00:00:00+00:00");
        root.AddRuntime("0.8.0-alpha-20260104-000000", deployed: true, deployedAt: "2026-01-04T00:00:00+00:00");
        // Deliberately NOT the newest: the active runtime is whatever the service points at.
        string active = root.ServiceExecutable("0.8.0-alpha-20260101-000000");

        string output = Run(root.Path, active, keepPrevious: 2, apply: true);

        Assert.True(Directory.Exists(Path.Combine(root.Path, "0.8.0-alpha-20260101-000000")), output);
        // Active + the two newest previous survive; the remaining one is pruned.
        Assert.True(Directory.Exists(Path.Combine(root.Path, "0.8.0-alpha-20260104-000000")), output);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "0.8.0-alpha-20260103-000000")), output);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "0.8.0-alpha-20260102-000000")), output);
    }

    [Fact]
    public void ADryRunDeletesNothing()
    {
        using TemporaryRoot root = new();
        root.AddRuntime("0.8.0-alpha-20260101-000000", deployed: true, deployedAt: "2026-01-01T00:00:00+00:00");
        root.AddRuntime("0.8.0-alpha-20260102-000000", deployed: true, deployedAt: "2026-01-02T00:00:00+00:00");
        root.AddRuntime("0.8.0-alpha-20260103-000000", deployed: true, deployedAt: "2026-01-03T00:00:00+00:00");
        root.AddRuntime("0.8.0-alpha-20260104-000000", deployed: true, deployedAt: "2026-01-04T00:00:00+00:00");

        string output = Run(root.Path, root.ServiceExecutable("0.8.0-alpha-20260104-000000"), keepPrevious: 1, apply: false);

        Assert.Equal(4, Directory.GetDirectories(root.Path).Length);
        Assert.Contains("DRY RUN", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An install that never completed was never a rollback target.</summary>
    [Fact]
    public void IncompleteDeploymentsArePrunedWhileUnclassifiableOnesAreKept()
    {
        using TemporaryRoot root = new();
        root.AddRuntime("active", deployed: true, deployedAt: "2026-01-04T00:00:00+00:00");
        root.AddRuntime("failed", deployed: false, deployedAt: "2026-01-03T00:00:00+00:00");
        root.AddRuntimeWithoutManifest("mystery");

        string output = Run(root.Path, root.ServiceExecutable("active"), keepPrevious: 0, apply: true);

        Assert.True(Directory.Exists(Path.Combine(root.Path, "active")), output);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "failed")), output);
        // No manifest means it cannot be classified, so it is retained rather than guessed at.
        Assert.True(Directory.Exists(Path.Combine(root.Path, "mystery")), output);
    }

    /// <summary>
    /// The property that matters most. A prune that cannot prove which runtime is live is
    /// exactly the prune that must not run.
    /// </summary>
    [Fact]
    public void AnActivePathOutsideTheRootDeletesNothing()
    {
        using TemporaryRoot root = new();
        root.AddRuntime("0.8.0-alpha-20260101-000000", deployed: true, deployedAt: "2026-01-01T00:00:00+00:00");
        root.AddRuntime("0.8.0-alpha-20260102-000000", deployed: true, deployedAt: "2026-01-02T00:00:00+00:00");

        string output = Run(
            root.Path,
            @"C:\Program Files\PC Helper\service\PCHelper.Service.exe",
            keepPrevious: 0,
            apply: true,
            expectFailure: true);

        Assert.Equal(2, Directory.GetDirectories(root.Path).Length);
        Assert.Contains("Refusing to prune", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyActivePathDeletesNothing()
    {
        using TemporaryRoot root = new();
        root.AddRuntime("only", deployed: true, deployedAt: "2026-01-01T00:00:00+00:00");

        // An explicitly blank override forces the live lookup; on a machine with no such
        // service that must refuse rather than fall back to a guess. Where the service does
        // exist its image path is outside this synthetic root, which refuses too.
        string output = Run(root.Path, string.Empty, keepPrevious: 0, apply: true, expectFailure: true);

        Assert.True(Directory.Exists(Path.Combine(root.Path, "only")), output);
        Assert.Contains("Refusing to prune", output, StringComparison.OrdinalIgnoreCase);
    }

    private static string Run(
        string root,
        string activeImage,
        int keepPrevious,
        bool apply,
        bool expectFailure = false)
    {
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(ScriptPath);
        startInfo.ArgumentList.Add("-DeploymentRoot");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("-KeepPreviousSuccessful");
        startInfo.ArgumentList.Add(Math.Max(1, keepPrevious).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(activeImage))
        {
            startInfo.ArgumentList.Add("-ActiveServiceImagePath");
            startInfo.ArgumentList.Add(activeImage);
        }

        if (apply)
        {
            startInfo.ArgumentList.Add("-Apply");
        }

        using Process process = Process.Start(startInfo)!;
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        string combined = standardOutput + standardError;
        if (!expectFailure)
        {
            Assert.True(process.ExitCode == 0, combined);
        }

        return combined;
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rigpilot-retention-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string ServiceExecutable(string runtime) =>
            System.IO.Path.Combine(Path, runtime, "payload", "service", "PCHelper.Service.exe");

        public void AddRuntime(string name, bool deployed, string deployedAt)
        {
            string service = System.IO.Path.GetDirectoryName(ServiceExecutable(name))!;
            Directory.CreateDirectory(service);
            File.WriteAllText(ServiceExecutable(name), "stub");
            File.WriteAllText(
                System.IO.Path.Combine(Path, name, "deployment.json"),
                $$"""{"schemaVersion":1,"deployed":{{(deployed ? "true" : "false")}},"deployedAt":"{{deployedAt}}"}""");
        }

        public void AddRuntimeWithoutManifest(string name)
        {
            string service = System.IO.Path.GetDirectoryName(ServiceExecutable(name))!;
            Directory.CreateDirectory(service);
            File.WriteAllText(ServiceExecutable(name), "stub");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
