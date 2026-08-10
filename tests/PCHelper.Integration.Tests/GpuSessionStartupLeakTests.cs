using System.Diagnostics;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// A helper start that never publishes its child must kill it before unwinding.
///
/// <para><see cref="GpuSessionHost"/> spawns the child, then polls up to 40 times for the
/// private pipe. That loop observes the cancellation token in two places, and a cancelled
/// start used to walk straight out of the method: <c>_process</c> was still null, so nothing
/// tracked the live child, the next send spawned another one, and the strays were collected
/// only when the host itself was disposed or the service exited. Each stray is an orphaned
/// NVAPI session - roughly 30 MB private - and for the fan family one that still owns the
/// cooler, which is the state this codebase has repeatedly had to reboot out of.</para>
///
/// <para>The stub is a plain <c>ping</c>: it stays alive, never opens the pipe, and touches
/// no hardware, so the real startup, cancellation, and cleanup paths run without an NVAPI
/// session. It is bounded rather than infinite so a regression cannot hang the suite.</para>
/// </summary>
public sealed class GpuSessionStartupLeakTests
{
    private static readonly ProcessStartInfo Stub = new("ping.exe")
    {
        // ~29 s: far longer than the test needs, short enough that a leaked child
        // cannot outlive the run.
        Arguments = "-n 30 127.0.0.1",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    /// <summary>
    /// Opens an independent handle to the child so the assertion survives the host
    /// disposing its own <see cref="Process"/> object during cleanup - which it does, and
    /// which would otherwise make <c>HasExited</c> throw rather than answer.
    /// </summary>
    private static Process StartObservableStub(List<Process> observers)
    {
        Process process = Process.Start(Stub)!;
        observers.Add(Process.GetProcessById(process.Id));
        return process;
    }

    [Fact]
    public async Task ACancelledStartDoesNotLeaveTheChildRunning()
    {
        List<Process> observers = [];
        using GpuSessionHost host = new(
            "--gpu-clock-session",
            "clock session leak probe",
            startProcess: _ => StartObservableStub(observers));

        using CancellationTokenSource cancellation = new();
        // Long enough for the handshake loop to make several failing attempts, so the
        // cancellation lands inside the loop rather than before the child is spawned.
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.SendAsync<GpuClockSessionRequest, GpuClockSessionResult>(
                IpcCommand.GpuClockSession,
                new GpuClockSessionRequest(GpuClockSessionOps.ReadState, GpuClockOffsetDomain.Core, 0, false),
                cancellation.Token));

        Process child = Assert.Single(observers);
        try
        {
            // TerminateAsync waits for exit, so this is settled rather than racy by the
            // time the cancellation has propagated out.
            Assert.True(
                child.HasExited,
                "A start that never published its child left it running; that is the orphaned-session leak.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            child.Dispose();
        }
    }

    /// <summary>
    /// The same discipline on the other non-publishing exit: the child that fails every
    /// handshake attempt is terminated when the loop gives up, not left for disposal.
    /// </summary>
    [Fact]
    public async Task AHelperThatNeverOpensItsPipeIsTerminatedWhenTheLoopGivesUp()
    {
        List<Process> observers = [];
        using GpuSessionHost host = new(
            "--gpu-clock-session",
            "clock session handshake probe",
            startProcess: _ => StartObservableStub(observers));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            host.SendAsync<GpuClockSessionRequest, GpuClockSessionResult>(
                IpcCommand.GpuClockSession,
                new GpuClockSessionRequest(GpuClockSessionOps.ReadState, GpuClockOffsetDomain.Core, 0, false),
                CancellationToken.None));

        Process child = Assert.Single(observers);
        try
        {
            Assert.True(child.HasExited, "The helper that never opened its pipe was left running.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            child.Dispose();
        }
    }
}
