[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CliExecutable,

    [Parameter(Mandatory)]
    [string]$CapabilityId,

    [string]$OutputDirectory = (Join-Path $env:ProgramData "RigPilot\Diagnostics"),

    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required because this diagnostic runs one temporary LocalSystem scheduled task."
}

if ($TimeoutSeconds -lt 10 -or $TimeoutSeconds -gt 120) {
    throw "TimeoutSeconds must be from 10 through 120."
}

$cli = [System.IO.Path]::GetFullPath($CliExecutable)
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
    throw "CLI executable was not found: $cli"
}

if ([string]::IsNullOrWhiteSpace($CapabilityId) -or
    $CapabilityId -notmatch '^lhm\.control:/lpc/[A-Za-z0-9._-]+/[0-9]+/control/[0-9]+$') {
    throw "CapabilityId must be an exact LPC LibreHardwareMonitor cooling-control ID."
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $outputRoot "local-system-direct-prepare-$timestamp.json"
$taskName = "RigPilot-LocalSystem-DirectPrepare-$([guid]::NewGuid().ToString('N'))"
$stageRoot = Join-Path $outputRoot ("local-system-direct-prepare-stage-" + [guid]::NewGuid().ToString('N'))
$stageCliDirectory = Join-Path $stageRoot "cli"
$stageCli = Join-Path $stageCliDirectory (Split-Path -Path $cli -Leaf)

# The capability pattern above permits only the exact LHM LPC identifier shape.
# The staged CLI runs from ProgramData so the LocalSystem task never depends on
# a user profile, OneDrive ACL, or mutable build-output directory.
$arguments = @(
    "direct-prepare",
    "--capability", $CapabilityId,
    "--confirm-no-write",
    "--output", ('"{0}"' -f $reportPath),
    "--json"
) -join " "
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSeconds) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    Copy-Item -LiteralPath (Split-Path -Path $cli -Parent) -Destination $stageCliDirectory -Recurse -Force
    & icacls.exe $stageRoot /grant "*S-1-5-18:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not grant LocalSystem access to the staged diagnostic payload."
    }
    if (-not (Test-Path -LiteralPath $stageCli -PathType Leaf)) {
        throw "The staged CLI executable is missing: $stageCli"
    }

    $action = New-ScheduledTaskAction -Execute $stageCli -Argument $arguments
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $task = Get-ScheduledTask -TaskName $taskName
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName
        if ($task.State -ne "Running") {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ($task.State -eq "Running") {
        throw "The LocalSystem diagnostic did not finish within $TimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "The LocalSystem diagnostic did not produce a report. LastTaskResult=$($taskInfo.LastTaskResult)"
    }

    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.applyIssued -eq $true -or
        $report.verifyIssued -eq $true -or
        $report.rollbackIssued -eq $true -or
        $report.resetIssued -eq $true) {
        throw "The diagnostic reported a forbidden hardware control operation."
    }
    if ($report.processIdentity -ne "LocalSystem") {
        throw "The task did not run as LocalSystem. Observed identity: $($report.processIdentity)"
    }

    $report
}
finally {
    try {
        $registeredTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -ne $registeredTask -and $registeredTask.State -eq "Running") {
            Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        }
    }
    catch {
        # Cleanup remains best-effort; task removal below is still attempted.
    }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
}
