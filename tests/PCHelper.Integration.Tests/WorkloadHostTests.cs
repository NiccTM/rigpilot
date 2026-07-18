using System.Diagnostics;
using System.Security.Cryptography;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Integration.Tests;

public sealed class WorkloadHostTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task AuthenticatedHostRunsAndFencesExactGpuWork()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PCHELPER_LIVE_WORKLOAD_HOST_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string host = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PCHelper.WorkloadHost", "bin", "Release", "net10.0-windows10.0.19041.0",
            "PCHelper.WorkloadHost.exe"));
        Assert.True(File.Exists(host), $"Workload Host build output is missing: {host}");
        string sessionId = Guid.NewGuid().ToString("N");
        string pipeName = $"pchelper.workload.{sessionId}";
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ProcessStartInfo start = new(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(host)!
        };
        foreach (string argument in new[]
        {
            "--session", sessionId,
            "--pipe", pipeName,
            "--token", token,
            "--vendor", "10DE",
            "--adapter-index", "0",
            "--parent", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Workload Host did not start.");
        WorkloadHostDescriptorV1 descriptor = new(
            WorkloadHostDescriptorV1.CurrentSchemaVersion,
            sessionId,
            pipeName,
            token,
            "nvidia:gpu-0",
            0x10DE,
            0,
            process.Id);
        WorkloadHostClient client = new(descriptor);
        try
        {
            WorkloadHostStatusV1 ready = await AwaitReadyAsync(client, process);
            Assert.True(ready.Ready);
            Assert.Equal(1, ready.MatchingHardwareAdapterCount);
            Assert.NotEqual(0, ready.AdapterLuid);

            WorkloadHostStatusV1 started = await client.SendAsync(
                WorkloadHostCommand.SetMode,
                AutoOcWorkloadMode.Core,
                CancellationToken.None);
            Assert.True(started.Running);
            await Task.Delay(500);
            WorkloadHostStatusV1 active = await client.SendAsync(
                WorkloadHostCommand.Ping,
                AutoOcWorkloadMode.Stopped,
                CancellationToken.None);
            Assert.True(active.DispatchCount > 0);
            Assert.True(DateTimeOffset.UtcNow - active.HeartbeatAt < TimeSpan.FromSeconds(3));

            WorkloadHostStatusV1 stopped = await client.SendAsync(
                WorkloadHostCommand.Stop,
                AutoOcWorkloadMode.Stopped,
                CancellationToken.None);
            Assert.False(stopped.Running);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<WorkloadHostStatusV1> AwaitReadyAsync(
        WorkloadHostClient client,
        Process process)
    {
        Exception? lastError = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                string error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Workload Host exited with code {process.ExitCode}: {error}");
            }

            try
            {
                return await client.SendAsync(
                    WorkloadHostCommand.Ping,
                    AutoOcWorkloadMode.Stopped,
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                lastError = exception;
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"Workload Host was not ready: {lastError?.Message}");
    }
}
