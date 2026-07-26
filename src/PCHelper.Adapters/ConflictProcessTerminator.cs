using System.Diagnostics;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>One running process the terminator may consider. <paramref name="ModulePath"/> is
/// the executable path when it could be read (best-effort), used to match controllers that run
/// under a generic process name but a distinctive install path.</summary>
public sealed record RunningProcessInfo(int ProcessId, string ProcessName, string? ModulePath = null);

/// <summary>
/// Seam over process enumeration and termination so the terminator can be
/// unit-tested without touching real processes.
/// </summary>
public interface IProcessControl
{
    IReadOnlyList<RunningProcessInfo> List();

    /// <summary>Terminates the process; returns null on success or an error message.</summary>
    string? TryKill(int processId);
}

/// <summary>
/// Terminates the running processes of detected conflicting controllers so they
/// release the device handles that block RigPilot's own gated writes (the
/// "close blockers" action). Hard safety boundaries:
/// <list type="bullet">
/// <item>It only ever terminates a process whose name is on
/// <see cref="ConflictDetector"/>'s curated known-controller allowlist for a
/// requested conflict id — never an arbitrary caller-supplied name.</item>
/// <item>It requires explicit confirmation.</item>
/// <item>It never terminates a RigPilot process (self-protection).</item>
/// </list>
/// It takes over no hardware control and is deliberately separate from the
/// identity-verified hardware-takeover executor; it simply frees device
/// ownership, exactly as closing the app in Task Manager would.
/// </summary>
public sealed class ConflictProcessTerminator(IProcessControl processControl)
{
    private static readonly string[] SelfProcessPrefixes = ["PCHelper", "pchelper", "RigPilot"];

    public StopConflictingProcessesResultV1 Terminate(StopConflictingProcessesRequestV1 request)
    {
        if (request.SchemaVersion != StopConflictingProcessesRequestV1.CurrentSchemaVersion)
        {
            return StopConflictingProcessesResultV1.Empty("Unsupported close-blockers request schema.");
        }
        if (!request.Confirm)
        {
            return StopConflictingProcessesResultV1.Empty("Closing a competing controller requires explicit confirmation.");
        }

        // Resolve the requested conflict ids (or all known ids) to their curated
        // process names. Anything not on the allowlist is silently ignored — it
        // can never widen the set of processes eligible for termination.
        IReadOnlyList<string> conflictIds = request.ConflictIds.Count > 0
            ? request.ConflictIds
            : ConflictDetector.KnownControllerIds;
        HashSet<string> allowedNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowedPathHints = new(StringComparer.OrdinalIgnoreCase);
        foreach (string conflictId in conflictIds)
        {
            foreach (string name in ConflictDetector.ProcessNamesFor(conflictId))
            {
                allowedNames.Add(name);
            }
            foreach (string hint in ConflictDetector.PathHintsFor(conflictId))
            {
                allowedPathHints.Add(hint);
            }
        }

        if (allowedNames.Count == 0 && allowedPathHints.Count == 0)
        {
            return StopConflictingProcessesResultV1.Empty("No known competing controller matched the request.");
        }

        List<TerminatedProcessV1> results = [];
        foreach (RunningProcessInfo process in processControl.List())
        {
            if (SelfProcessPrefixes.Any(prefix => process.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // never terminate RigPilot's own processes
            }

            // A process is eligible if its name is on the curated allowlist, OR its executable
            // path contains a curated install-path hint — the latter reaches controllers like
            // NZXT CAM whose background service runs as a generic "service.exe". Both sets come
            // only from ConflictDetector's allowlist, never from caller-supplied values.
            bool byName = allowedNames.Contains(process.ProcessName);
            bool byPath = process.ModulePath is { Length: > 0 } modulePath
                && allowedPathHints.Any(hint => modulePath.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (!byName && !byPath)
            {
                continue; // only curated conflict processes are eligible
            }

            string? error = processControl.TryKill(process.ProcessId);
            results.Add(new TerminatedProcessV1(process.ProcessId, process.ProcessName, error is null, error));
        }

        int killed = results.Count(result => result.Terminated);
        string message = results.Count == 0
            ? "No matching competing controller was running."
            : $"Closed {killed} of {results.Count} competing controller process(es). RigPilot took over no hardware; relaunch the app to hand control back.";
        return new StopConflictingProcessesResultV1(StopConflictingProcessesResultV1.CurrentSchemaVersion, results, message);
    }
}

/// <summary>
/// Live process control. Enumeration is best-effort (processes that exit mid-scan
/// or refuse access are skipped); termination uses the whole process tree so a
/// helper-heavy controller (for example NZXT CAM) is fully cleared. The service
/// runs as LocalSystem, so it can terminate elevated controllers.
/// </summary>
public sealed class WindowsProcessControl : IProcessControl
{
    public IReadOnlyList<RunningProcessInfo> List()
    {
        List<RunningProcessInfo> running = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    // Best-effort executable path so a controller with a generic process name
                    // but a distinctive install path (e.g. NZXT CAM's service.exe) is matchable.
                    // Reading another process's module can fail (access denied, bitness
                    // mismatch, exit); fall back to name-only for that process.
                    string? modulePath = null;
                    try
                    {
                        modulePath = process.MainModule?.FileName;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                    }

                    running.Add(new RunningProcessInfo(process.Id, process.ProcessName, modulePath));
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and read; skip.
                }
            }
        }

        return running;
    }

    public string? TryKill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null; // already gone
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return exception.Message;
        }
    }
}
