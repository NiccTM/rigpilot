using System.Diagnostics;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The footprint tool's evidence contract, pinned after the first soak that completed
/// exposed three defects in it.
///
/// <para>CPU unavailable was emitted as a measured zero, so a 24-hour run reported 0.000%
/// mean CPU when in truth <c>TotalProcessorTime</c> had been denied for every LocalSystem
/// process. There was no summary artifact at all - it was written to the success stream and
/// the caller was expected to redirect it, so two killed runs left 0-byte files and a third
/// left none, each beside a complete CSV. And per-process data carried working set only,
/// which made trims and refaults indistinguishable from retained allocation during the
/// analysis.</para>
///
/// <para>These run the real script briefly against whatever RigPilot processes exist, so
/// they pass on a machine with none. They assert the artifact contract, not the numbers.</para>
/// </summary>
public sealed class RuntimeFootprintToolTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "Measure-RuntimeFootprint.ps1"));

    private sealed record Run(string Csv, string Summary, string Output, int ExitCode);

    // One run shared by every assertion here. DurationMinutes is range-validated with a floor
    // of 1, so a run per test added five minutes to the suite for no extra coverage: all of
    // these inspect the same artifacts.
    private static readonly Lazy<Run> Shared = new(Execute, isThreadSafe: true);

    private static Run Execute()
    {
        string root = Path.Combine(Path.GetTempPath(), $"rigpilot-footprint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string csv = Path.Combine(root, "probe.csv");

        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath,
            "-DurationMinutes", "1", "-IntervalSeconds", "1", "-OutputPath", csv
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new Run(csv, Path.Combine(root, "probe.summary.txt"), output, process.ExitCode);
    }

    [Fact]
    public void TheScriptIsWhereTheTestsExpectIt() => Assert.True(File.Exists(ScriptPath), ScriptPath);

    /// <summary>
    /// The defect that mattered most: three runs produced no usable summary. A completed run
    /// must leave a non-empty summary file, and no temporary file behind it.
    /// </summary>
    [Fact]
    public void ACompletedRunWritesANonEmptySummaryAndLeavesNoTemporaryFile()
    {
        Run run = Shared.Value;

        Assert.True(File.Exists(run.Summary), $"No summary was written.{Environment.NewLine}{run.Output}");
        Assert.True(new FileInfo(run.Summary).Length > 0, "The summary file is empty, which is the exact failure this fixes.");
        Assert.False(File.Exists(run.Summary + ".tmp"), "The atomic-write temporary file was left behind.");
        Assert.True(File.Exists(run.Csv), "The CSV is missing.");
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>
    /// CPU unavailable must never render as a number. These tests run unelevated, so on a
    /// machine with LocalSystem RigPilot processes the reads are denied; on a machine with no
    /// RigPilot processes there is nothing to read. Either way the answer is N/A, never 0.
    /// </summary>
    [Fact]
    public void UnavailableCpuIsReportedAsNotApplicableAndNeverAsZero()
    {
        Run run = Shared.Value;
        string summary = File.ReadAllText(run.Summary);

        Assert.Contains("Mean CPU", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("0.000 %", summary, StringComparison.Ordinal);
        if (summary.Contains("N/A", StringComparison.Ordinal))
        {
            Assert.Contains("not 0% CPU", summary, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every sample records whether its CPU read set was complete, so a partial sum can never
    /// be mistaken for the suite's CPU.
    /// </summary>
    [Fact]
    public void EverySampleRecordsCpuReadAccounting()
    {
        Run run = Shared.Value;
        string[] lines = File.ReadAllLines(run.Csv);

        Assert.True(lines.Length >= 2, "Expected a header and at least one sample.");
        Assert.Contains("CpuReadsAttempted", lines[0], StringComparison.Ordinal);
        Assert.Contains("CpuReadsSucceeded", lines[0], StringComparison.Ordinal);
        Assert.Contains("CpuSeconds", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema must distinguish a residency change from a retained-allocation change, which
    /// per-process working set alone cannot do. Both metrics are recorded, keyed by PID, and
    /// the legacy working-set-only column is preserved so old parsers keep working.
    /// </summary>
    [Fact]
    public void EverySampleRecordsPerProcessWorkingSetAndPrivateCommitKeyedByPid()
    {
        Run run = Shared.Value;
        string[] lines = File.ReadAllLines(run.Csv);

        Assert.Contains("SchemaVersion", lines[0], StringComparison.Ordinal);
        Assert.Contains("ProcessBreakdown", lines[0], StringComparison.Ordinal);
        Assert.Contains("Breakdown", lines[0], StringComparison.Ordinal);

        string body = string.Join(Environment.NewLine, lines.Skip(1));
        if (body.Contains("PCHelper.", StringComparison.Ordinal))
        {
            // Only assert the record grammar when this machine actually had processes to record.
            Assert.Matches(@"[A-Za-z\.]+#\d+:role=[^;]+;ws=[-\d\.]+;private=[-\d\.]+", body);
        }
    }

    /// <summary>The summary names the run's completion state, so a partial run cannot read as whole.</summary>
    [Fact]
    public void TheSummaryStatesWhetherTheRunCompleted()
    {
        Run run = Shared.Value;
        string summary = File.ReadAllText(run.Summary);

        Assert.Contains("Run", summary, StringComparison.Ordinal);
        Assert.Contains("completed", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Samples", summary, StringComparison.Ordinal);
        Assert.Contains("Cadence", summary, StringComparison.Ordinal);
    }
}
