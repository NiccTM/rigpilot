using System.Diagnostics;
using System.Text.Json;

namespace PCHelper.Integration.Tests;

public sealed class UiAutomationSmokeTests
{
    [Fact]
    public async Task RepositoryOwnedUiSmokeVisitsEveryPageAndValidatesCriticalAutomationIds()
    {
        string repoRoot = FindRepositoryRoot();
        string colocatedTool = Path.Combine(AppContext.BaseDirectory, "PCHelper.UiSnapshot.exe");
        string defaultTool = Path.Combine(
            repoRoot,
            "tools",
            "PCHelper.UiSnapshot",
            "bin",
            "Release",
            "net10.0-windows10.0.19041.0",
            "PCHelper.UiSnapshot.exe");
        string tool = File.Exists(colocatedTool) ? colocatedTool : defaultTool;
        Assert.True(File.Exists(tool), $"Build the solution before running the UI smoke test: {tool}");
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
            Assert.Equal(29, root.GetProperty("actionableHardwareControlIds").GetArrayLength());
            Assert.Equal(
                root.GetProperty("interactiveControlCount").GetInt32(),
                root.GetProperty("namedInteractiveControlCount").GetInt32());
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PCHelper.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PC Helper repository root.");
    }
}
