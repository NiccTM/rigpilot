[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CliPath,
    [string]$OperationId = "f29e1c16d65e4ea6918b709fdb4db21e",
    [string]$SessionId = "commission.d4567c50d090444abb0df7f5b395fdde",
    [string]$CapabilityId = "lhm.control:/lpc/nct6798d/0/control/0",
    [string]$RpmSensorId = "lhm.sensor:/lpc/nct6798d/0/fan/0",
    [string]$HeaderAlias = "CASE_FAN_1",
    [string]$ReportDirectory
)

<#
.SYNOPSIS
Exports read-only evidence from one completed case-fan calibration.

.DESCRIPTION
This command never starts a calibration, changes a profile, writes a fan
controller, or changes a commissioning session.  It reads the service's most
recent operation and the matching commissioning session, validates their exact
identity, and writes a normalized local evidence record.

A completed calibration where the fan remains spinning at the controller's
minimum command is recorded as a no-stall characterization.  It is not
reported as restart-qualified and cannot justify a cooling curve.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-RigPilotCli([string[]]$Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:cli
    # Every argument is a fixed command/flag or a validated identifier from the
    # parameter block. No command line is built from arbitrary user text.
    $startInfo.Arguments = $Arguments -join " "
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start RigPilot CLI: $script:cli"
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdoutTask, $stderrTask))
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-RigPilotJson([string[]]$Arguments) {
    $result = Invoke-RigPilotCli $Arguments
    $text = $result.StandardOutput.Trim()
    if ($result.ExitCode -ne 0) {
        $detailParts = @($result.StandardOutput.Trim(), $result.StandardError.Trim()) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $detail = [string]::Join(" ", [string[]]$detailParts)
        throw "RigPilot CLI exited $($result.ExitCode) for '$($Arguments[0])': $detail"
    }
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "RigPilot CLI returned no JSON for '$($Arguments[0])'."
    }

    try {
        $parsed = $text | ConvertFrom-Json
        foreach ($item in $parsed) {
            Write-Output $item
        }
    }
    catch {
        throw "RigPilot CLI returned invalid JSON for '$($Arguments[0])': $text"
    }
}

function Write-Evidence([System.Collections.IDictionary]$Evidence, [string]$Path) {
    $Evidence | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $Path -Encoding utf8
}

