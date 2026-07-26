<#
.SYNOPSIS
    Samples the RigPilot runtime's memory and CPU footprint over time.

.DESCRIPTION
    The release gate tracks combined working set against a < 300 MB beta / < 250 MB 1.0
    target and requires a 24-hour memory-growth soak. Both were previously measured by
    hand, which is why the figures in AI_CONTEXT could not be reproduced on demand. This
    script makes the measurement repeatable.

    The target used to be written four different ways across the docs and this script —
    200 MB here and in docs/feature-status.md, < 250 MB as the competitive goal in
    docs/beta-roadmap.md, and < 300 MB beta / < 250 MB 1.0 in the same file's ledger. The
    staged pair is now the single target everywhere. The retired 200 MB figure had no
    stated basis and was the one that made a 307 MB measurement read as a 188 MB miss
    rather than a 7 MB one.

    It is READ-ONLY: it reads process counters and writes a CSV. It never touches a
    capability, a profile, or the service state, so it is safe to leave running.

    The documented figure is a CLOSED-DASHBOARD measurement — the dashboard is a normal
    user-session WPF app and is not part of the resident service footprint. The dashboard
    is therefore excluded unless -IncludeDashboard is passed, and every summary states
    which of the two it measured.

.PARAMETER DurationMinutes
    Total sampling window. Use 1440 for the 24-hour soak.

.PARAMETER IntervalSeconds
    Seconds between samples. Keep it coarse for long soaks; the point is growth, not noise.

.PARAMETER OutputPath
    CSV destination. Defaults to a timestamped file under artifacts\footprint.

.PARAMETER IncludeDashboard
    Also count PCHelper.App. Off by default so results are comparable to the recorded
    closed-dashboard figures.

.EXAMPLE
    .\Measure-RuntimeFootprint.ps1 -DurationMinutes 10
    The short pass, comparable to the recorded 388-394 MB result.

.EXAMPLE
    .\Measure-RuntimeFootprint.ps1 -DurationMinutes 1440 -IntervalSeconds 300
    The open 24-hour memory-growth soak.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 10080)]
    [int]$DurationMinutes = 10,

    [ValidateRange(5, 3600)]
    [int]$IntervalSeconds = 30,

    [string]$OutputPath,

    [switch]$IncludeDashboard
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$serviceProcessNames = @("PCHelper.Service", "PCHelper.AdapterHost", "PCHelper.AutomationHost", "PCHelper.EffectHost")
$dashboardProcessNames = @("PCHelper.App", "PCHelper.WorkloadHost")
$processNames = if ($IncludeDashboard) { $serviceProcessNames + $dashboardProcessNames } else { $serviceProcessNames }

if (-not $OutputPath) {
    $stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
    $scope = if ($IncludeDashboard) { "with-dashboard" } else { "closed-dashboard" }
    $OutputPath = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\footprint\footprint-$scope-$stamp.csv"
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

function Get-FootprintSample {
    param([string[]]$Names)

    $processes = @(Get-Process -Name $Names -ErrorAction SilentlyContinue)
    $sample = [pscustomobject]@{
        TimestampUtc  = (Get-Date).ToUniversalTime().ToString("o")
        ProcessCount  = $processes.Count
        WorkingSetMB  = 0.0
        PrivateMB     = 0.0
        CpuSeconds    = 0.0
        Breakdown     = ""
    }

    if ($processes.Count -eq 0) {
        return $sample
    }

    $sample.WorkingSetMB = [math]::Round((($processes | Measure-Object -Property WorkingSet64 -Sum).Sum / 1MB), 1)
    $sample.PrivateMB = [math]::Round((($processes | Measure-Object -Property PrivateMemorySize64 -Sum).Sum / 1MB), 1)

    # TotalProcessorTime can be denied for a LocalSystem process when this script is not
    # elevated. That is a missing datum, not a failure: memory growth is the point of the
    # soak, so the run continues with CPU reported as zero rather than aborting overnight.
    $cpu = 0.0
    foreach ($process in $processes) {
        try { $cpu += $process.TotalProcessorTime.TotalSeconds } catch { }
    }
    $sample.CpuSeconds = [math]::Round($cpu, 2)

    $sample.Breakdown = (
        $processes |
            Sort-Object -Property WorkingSet64 -Descending |
            ForEach-Object { "$($_.Name)#$($_.Id)=$([math]::Round($_.WorkingSet64 / 1MB, 1))" }
    ) -join " "

    return $sample
}

$deadline = (Get-Date).AddMinutes($DurationMinutes)
$samples = [System.Collections.Generic.List[object]]::new()

Write-Host "Sampling $(if ($IncludeDashboard) { 'service + dashboard' } else { 'service only (closed-dashboard)' }) every $IntervalSeconds s until $($deadline.ToString('u'))."
Write-Host "Writing $OutputPath"

while ((Get-Date) -lt $deadline) {
    $sample = Get-FootprintSample -Names $processNames
    $samples.Add($sample)
    $sample | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Append -Encoding utf8

    $remaining = [int]($deadline - (Get-Date)).TotalSeconds
    if ($remaining -le 0) { break }
    Start-Sleep -Seconds ([math]::Min($IntervalSeconds, $remaining))
}

if ($samples.Count -eq 0) {
    throw "No samples were taken."
}

$first = $samples[0]
$last = $samples[$samples.Count - 1]
$peak = ($samples | Measure-Object -Property WorkingSetMB -Maximum).Maximum
$growthPercent = if ($first.WorkingSetMB -gt 0) {
    [math]::Round((($last.WorkingSetMB - $first.WorkingSetMB) / $first.WorkingSetMB) * 100, 2)
} else { 0 }

$elapsedSeconds = ([datetime]$last.TimestampUtc - [datetime]$first.TimestampUtc).TotalSeconds
$cpuPercent = if ($elapsedSeconds -gt 0) {
    [math]::Round((($last.CpuSeconds - $first.CpuSeconds) / $elapsedSeconds / [Environment]::ProcessorCount) * 100, 3)
} else { 0 }

[pscustomobject]@{
    Scope               = if ($IncludeDashboard) { "service + dashboard" } else { "service only (closed-dashboard)" }
    Samples             = $samples.Count
    DurationMinutes     = [math]::Round($elapsedSeconds / 60, 1)
    InitialWorkingSetMB = $first.WorkingSetMB
    FinalWorkingSetMB   = $last.WorkingSetMB
    PeakWorkingSetMB    = $peak
    GrowthPercent       = $growthPercent
    FinalPrivateMB      = $last.PrivateMB
    MeanCpuPercent      = $cpuPercent
    ProcessCount        = $last.ProcessCount
    FinalBreakdown      = $last.Breakdown
    CsvPath             = $OutputPath
}
