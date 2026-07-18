using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.App;

internal sealed class WorkloadHostSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly WorkloadHostClient _client;
    private int _disposeState;

    private WorkloadHostSession(Process process, WorkloadHostDescriptorV1 descriptor)
    {
        _process = process;
        Descriptor = descriptor;
        _client = new WorkloadHostClient(descriptor);
    }

    public WorkloadHostDescriptorV1 Descriptor { get; }

    public static async Task<WorkloadHostSession> StartAsync(
        string targetDeviceId,
        CancellationToken cancellationToken)
    {
        string executable = ResolveExecutablePath();
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The signed-in-user Auto OC workload host is not installed.", executable);
        }

        string sessionId = Guid.NewGuid().ToString("N");
        string pipeName = $"pchelper.workload.{sessionId}";
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add(sessionId);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(token);
        startInfo.ArgumentList.Add("--vendor");
        startInfo.ArgumentList.Add("10DE");
        startInfo.ArgumentList.Add("--adapter-index");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Auto OC workload host could not be started.");
        WorkloadHostDescriptorV1 descriptor = new(
            WorkloadHostDescriptorV1.CurrentSchemaVersion,
            sessionId,
            pipeName,
            token,
            targetDeviceId,
            VendorId: 0x10DE,
            AdapterIndex: 0,
            process.Id);
        WorkloadHostSession session = new(process, descriptor);
        try
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            Exception? lastError = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    string error = await process.StandardError.ReadToEndAsync(cancellationToken);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? $"The Auto OC workload host exited with code {process.ExitCode}."
                            : error.Trim());
                }

                try
                {
                    WorkloadHostStatusV1 ready = await session._client
                        .SendAsync(WorkloadHostCommand.Ping, AutoOcWorkloadMode.Stopped, cancellationToken);
                    if (ready.Ready
                        && !ready.Running
                        && ready.Mode == AutoOcWorkloadMode.Stopped
                        && ready.MatchingHardwareAdapterCount == 1)
                    {
                        return session;
                    }
                }
                catch (Exception exception) when (exception is TimeoutException or IOException)
                {
                    lastError = exception;
                }

                await Task.Delay(250, cancellationToken);
            }

            throw new TimeoutException(
                lastError is null
                    ? "The Auto OC workload host did not become ready within 10 seconds."
                    : $"The Auto OC workload host did not become ready: {lastError.Message}");
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static string ResolveExecutablePath()
    {
        string colocated = Path.Combine(AppContext.BaseDirectory, "PCHelper.WorkloadHost.exe");
        if (File.Exists(colocated))
        {
            return colocated;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "workload-host",
            "PCHelper.WorkloadHost.exe"));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await _client.SendAsync(
                        WorkloadHostCommand.Stop,
                        AutoOcWorkloadMode.Stopped,
                        CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception)
                {
                    // Process-tree termination below is the bounded fallback.
                }

                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
