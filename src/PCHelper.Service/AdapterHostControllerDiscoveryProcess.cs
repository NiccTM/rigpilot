using System.Diagnostics;
using System.Text;
using PCHelper.Core;

namespace PCHelper.Service;

/// <summary>
/// Production discovery child: the Adapter Host executable launched with a read-only
/// discovery argument (<c>--discover-controllers</c> or <c>--discover-hid</c>), contained
/// inside a kill-on-close job object so a native HidSharp crash cannot outlive or
/// destabilise the service. The exit contract is argument-agnostic (exit code + output).
/// </summary>
internal sealed class AdapterHostControllerDiscoveryProcess : IControllerDiscoveryProcess
{
    private readonly ChildProcessJob _job = new();
    private readonly StringBuilder _output = new();
    private readonly Process _process;
    private bool _disposed;

    public AdapterHostControllerDiscoveryProcess(params string[] arguments)
    {
        string executable = AdapterHostProxy.ResolveAdapterHostPath();
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments.Length == 0 ? ["--discover-controllers"] : arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = Process.Start(startInfo)
            ?? throw new ControllerDiscoveryProcessException("The discovery process could not be started.");
        _job.Add(_process);
        _process.OutputDataReceived += Capture;
        _process.ErrorDataReceived += Capture;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task<ControllerDiscoveryProcessExit> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree();
            throw new TimeoutException("Controller discovery did not exit within its time budget.");
        }

        // WaitForExitAsync returns before the async output pump is guaranteed to
        // have flushed; a bounded synchronous drain collects the trailing payload.
        _process.WaitForExit();

        string output;
        lock (_output)
        {
            output = _output.ToString();
        }

        return new ControllerDiscoveryProcessExit(_process.ExitCode, output);
    }

    private void Capture(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is null)
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(eventArgs.Data);
        }
    }

    private void KillProcessTree()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process may exit between the check and the kill; the job object
            // remains the backstop that guarantees termination.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        KillProcessTree();
        _process.Dispose();
        _job.Dispose();
        return ValueTask.CompletedTask;
    }
}
