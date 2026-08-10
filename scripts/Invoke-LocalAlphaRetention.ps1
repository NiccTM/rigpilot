[CmdletBinding()]
param(
    [string]$DeploymentRoot,
    [ValidateRange(1, 20)]
    [int]$KeepPreviousSuccessful = 2,
    # Test seam only. Production leaves this empty so the active runtime is resolved from
    # the running service, which is the only source that cannot be spoofed by directory
    # naming. Supplying it does not weaken the guarantee that matters: an active path that
    # does not resolve to a runtime under the root still refuses to delete anything.
    [string]$ActiveServiceImagePath,
    [switch]$Apply
)

<#
.SYNOPSIS
Prunes superseded RigPilot LocalAlpha runtimes, never touching the active one.

.DESCRIPTION
Every deployment stages a complete payload plus a pre-start copy of state.db under
%ProgramData%\RigPilot\LocalAlpha, so each one costs roughly half a gigabyte and nothing
ever removed them. This has been recorded as unbounded system-drive growth twice: 145
runtimes / 63.3 GB before a manual prune on 2026-07-26, and it had already grown back to
6 runtimes / 2.8 GB by 2026-08-09. A manual prune is not a retention policy.

Retention is DRY-RUN BY DEFAULT. Nothing is deleted unless -Apply is passed.

What is kept:
  * The ACTIVE runtime, resolved from the running service's own ImagePath - never from a
    name, a timestamp, or this script's idea of "newest". If that resolution is ambiguous
    the script fails closed and deletes nothing.
  * The newest -KeepPreviousSuccessful runtimes that actually completed a deployment
    (deployment.json with deployed=true), so a rollback target always survives.
  * Anything that cannot be positively classified.

What is pruned:
  * Superseded successful runtimes beyond the retention count.
  * Staged deployments that never completed (deployed != true), which are the residue of
    a failed or rolled-back install and were never a rollback target.

Safety properties:
  * Reparse points are never followed out of the LocalAlpha root, and a runtime directory
    that IS a reparse point is refused rather than deleted, so a redirected folder cannot
    turn this into a delete-anywhere primitive.
  * Every candidate must resolve to a real subdirectory of the LocalAlpha root.
  * The active runtime is excluded by full-path comparison after both sides are resolved.
  * Every retained and deleted path is reported.

This is deliberately NOT wired into service startup: pruning is a deployment concern, and
a service that deletes payloads while coming up is a far worse failure mode than disk use.
Call it from a successful deployment, or run it manually.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-ActiveServiceImagePath {
    $service = Get-CimInstance Win32_Service -Filter "Name='PCHelper'" -ErrorAction SilentlyContinue
    if ($null -eq $service) { return $null }
    $imagePath = $service.PathName
    if ([string]::IsNullOrWhiteSpace($imagePath)) { return $null }
    # ImagePath is a command line: the executable may be quoted and may carry arguments.
    if ($imagePath.StartsWith('"')) {
        $end = $imagePath.IndexOf('"', 1)
        if ($end -gt 1) { return $imagePath.Substring(1, $end - 1) }
        return $null
    }
    return ($imagePath -split '\s+')[0]
}