$script:cli = [System.IO.Path]::GetFullPath($CliPath)
if (-not (Test-Path -LiteralPath $script:cli -PathType Leaf)) {
    throw "RigPilot CLI was not found: $script:cli"
}
if ($OperationId -notmatch '^[A-Fa-f0-9]{32}$') {
    throw "OperationId must be the expected 32-character operation identifier."
}
if ($SessionId -notmatch '^commission\.[A-Fa-f0-9]{32}$') {
    throw "SessionId must be a RigPilot commissioning session identifier."
}
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $PSScriptRoot "..\artifacts\commissioning"
}
$reportRoot = [System.IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$reportPath = Join-Path $reportRoot ("case-fan-evidence-" + (Get-Date -Format "yyyyMMdd-HHmmss-fff") + ".json")

$evidence = [ordered]@{
    schemaVersion = 1
    generatedAt = [DateTimeOffset]::UtcNow
    generator = "Export-CaseFanCalibrationEvidence.ps1"
    readOnlyEvidenceExport = $true
    cliPath = $script:cli
    expectedOperationId = $OperationId
    expectedSessionId = $SessionId
    expectedCapabilityId = $CapabilityId
    expectedRpmSensorId = $RpmSensorId
    headerAlias = $HeaderAlias
    operationLookupMode = "NotRun"
    service = $null
    operation = $null
    session = $null
    outcome = "NotEvaluated"
    curveEligible = $false
    reasons = @()
}

try {
    $evidence.service = Invoke-RigPilotJson @("status", "--json")
    try {
        $operation = Invoke-RigPilotJson @("operation", "--id", $OperationId, "--json")
        $evidence.operationLookupMode = "ExactIdRequestedIdentityChecked"
    }
    catch {
        if ($_.Exception.Message -notmatch "NOT_IMPLEMENTED|Unknown command|IPC_ERROR:.*IpcRequest") {
            throw
        }

        # A temporary pre-lookup service can be read safely, but cannot prove
        # an historical query path. The strict operation-ID comparison below
        # still prevents unrelated latest-operation data from becoming evidence.
        $operation = Invoke-RigPilotJson @("operation", "--json")
        $evidence.operationLookupMode = "LatestIdentityCheckedFallback"
    }
    if ($null -eq $operation) {
        throw "The service did not return a current or recent hardware operation."
    }
    if ([string]$operation.id -ne $OperationId) {
        throw "The service returned operation '$($operation.id)' instead of expected '$OperationId'. No valid calibration evidence was produced."
    }
    if ([string]$operation.capabilityId -ne $CapabilityId) {
        throw "The operation capability does not match the selected case-fan controller."
    }
    if ([string]$operation.kind -ne "Calibration" -or [string]$operation.state -ne "Completed") {
        throw "The expected operation is not a completed fan calibration."
    }
    if ($null -eq $operation.calibrationResult) {
        throw "The completed calibration has no calibration result."
    }
    if ([string]$operation.calibrationResult.rpmSensorId -ne $RpmSensorId) {
        throw "The calibration RPM sensor does not match the expected case-fan sensor."
    }
    $evidence.operation = $operation

    $sessions = @(Invoke-RigPilotJson -Arguments @("commission-sessions", "--json"))
    $expectedSessionId = $SessionId.Trim()
    $matchingSessions = [System.Collections.Generic.List[object]]::new()
    $returnedIds = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in $sessions) {
        $candidateId = [string]$candidate.id
        $returnedIds.Add($candidateId)
        if ([string]::Equals($candidateId, $expectedSessionId, [System.StringComparison]::Ordinal)) {
            $matchingSessions.Add($candidate)
        }
    }
    if ($matchingSessions.Count -ne 1) {
        $returnedIdText = [string]::Join(", ", [string[]]$returnedIds)
        throw "Expected exactly one commissioning session '$expectedSessionId'; the service returned $($matchingSessions.Count) matching records. Returned IDs: $returnedIdText"
    }
    $session = $matchingSessions[0]
    if ([string]$session.capabilityId -ne $CapabilityId -or [string]$session.rpmSensorId -ne $RpmSensorId) {
        throw "The commissioning session does not match the calibration control and RPM sensor."
    }
    $evidence.session = $session

    $result = $operation.calibrationResult
    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($result.restartVerified -ne $true) {
        $reasons.Add("Restart verification is false.")
    }
    if ($null -eq $result.stallDutyPercent) {
        $reasons.Add("No stall was observed at the controller minimum command.")
    }
    if ($null -eq $result.restartDutyPercent) {
        $reasons.Add("No restart duty was measured.")
    }
    if ([int]$result.restartVerificationCyclesCompleted -lt 1) {
        $reasons.Add("No completed restart cycle is recorded.")
    }
    if ($session.physicalHeaderObserved -ne $true) {
        $reasons.Add("Physical header observation is not recorded for this declared alias.")
    }

    $evidence.reasons = @($reasons)
    if ($result.restartVerified -eq $true -and $session.physicalHeaderObserved -eq $true) {
        $evidence.outcome = "RestartQualified"
        $evidence.curveEligible = $true
    }
    elseif ($null -eq $result.stallDutyPercent) {
        $evidence.outcome = "CompletedNoStallObservedAtMinimumCommand"
    }
    else {
        $evidence.outcome = "CompletedRestartNotVerified"
    }
}
catch {
    $evidence.outcome = "EvidenceExportFailed"
    $evidence.reasons = @($_.Exception.Message)
    throw
}
finally {
    $evidence.completedAt = [DateTimeOffset]::UtcNow
    Write-Evidence $evidence $reportPath
}

[pscustomobject]@{
    reportPath = $reportPath
    outcome = $evidence.outcome
    curveEligible = $evidence.curveEligible
    operationLookupMode = $evidence.operationLookupMode
    reasons = $evidence.reasons
}
