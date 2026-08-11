<#
.SYNOPSIS
    Samples the RigPilot runtime's memory and CPU footprint over time.

.DESCRIPTION
    The release gate tracks combined working set against a < 300 MB beta / < 250 MB 1.0
    target and requires a 24-hour memory-growth soak. Both were previously measured by
    hand, which is why the figures in AI_CONTEXT could not be reproduced on demand. This
    script makes the measurement repeatable.

    It is READ-ONLY: it reads process counters and writes a CSV plus a summary. It never
    touches a capability, a profile, or the service state, so it is safe to leave running.

    The documented figure is a CLOSED-DASHBOARD measurement - the dashboard is a normal
    user-session WPF app and is not part of the resident service footprint. The dashboard
    is therefore excluded unless -IncludeDashboard is passed, and every summary states
    which of the two it measured.

    THREE EVIDENCE DEFECTS FIXED 2026-08-11, all found by the first soak that completed:

    1. CPU unavailable was recorded as a measured zero. TotalProcessorTime is denied for a
       LocalSystem process when this script runs unelevated, the failure was swallowed, and
       the run emitted CpuSeconds=0 for 24 hours. The summary then divided those zeros and
       reported 0.000% mean CPU - a missing measurement wearing the costume of an excellent
       result. CPU validity is now explicit per sample and the summary reports N/A rather
       than a number it cannot support.

    2. There was no summary artifact at all. The summary was emitted as an object to the
       success stream and the caller was expected to redirect it. Two soaks were killed
       before reaching that line, so their redirections produced 0-byte files; a third ran
       without redirection and produced nothing. A completed CSV could therefore carry a
       full run with no readable conclusion. The summary is now a file this script owns,
       written atomically, and produced even when the run is interrupted.

    3. The CSV recorded per-process WORKING SET only. Working set trims and refaults by
       tens of megabytes without any allocation changing, so during the 24-hour analysis
       residency changes were repeatedly mistaken for retained allocation. Every sample now
       carries per-process private commit beside working set, keyed by PID.

.PARAMETER DurationMinutes
    Total sampling window. Use 1440 for the 24-hour soak.

.PARAMETER IntervalSeconds
    Seconds between samples. Keep it coarse for long soaks; the point is growth, not noise.

.PARAMETER OutputPath
    CSV path. The summary is written beside it as <name>.summary.txt.

.PARAMETER IncludeDashboard
    Include the user-session dashboard processes in the measured set.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 10080)]
    [int]$DurationMinutes = 10,

    [ValidateRange(1, 3600)]
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

$summaryPath = [System.IO.Path]::ChangeExtension($OutputPath, $null) + "summary.txt"

function Get-AdapterHostRoles {
    <#
        Maps AdapterHost PIDs to their session role, so per-process rows are attributable
        without correlating named pipes by hand.

        Four processes share the image name PCHelper.AdapterHost - the general/LHM host plus
        one child per GPU session - and every distinguishing fact lives in the command line.
        Pipe names carry the mode but are stamped with the SERVICE pid, not the child's, so
        they identify which sessions exist and never which process is which.

        Win32_Process.CommandLine is unreadable for a LocalSystem process from an unelevated
        context: it returns null rather than failing, which previously produced a role table
        that silently labelled everything - including the service - as the default. Roles are
        therefore reported as "unknown" when the read is denied, never guessed from memory
        size or spawn order.
    #>
    $roles = @{}
    try {
        foreach ($row in @(Get-CimInstance Win32_Process -Filter "Name='PCHelper.AdapterHost.exe'" -ErrorAction Stop)) {
            $role = switch -Regex ([string]$row.CommandLine) {
                '--gpu-fan-session'   { 'fan'; break }
                '--gpu-power-session' { 'power'; break }
                '--gpu-clock-session' { 'clock'; break }
                '\S'                  { 'general'; break }
                default               { 'unknown' }
            }
            $roles[[string]$row.ProcessId] = $role
        }
    }
    catch {
    }
    return $roles
}