function Test-IsReparsePoint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $false }
    return (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

if ([string]::IsNullOrWhiteSpace($DeploymentRoot)) {
    $DeploymentRoot = Join-Path $env:ProgramData "RigPilot\LocalAlpha"
}

$root = [System.IO.Path]::GetFullPath($DeploymentRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    [pscustomobject]@{
        Root = $root; Applied = $false; Active = $null
        Retained = @(); Deleted = @(); ReclaimedBytes = 0
        Message = "LocalAlpha root does not exist; nothing to prune."
    }
    return
}
if (Test-IsReparsePoint $root) {
    throw "Refusing to prune: the LocalAlpha root '$root' is a reparse point."
}

# Fail closed on the active runtime. A prune that cannot prove which runtime is live is
# exactly the prune that must not run: the one case where deleting the wrong directory
# stops the machine's hardware service from starting.
$activeImage = if ([string]::IsNullOrWhiteSpace($ActiveServiceImagePath)) {
    Get-ActiveServiceImagePath
} else {
    $ActiveServiceImagePath
}
if ([string]::IsNullOrWhiteSpace($activeImage)) {
    throw "Refusing to prune: the PCHelper service ImagePath could not be resolved, so the active runtime is unknown."
}

$activeRuntime = $null
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$activeFull = [System.IO.Path]::GetFullPath($activeImage)
if ($activeFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    # <root>\<runtime>\payload\service\PCHelper.Service.exe -> <runtime>
    $relative = $activeFull.Substring($rootPrefix.Length)
    $activeRuntime = ($relative -split '[\\/]')[0]
}

$candidates = Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction Stop
$successful = @()
$incomplete = @()
foreach ($candidate in $candidates) {
    if (Test-IsReparsePoint $candidate.FullName) {
        Write-Verbose "Skipping reparse point: $($candidate.FullName)"
        continue
    }
    $manifestPath = Join-Path $candidate.FullName "deployment.json"
    $deployed = $false
    $deployedAt = $candidate.CreationTimeUtc
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.PSObject.Properties.Name -contains 'deployed') { $deployed = [bool]$manifest.deployed }
            if ($manifest.PSObject.Properties.Name -contains 'deployedAt' -and $manifest.deployedAt) {
                $deployedAt = [DateTimeOffset]::Parse($manifest.deployedAt).UtcDateTime
            }
        }
        catch {
            # An unreadable manifest is not evidence of anything; keep the runtime.
            Write-Verbose "Unreadable manifest, retaining: $manifestPath"
            $deployed = $true
        }
    }
    else {
        # No manifest at all: cannot classify, so retain rather than guess.
        $deployed = $true
    }

    $entry = [pscustomobject]@{ Name = $candidate.Name; Path = $candidate.FullName; DeployedAt = $deployedAt }
    if ($deployed) { $successful += $entry } else { $incomplete += $entry }
}

$retained = @()
$toDelete = @()

$activeEntry = $null
if ($activeRuntime) {
    $activeEntry = $successful + $incomplete | Where-Object { $_.Name -eq $activeRuntime } | Select-Object -First 1
}
if ($null -eq $activeEntry) {
    throw "Refusing to prune: the active service image '$activeImage' does not resolve to a runtime under '$root'."
}
$retained += $activeEntry

$previous = $successful |
    Where-Object { $_.Name -ne $activeEntry.Name } |
    Sort-Object DeployedAt -Descending
$keep = @($previous | Select-Object -First $KeepPreviousSuccessful)
$retained += $keep
$toDelete += @($previous | Select-Object -Skip $KeepPreviousSuccessful)
$toDelete += @($incomplete | Where-Object { $_.Name -ne $activeEntry.Name })

$reclaimed = 0L
$deleted = @()
foreach ($target in $toDelete) {
    $size = 0L
    try {
        $size = (Get-ChildItem -LiteralPath $target.Path -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
        if ($null -eq $size) { $size = 0L }
    }
    catch { $size = 0L }

    if ($Apply) {
        Remove-Item -LiteralPath $target.Path -Recurse -Force -Confirm:$false
    }
    $reclaimed += $size
    $deleted += [pscustomobject]@{ Name = $target.Name; Path = $target.Path; Bytes = $size }
}

[pscustomobject]@{
    Root = $root
    Applied = [bool]$Apply
    Active = $activeEntry.Name
    Retained = @($retained | ForEach-Object { $_.Name })
    Deleted = $deleted
    ReclaimedBytes = $reclaimed
    ReclaimedGB = [Math]::Round($reclaimed / 1GB, 2)
    Message = if ($Apply) { "Pruned $($deleted.Count) runtime(s)." } else { "DRY RUN: $($deleted.Count) runtime(s) would be pruned. Pass -Apply to delete." }
}
