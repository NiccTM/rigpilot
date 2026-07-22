using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace PCHelper.App;

/// <summary>One RigPilot process's live resource use for the footprint card.</summary>
public sealed record ProcessFootprintDisplay(string Name, string Role, double RamMegabytes, double CpuPercent, bool CpuKnown)
{
    public string RamText => $"{RamMegabytes:0} MB";

    public string CpuText => CpuKnown ? $"{CpuPercent:0.0}%" : "—";
}

public sealed partial class MainViewModel
{
    // RigPilot's own processes, by base name, with the friendly role shown on the
    // footprint card. Base names match System.Diagnostics.Process.ProcessName
    // (no ".exe").
    private static readonly (string Process, string Role)[] FootprintProcessRoster =
    [
        ("PCHelper.App", "Dashboard"),
        ("PCHelper.Service", "Background service"),
        ("PCHelper.AdapterHost", "Adapter host"),
        ("PCHelper.EffectHost", "Effect host"),
        ("PCHelper.AutomationHost", "Automation host"),
        ("PCHelper.WorkloadHost", "Workload host"),
        ("PCHelper.GameBarWidget", "Game Bar widget"),
    ];

    // Per-process CPU-time samples so a percentage can be derived from the delta
    // between refresh ticks rather than reported as a meaningless cumulative total.
    private readonly Dictionary<int, (TimeSpan Cpu, long Timestamp)> _footprintCpuSamples = new();
    private readonly int _footprintProcessorCount = Math.Max(1, Environment.ProcessorCount);

    public ObservableCollection<ProcessFootprintDisplay> Footprint { get; } = new BatchedObservableCollection<ProcessFootprintDisplay>();

    private string _footprintSummary = "Measuring RigPilot's own footprint…";

    public string FootprintSummary
    {
        get => _footprintSummary;
        private set => Set(ref _footprintSummary, value);
    }

    /// <summary>
    /// Samples the working set and CPU time of every RigPilot process so the
    /// footprint card can show, honestly, exactly what the suite costs — the
    /// direct answer to the bloat that drives users off iCUE/CAM. RAM is the
    /// headline (it is always readable); CPU is best-effort because the elevated
    /// service may deny a medium-integrity query, in which case it shows "—"
    /// rather than a wrong zero.
    /// </summary>
    private void UpdateFootprint()
    {
        List<ProcessFootprintDisplay> rows = [];
        double totalRam = 0;
        double totalCpu = 0;
        bool anyCpuKnown = false;
        long now = Stopwatch.GetTimestamp();
        HashSet<int> live = [];

        foreach ((string processName, string role) in FootprintProcessRoster)
        {
            Process[] instances;
            try
            {
                instances = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            double ram = 0;
            double cpu = 0;
            bool found = false;
            bool cpuKnown = false;

            foreach (Process process in instances)
            {
                using (process)
                {
                    int id;
                    try
                    {
                        id = process.Id;
                        ram += process.WorkingSet64 / (1024.0 * 1024.0);
                        found = true;
                        live.Add(id);
                    }
                    catch
                    {
                        continue;
                    }

                    try
                    {
                        TimeSpan cpuTime = process.TotalProcessorTime;
                        if (_footprintCpuSamples.TryGetValue(id, out (TimeSpan Cpu, long Timestamp) previous))
                        {
                            double wallSeconds = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                            if (wallSeconds > 0.01)
                            {
                                double busySeconds = (cpuTime - previous.Cpu).TotalSeconds;
                                cpu += Math.Clamp(busySeconds / wallSeconds / _footprintProcessorCount * 100.0, 0, 100);
                                cpuKnown = true;
                            }
                        }

                        _footprintCpuSamples[id] = (cpuTime, now);
                    }
                    catch
                    {
                        // Elevated service can deny the CPU-time query; keep RAM.
                    }
                }
            }

            if (found)
            {
                rows.Add(new ProcessFootprintDisplay(processName.Replace("PCHelper.", string.Empty), role, ram, cpu, cpuKnown));
                totalRam += ram;
                if (cpuKnown)
                {
                    totalCpu += cpu;
                    anyCpuKnown = true;
                }
            }
        }

        // Forget CPU samples for processes that have exited so the map cannot grow
        // without bound across restarts.
        foreach (int id in _footprintCpuSamples.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _footprintCpuSamples.Remove(id);
        }

        Replace(Footprint, rows.OrderByDescending(row => row.RamMegabytes), row => row.Name, StringComparer.Ordinal);

        if (rows.Count == 0)
        {
            FootprintSummary = "No RigPilot processes are running.";
            return;
        }

        string cpuPart = anyCpuKnown ? $" · {totalCpu:0.0}% CPU" : string.Empty;
        FootprintSummary =
            $"{totalRam:0} MB RAM{cpuPart} across {rows.Count} local process{(rows.Count == 1 ? string.Empty : "es")} — no cloud account, no telemetry, no bundled monitor.";
    }
}
