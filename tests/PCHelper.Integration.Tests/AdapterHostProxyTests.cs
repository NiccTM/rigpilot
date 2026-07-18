using System.Diagnostics;
using System.Reflection;
using PCHelper.Contracts;
using PCHelper.Ipc;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class AdapterHostProxyTests
{
    [Fact]
    public async Task TimedOutGenerationIsAtomicallyClearedAndTerminated()
    {
        await using AdapterHostProxy proxy = new();
        using Process sleeper = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30"
        }) ?? throw new InvalidOperationException("Test sleeper process did not start.");
        int processId = sleeper.Id;
        FieldInfo processField = typeof(AdapterHostProxy).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(AdapterHostProxy).FullName, "_process");
        processField.SetValue(proxy, sleeper);

        await proxy.RecycleHostAsync(sleeper);

        Assert.Null(processField.GetValue(proxy));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    [Fact]
    public async Task PrivateAdapterHostStartsProbesAndShutsDown()
    {
        await using AdapterHostProxy proxy = new();

        AdapterProbeResult probe = await proxy.ProbeAsync(CancellationToken.None);

        Assert.Equal("librehardwaremonitor", probe.Manifest.Id);
        Assert.Equal(AdapterExecutionContext.AdapterHost, proxy.Manifest.ExecutionContext);
        Assert.DoesNotContain(probe.Capabilities, capability => capability.AdapterId != proxy.Manifest.Id);

        AdapterHostDiagnosticsV1? diagnostics = await proxy.GetDiagnosticsAsync(CancellationToken.None);
        Assert.NotNull(diagnostics);
        Assert.Equal("PerRequestSessionToken", diagnostics.ClientAuthentication);
        Assert.Equal("SkippedFailClosedForTokenAuthenticatedPrivatePipe", diagnostics.ClientIdentityEvaluation);
        Assert.NotEqual(0, diagnostics.ProcessId);

        NamedPipeRequestClient bypassClient = new(proxy.PipeName, TimeSpan.FromSeconds(2));
        IpcResponse bypass = await bypassClient.SendAsync(
            NamedPipeRequestClient.CreateRequest(
                IpcCommand.AdapterReset,
                new AdapterHostEnvelope<AdapterResetRequest>("wrong-token", new AdapterResetRequest("any"))),
            CancellationToken.None);
        Assert.False(bypass.Success);
        Assert.Contains("token", bypass.Error, StringComparison.OrdinalIgnoreCase);
    }
}