function Get-FootprintSample {
    param([string[]]$Names, [hashtable]$Roles)

    $processes = @(Get-Process -Name $Names -ErrorAction SilentlyContinue)
    $sample = [pscustomobject]@{
        TimestampUtc         = (Get-Date).ToUniversalTime().ToString("o")
        ProcessCount         = $processes.Count
        WorkingSetMB         = 0.0
        PrivateWorkingSetMB  = 0.0
        PrivateMB            = 0.0
        # Blank, never 0, when the CPU read set is incomplete. A denied read is not an
        # idle process, and emitting zero is what produced a false 0.000% for 24 hours.
        CpuSeconds           = ""
        CpuReadsAttempted    = 0
        CpuReadsSucceeded    = 0
        # Legacy field: per-process WORKING SET only. Retained unchanged so historical CSVs
        # and any existing parser keep working. Prefer ProcessBreakdown for new analysis.
        Breakdown            = ""
        # name#pid:ws=<MB>;private=<MB> records joined by '|'. No commas, so it survives CSV
        # escaping, and PID is the continuity key across samples.
        ProcessBreakdown     = ""
    }

    if ($processes.Count -eq 0) {
        return $sample
    }

    $sample.WorkingSetMB = [math]::Round((($processes | Measure-Object -Property WorkingSet64 -Sum).Sum / 1MB), 1)
    $sample.PrivateMB = [math]::Round((($processes | Measure-Object -Property PrivateMemorySize64 -Sum).Sum / 1MB), 1)

    # Summing WorkingSet64 across processes counts every shared page once per process, and
    # these processes all map the same .NET runtime and framework images. Measured on the
    # reference machine that inflated a 150 MB runtime to 317 MB purely by triple-counting
    # pages that exist once in physical memory. Private working set is the resident memory
    # that is genuinely this suite's, so it is reported alongside.
    #
    # Joined on PID rather than the counter instance name: two adapter hosts share the name
    # "PCHelper.AdapterHost", and instance-name lookups silently collide on it.
    $privateWorkingSet = 0.0
    try {
        $counters = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process -ErrorAction Stop |
            Where-Object { $_.IDProcess -gt 0 })
        foreach ($process in $processes) {
            $row = $counters | Where-Object { $_.IDProcess -eq $process.Id } | Select-Object -First 1
            if ($null -ne $row) {
                $privateWorkingSet += [double]$row.WorkingSetPrivate
            }
        }
        $sample.PrivateWorkingSetMB = [math]::Round(($privateWorkingSet / 1MB), 1)
    }
    catch {
        $sample.PrivateWorkingSetMB = 0.0
    }

    # CPU validity is explicit. TotalProcessorTime is denied for a LocalSystem process when
    # this script is unelevated; that is a missing datum, and a missing datum must never be
    # emitted as the number zero.
    $cpu = 0.0
    $succeeded = 0
    foreach ($process in $processes) {
        try {
            $cpu += $process.TotalProcessorTime.TotalSeconds
            $succeeded++
        }
        catch {
        }
    }

    $sample.CpuReadsAttempted = $processes.Count
    $sample.CpuReadsSucceeded = $succeeded
    # Only a COMPLETE read set is a usable total. A partial sum is not the suite's CPU and
    # must not be allowed to look like one.
    $sample.CpuSeconds = if ($succeeded -eq $processes.Count) { [math]::Round($cpu, 2) } else { "" }

    $ordered = $processes | Sort-Object -Property WorkingSet64 -Descending
    $sample.Breakdown = ($ordered | ForEach-Object {
        "$($_.Name)#$($_.Id)=$([math]::Round($_.WorkingSet64 / 1MB, 1))"
    }) -join " "
    $sample.ProcessBreakdown = ($ordered | ForEach-Object {
        $role = if ($_.Name -ne 'PCHelper.AdapterHost') { 'n/a' }
                elseif ($null -ne $Roles -and $Roles.ContainsKey([string]$_.Id)) { $Roles[[string]$_.Id] }
                else { 'unknown' }
        "$($_.Name)#$($_.Id):role=$role;ws=$([math]::Round($_.WorkingSet64 / 1MB, 1));private=$([math]::Round($_.PrivateMemorySize64 / 1MB, 1))"
    }) -join "|"

    return $sample
}

