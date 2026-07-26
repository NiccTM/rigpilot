using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>One RigPilot control domain and who currently owns it.</summary>
public sealed record HardwareOwnershipDisplay(string Domain, string Glyph, string Owner, string Detail, bool Contended);

public sealed partial class MainViewModel
{
    // RigPilot's writable control domains, each mapped to the competing-writer
    // resource families (see ConflictDetector) that would contend it. When a
    // *running* competing writer claims one of a domain's families, that app holds
    // the domain; otherwise RigPilot owns it outright. GpuFan sits in both cooling
    // and GPU tuning on purpose — a GPU-fan writer contends both.
    private static readonly (string Domain, string Glyph, string[] Families)[] OwnershipDomains =
    [
        ("Cooling & fans", "", ["MotherboardFan", "GpuFan", "Aio", "UsbFan", "CpuTuning"]),
        ("GPU tuning", "", ["GpuTuning", "GpuFan"]),
        ("RGB lighting", "", ["Lighting"]),
    ];

    public ObservableCollection<HardwareOwnershipDisplay> HardwareOwnership { get; } = new BatchedObservableCollection<HardwareOwnershipDisplay>();

    private string _ownershipSummary = "RigPilot owns every writable hardware domain.";

    public string OwnershipSummary
    {
        get => _ownershipSummary;
        private set => Set(ref _ownershipSummary, value);
    }

    /// <summary>
    /// Rebuilds the ownership map from the current running competing writers. This
    /// turns RigPilot's existing conflict detection into a single answer to the #1
    /// real-world pain — "which app owns my hardware right now" — that CAM, iCUE,
    /// and Armoury Crate silently fight over.
    /// </summary>
    private void RebuildHardwareOwnership()
    {
        ConflictDescriptor[] running = _snapshot?.Conflicts.Where(conflict => conflict.IsRunning).ToArray() ?? [];
        List<HardwareOwnershipDisplay> rows = [];
        int contendedCount = 0;

        foreach ((string domain, string glyph, string[] families) in OwnershipDomains)
        {
            string[] contenders = running
                .Where(conflict => conflict.ResourceFamilies.Any(family => families.Contains(family, StringComparer.OrdinalIgnoreCase)))
                .Select(conflict => conflict.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (contenders.Length == 0)
            {
                rows.Add(new HardwareOwnershipDisplay(
                    domain,
                    glyph,
                    "RigPilot",
                    "No competing writer — RigPilot has exclusive ownership.",
                    Contended: false));
            }
            else
            {
                contendedCount++;
                rows.Add(new HardwareOwnershipDisplay(
                    domain,
                    glyph,
                    string.Join(", ", contenders),
                    "Held by another app. Use Close blockers below to hand this domain to RigPilot.",
                    Contended: true));
            }
        }

        Replace(HardwareOwnership, rows, row => row.Domain, StringComparer.Ordinal);
        OwnershipSummary = contendedCount == 0
            ? "RigPilot owns every writable hardware domain — no competing controller is running."
            : $"{contendedCount} of {rows.Count} domain{(rows.Count == 1 ? string.Empty : "s")} held by another app. Close blockers below to reclaim.";
    }
}
