using System.Diagnostics;
using System.Text.Json;

namespace PCHelper.Integration.Tests;

public sealed class UiAutomationSmokeTests
{
    [Fact]
    public async Task RepositoryOwnedUiSmokeVisitsEveryPageAndValidatesCriticalAutomationIds()
    {
        const string toolProject = @"tools\PCHelper.UiSnapshot";
        const string toolFramework = "net10.0-windows10.0.19041.0";
        const string toolExecutable = "PCHelper.UiSnapshot.exe";
        string repoRoot = RepositoryBuildOutput.FindRepositoryRoot();
        string? resolvedTool = RepositoryBuildOutput.TryResolveExecutable(toolProject, toolFramework, toolExecutable);
        Assert.True(
            resolvedTool is not null && File.Exists(resolvedTool),
            "Build the solution before running the UI smoke test; searched:"
                + Environment.NewLine
                + RepositoryBuildOutput.DescribeSearchedLocations(toolProject, toolFramework, toolExecutable));
        string tool = resolvedTool!;
        string reportPath = Path.Combine(Path.GetTempPath(), $"pchelper-ui-smoke-{Guid.NewGuid():N}.json");

        try
        {
            ProcessStartInfo startInfo = new(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repoRoot
            };
            startInfo.ArgumentList.Add("--smoke");
            startInfo.ArgumentList.Add(reportPath);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("UI smoke process did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            string standardOutput = await output;
            string standardError = await error;

            Assert.True(
                process.ExitCode == 0,
                $"UI smoke exited {process.ExitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
            Assert.True(File.Exists(reportPath));
            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            JsonElement root = report.RootElement;
            Assert.True(root.GetProperty("passed").GetBoolean());
            Assert.Equal(9, root.GetProperty("visitedPages").GetArrayLength());
            Assert.True(root.GetProperty("advancedToggleWorked").GetBoolean());
            Assert.True(root.GetProperty("simpleSurfaceWorked").GetBoolean());
            Assert.True(root.GetProperty("advancedSurfaceWorked").GetBoolean());
            Assert.Empty(root.GetProperty("duplicateAutomationIds").EnumerateArray());
            Assert.Empty(root.GetProperty("unnamedInteractiveControls").EnumerateArray());
            Assert.Empty(root.GetProperty("errors").EnumerateArray());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("featureReadiness").ValueKind);
            Assert.True(root.GetProperty("requiredAutomationIds").GetArrayLength() >= 20);
            JsonElement actionableControls = root.GetProperty("actionableHardwareControlIds");
            Assert.Equal(30, actionableControls.GetArrayLength());
            Assert.Contains(
                actionableControls.EnumerateArray(),
                item => string.Equals(item.GetString(), "Games.ApplyBundle", StringComparison.Ordinal));
            Assert.Equal(
                root.GetProperty("interactiveControlCount").GetInt32(),
                root.GetProperty("namedInteractiveControlCount").GetInt32());
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

}