function Get-ProcessCommitMap {
    param([string]$Breakdown)

    $map = @{}
    if ([string]::IsNullOrWhiteSpace($Breakdown)) { return $map }
    foreach ($record in ($Breakdown -split '\|')) {
        if ($record -match '^(?<name>.+)#(?<pid>\d+):(role=(?<role>[^;]+);)?ws=(?<ws>[-\d\.]+);private=(?<private>[-\d\.]+)$') {
            $map[$Matches['pid']] = [pscustomobject]@{
                Name       = $Matches['name']
                Role       = if ($Matches['role']) { $Matches['role'] } else { 'n/a' }
                WorkingSet = [double]$Matches['ws']
                Private    = [double]$Matches['private']
            }
        }
    }
    return $map
}

function New-FootprintSummary {
    param(
        [System.Collections.Generic.List[object]]$Samples,
        [string]$Scope,
        [string]$Csv,
        [int]$ExpectedInterval,
        [bool]$Completed)

    $lines = [System.Collections.Generic.List[string]]::new()
    $first = $Samples[0]
    $last = $Samples[$Samples.Count - 1]
    $times = @($Samples | ForEach-Object { [datetime]$_.TimestampUtc })
    $elapsedSeconds = ($times[$times.Count - 1] - $times[0]).TotalSeconds

    $lines.Add("RigPilot runtime footprint summary")
    $lines.Add("Scope               : $Scope")
    $lines.Add("Run                 : $(if ($Completed) { 'completed' } else { 'INTERRUPTED - partial run' })")
    $lines.Add("Start (UTC)         : $($times[0].ToString('o'))")
    $lines.Add("End (UTC)           : $($times[$times.Count - 1].ToString('o'))")
    $lines.Add("Elapsed             : $([math]::Round($elapsedSeconds / 60, 1)) min")
    $lines.Add("Samples             : $($Samples.Count)")
    $lines.Add("Expected interval   : $ExpectedInterval s")

    if ($Samples.Count -ge 2) {
        $intervals = @(for ($i = 1; $i -lt $times.Count; $i++) { ($times[$i] - $times[$i - 1]).TotalSeconds })
        $stat = $intervals | Measure-Object -Minimum -Maximum -Average
        $lines.Add("Cadence min/mean/max: $([math]::Round($stat.Minimum,1)) / $([math]::Round($stat.Average,1)) / $([math]::Round($stat.Maximum,1)) s")
    }

    $malformed = @($Samples | Where-Object { [string]::IsNullOrWhiteSpace($_.ProcessBreakdown) }).Count
    $lines.Add("Samples with no per-process data: $malformed")
    $lines.Add("Distinct process counts         : $((@($Samples | ForEach-Object { $_.ProcessCount }) | Sort-Object -Unique) -join ', ')")
    $lines.Add("")

    foreach ($metric in @('WorkingSetMB', 'PrivateWorkingSetMB', 'PrivateMB')) {
        $values = @($Samples | ForEach-Object { [double]$_.$metric })
        $stat = $values | Measure-Object -Minimum -Maximum -Average
        $lines.Add(("{0,-20} first={1,8} min={2,8} max={3,8} mean={4,8} last={5,8} delta={6,8}" -f `
            $metric, $values[0], $stat.Minimum, $stat.Maximum, [math]::Round($stat.Average, 1), `
            $values[$values.Count - 1], [math]::Round($values[$values.Count - 1] - $values[0], 1)))
    }

    $lines.Add("")
    # CPU is reported only when a complete read set exists at both ends. Anything else is
    # N/A with the reason, because a denied read is not zero usage.
    $validFirst = -not [string]::IsNullOrWhiteSpace([string]$first.CpuSeconds)
    $validLast = -not [string]::IsNullOrWhiteSpace([string]$last.CpuSeconds)
    $unavailable = @($Samples | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.CpuSeconds) }).Count
    if ($validFirst -and $validLast -and $elapsedSeconds -gt 0) {
        $percent = [math]::Round(((([double]$last.CpuSeconds - [double]$first.CpuSeconds) / $elapsedSeconds) / [Environment]::ProcessorCount) * 100, 3)
        $lines.Add("Mean CPU            : $percent %")
        if ($unavailable -gt 0) {
            $lines.Add("                      (note: $unavailable of $($Samples.Count) samples had an incomplete CPU read set)")
        }
    }
    else {
        $lines.Add("Mean CPU            : N/A - CPU was not measurable for this run.")
        $lines.Add("                      $unavailable of $($Samples.Count) samples had an incomplete CPU read set.")
        $lines.Add("                      TotalProcessorTime is denied for LocalSystem processes when this")
        $lines.Add("                      script runs unelevated. This is a MISSING measurement, not 0% CPU.")
    }

    $lines.Add("")
    $lines.Add("Per-process, first -> last (PID is the continuity key):")
    $firstMap = Get-ProcessCommitMap -Breakdown $first.ProcessBreakdown
    $lastMap = Get-ProcessCommitMap -Breakdown $last.ProcessBreakdown
    $allPids = @(@($firstMap.Keys) + @($lastMap.Keys)) | Sort-Object -Unique
    foreach ($processId in $allPids) {
        if ($firstMap.ContainsKey($processId) -and $lastMap.ContainsKey($processId)) {
            $a = $firstMap[$processId]
            $b = $lastMap[$processId]
            $lines.Add(("  {0,-22} #{1,-6} {2,-8} ws {3,8} -> {4,-8} ({5,7})   private {6,8} -> {7,-8} ({8,7})" -f `
                $a.Name, $processId, $a.Role, $a.WorkingSet, $b.WorkingSet, [math]::Round($b.WorkingSet - $a.WorkingSet, 1), `
                $a.Private, $b.Private, [math]::Round($b.Private - $a.Private, 1)))
        }
        elseif ($lastMap.ContainsKey($processId)) {
            $lines.Add("  APPEARED  #$processId $($lastMap[$processId].Name)")
        }
        else {
            $lines.Add("  EXITED    #$processId $($firstMap[$processId].Name)")
        }
    }

    if (((@($firstMap.Keys) | Sort-Object) -join ',') -ne ((@($lastMap.Keys) | Sort-Object) -join ',')) {
        $lines.Add("")
        $lines.Add("WARNING: the PID set changed during this run. That is a lifecycle event -")
        $lines.Add("         analyse the segments either side separately rather than reading the")
        $lines.Add("         difference as memory growth or reclamation.")
    }

    $lines.Add("")
    $lines.Add("CSV: $Csv")
    return ($lines -join [Environment]::NewLine)
}

function Write-FootprintSummary {
    param([string]$Content, [string]$Path)

    # Written to a sibling temp file and moved into place, so an interrupted or failed write
    # can never leave a 0-byte file that looks like a successful summary.
    $temporary = "$Path.tmp"
    Set-Content -LiteralPath $temporary -Value $Content -Encoding utf8
    $written = Get-Item -LiteralPath $temporary
    if ($written.Length -le 0) {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        throw "Refusing to publish an empty footprint summary."
    }

    Move-Item -LiteralPath $temporary -Destination $Path -Force
    return (Get-Item -LiteralPath $Path).Length
}

$deadline = (Get-Date).AddMinutes($DurationMinutes)
$samples = [System.Collections.Generic.List[object]]::new()
$scopeLabel = if ($IncludeDashboard) { "service + dashboard" } else { "service only (closed-dashboard)" }
$completed = $false

Write-Host "Sampling $scopeLabel every $IntervalSeconds s until $($deadline.ToString('u'))."
Write-Host "CSV     : $OutputPath"
Write-Host "Summary : $summaryPath"

try {
    while ((Get-Date) -lt $deadline) {
        $sample = Get-FootprintSample -Names $processNames -Roles (Get-AdapterHostRoles)
        $samples.Add($sample)
        $sample | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Append -Encoding utf8

        $remaining = [int]($deadline - (Get-Date)).TotalSeconds
        if ($remaining -le 0) { break }
        Start-Sleep -Seconds ([math]::Min($IntervalSeconds, $remaining))
    }
    $completed = $true
}
finally {
    # A summary is produced even when the run is interrupted. Three previous soaks ended
    # with a complete CSV and no readable conclusion because this only ran on the happy path.
    if ($samples.Count -gt 0) {
        $summary = New-FootprintSummary -Samples $samples -Scope $scopeLabel -Csv $OutputPath `
            -ExpectedInterval $IntervalSeconds -Completed $completed
        $bytes = Write-FootprintSummary -Content $summary -Path $summaryPath
        Write-Host ""
        Write-Host $summary
        Write-Host ""
        Write-Host "Summary written: $summaryPath ($bytes bytes)"
    }
    else {
        Write-Warning "No samples were taken; no summary was written."
    }
}
