[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CliPath,
    [string]$SessionId = "commission.d4567c50d090444abb0df7f5b395fdde",
    [string]$CapabilityId = "lhm.control:/lpc/nct6798d/0/control/0",
    [string]$RpmSensorId = "lhm.sensor:/lpc/nct6798d/0/fan/0",
    [string]$HeaderAlias = "CASE_FAN_1",
    [string]$TemperatureSensorId = "lhm.sensor:/lpc/nct6798d/0/temperature/1",
    [ValidateRange(40, 90)]
    [double]$TemperatureLimitCelsius = 70,
    [ValidateRange(1, 10)]
    [int]$SettlingSeconds = 2,
    [ValidateRange(2, 3)]
    [int]$RestartCycles = 2,
    [ValidateRange(60, 600)]
    [int]$TimeoutSeconds = 300,
    [string]$ReportDirectory
)

<#
.SYNOPSIS
Runs one UAC-approved, bounded case-fan calibration through the typed CLI.

.DESCRIPTION
The script is intentionally limited to one declared case-fan controller. It
checks the matching alpha service contract, exact control/sensor pairing,
absence of active competing writers, a same-controller temperature ceiling,
and the persisted pump protection before it changes commissioning state.

It then stores the generic CASE_FAN_1 role, confirms the already-successful
identification-pulse session, and runs a 0-100% calibration with fan stop and
two restart cycles. The service owns every hardware command and restores the
prior policy through rollback or firmware/default recovery. This script never
creates a cooling graph or enables fan stop after calibration.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-RigPilotCli([string]$Executable, [string[]]$Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    # All command words and values originate in this parameter block. They are
    # validated below and never interpolated into a PowerShell command string.
    $startInfo.Arguments = $Arguments -join " "
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start RigPilot CLI: $Executable"
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

function Invoke-JsonCli(
    [string[]]$Arguments,
    [int[]]$AllowedExitCodes = @(0)
) {
    $result = Invoke-RigPilotCli $cli $Arguments
    if ($result.ExitCode -notin $AllowedExitCodes) {
        $detailParts = @($result.StandardOutput.Trim(), $result.StandardError.Trim()) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $detail = [string]::Join(" ", [string[]]$detailParts)
        throw "RigPilot CLI exited $($result.ExitCode): $detail"
    }
    if ([string]::IsNullOrWhiteSpace($result.StandardOutput)) {
        throw "RigPilot CLI returned no JSON for '$($Arguments[0])'."
    }
    try {
        return $result.StandardOutput | ConvertFrom-Json
    }
    catch {
        throw "RigPilot CLI returned invalid JSON for '$($Arguments[0])': $($result.StandardOutput)"
    }
}

function Write-Report([System.Collections.IDictionary]$Report, [string]$Path) {
    $Report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required. Start this script through one Windows UAC prompt."
}

$cli = [System.IO.Path]::GetFullPath($CliPath)
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
    throw "RigPilot CLI was not found: $cli"
}
if ($HeaderAlias -notmatch '^(?i)(CASE|CHA|CHASSIS|SYS)[_ -]?FAN[_ -]?[0-9A-Z]+$') {
    throw "HeaderAlias must be an explicit case/chassis header alias such as CASE_FAN_1."
}
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $env:ProgramData "RigPilot\Commissioning"
}
$reportRoot = [System.IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$reportPath = Join-Path $reportRoot ("case-fan-calibration-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
$report = [ordered]@{
    schemaVersion = 2
    startedAt = [DateTimeOffset]::UtcNow
    cliPath = $cli
    sessionId = $SessionId
    capabilityId = $CapabilityId
    rpmSensorId = $RpmSensorId
    headerAlias = $HeaderAlias
    temperatureSensorId = $TemperatureSensorId
    temperatureLimitCelsius = $TemperatureLimitCelsius
    role = $null
    sessionValidation = $null
    session = $null
    calibration = $null
    calibrationRestore = $null
    priorFirmwareDefaultResetEvidence = $null
    postCalibrationProbe = $null
    outcome = "NotRun"
    succeeded = $false
    failure = $null
}

try {
    $runtime = Invoke-JsonCli @("runtime-preflight", "--json")
    if ($runtime.state -ne "Ready" -or -not $runtime.canUseServiceWrites) {
        throw "The alpha runtime is not write-ready: $($runtime.summary)"
    }

    $snapshot = Invoke-JsonCli @("probe", "--json")
    if (@($snapshot.conflicts | Where-Object { $_.isRunning }).Count -gt 0) {
        throw "A competing hardware writer is running; calibration is blocked before any command."
    }
    $capability = @($snapshot.capabilities | Where-Object { $_.id -eq $CapabilityId })[0]
    $rpm = @($snapshot.sensors | Where-Object { $_.sensorId -eq $RpmSensorId })[0]
    $thermal = @($snapshot.sensors | Where-Object { $_.sensorId -eq $TemperatureSensorId })[0]
    if ($null -eq $capability -or $null -eq $rpm -or $null -eq $thermal) {
        throw "The requested control, RPM sensor, or temperature safety sensor is absent from the current snapshot."
    }
    if ($capability.adapterId -ne $rpm.adapterId -or $capability.deviceId -ne $rpm.deviceId -or
        $capability.adapterId -ne $thermal.adapterId -or $capability.deviceId -ne $thermal.deviceId -or
        $rpm.unit -ne "RPM" -or ([string]$thermal.unit) -notmatch 'C$') {
        throw "Calibration requires exact same-controller RPM and Celsius safety sensors."
    }
    if ($thermal.quality -ne "Good" -or $null -eq $thermal.value -or [double]$thermal.value -ge $TemperatureLimitCelsius) {
        throw "The temperature safety sensor is not healthy below the requested calibration ceiling."
    }

    $roles = Invoke-JsonCli @("output-roles", "--json")
    $existingRole = @($roles | Where-Object { $_.capabilityId -eq $CapabilityId })[0]
    if ($null -ne $existingRole -and $existingRole.role -in @("Pump", "CpuFan")) {
        throw "The selected output is persistently protected as $($existingRole.role); calibration is blocked."
    }
    if ($null -eq $existingRole -or $existingRole.role -ne "CaseFan" -or $existingRole.headerName -ne $HeaderAlias) {
        $report.role = Invoke-JsonCli @(
            "set-output-role",
            "--capability", $CapabilityId,
            "--rpm-sensor", $RpmSensorId,
            "--header", $HeaderAlias,
            "--role", "CaseFan",
            "--json")
    }
    else {
        $report.role = $existingRole
    }

    $sessions = Invoke-JsonCli @("commission-sessions", "--json")
    $session = @($sessions | Where-Object { $_.id -eq $SessionId })[0]
    if ($null -eq $session) {
        throw "The requested commissioning session was not found."
    }
    $sessionValidation = [ordered]@{
        sessionCapabilityId = [string]$session.capabilityId
        requestedCapabilityId = [string]$CapabilityId
        capabilityMatches = [string]::Equals([string]$session.capabilityId, [string]$CapabilityId, [System.StringComparison]::Ordinal)
        sessionRpmSensorId = [string]$session.rpmSensorId
        requestedRpmSensorId = [string]$RpmSensorId
        rpmMatches = [string]::Equals([string]$session.rpmSensorId, [string]$RpmSensorId, [System.StringComparison]::Ordinal)
        isCpuOrPump = [bool]$session.isCpuOrPump
    }
    $report.sessionValidation = $sessionValidation
    if (-not $sessionValidation.capabilityMatches -or -not $sessionValidation.rpmMatches -or $sessionValidation.isCpuOrPump) {
        throw "The requested commissioning session does not match this non-pump case-fan target."
    }
    if ($session.state -eq "AwaitingIdentification") {
        $session = Invoke-JsonCli @(
            "confirm-case-fan",
            "--session", $SessionId,
            "--header", $HeaderAlias,
            "--confirm-case-fan",
            "--json")
    }
    if ($session.state -ne "ReadyForCalibration" -or -not $session.headerConfirmed) {
        throw "The commissioning session is not ready for bounded calibration. State=$($session.state)"
    }
    $report.session = $session

    $calibration = Invoke-JsonCli -Arguments @(
        "calibrate-case-fan",
        "--session", $SessionId,
        "--capability", $CapabilityId,
        "--rpm-sensor", $RpmSensorId,
        "--temperature-sensor", $TemperatureSensorId,
        "--temperature-limit", $TemperatureLimitCelsius.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--settling-seconds", $SettlingSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--restart-cycles", $RestartCycles.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--timeout-seconds", $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--allow-fan-stop",
        "--confirm-experimental",
        "--confirm-device",
        "--json") -AllowedExitCodes @(0, 3)
    $report.calibration = $calibration
    if ($calibration.operation.state -ne "Completed") {
        $report.outcome = "CalibrationOperationFailed"
        throw "Calibration did not complete; no curve may be enabled."
    }
    if ($calibration.operation.calibrationResult.restartVerified -ne $true) {
        if ($null -eq $calibration.operation.calibrationResult.stallDutyPercent) {
            $report.outcome = "CompletedNoStallObservedAtMinimumCommand"
            throw "Calibration completed and restored the prior policy, but the fan remained running at the controller's minimum command. Restart behaviour is unproven; no curve may be enabled."
        }

        $report.outcome = "CompletedRestartNotVerified"
        throw "Calibration did not achieve a completed, restart-verified commissioning result."
    }
    if ($null -eq $calibration.commissioningSession -or $calibration.commissioningSession.state -ne "Completed") {
        $report.outcome = "RestartVerifiedCommissioningFinalisationFailed"
        throw "Calibration restarted successfully, but the commissioning session was not finalised. No curve may be enabled."
    }
    $report.outcome = "RestartVerified"

    $trace = Invoke-JsonCli @("trace", "--json")
    $operationStartedAt = [DateTimeOffset]$calibration.operation.startedAt
    $calibrationEvents = @($trace | Where-Object {
            $_.capabilityId -eq $CapabilityId -and [DateTimeOffset]$_.timestamp -ge $operationStartedAt
        })
    $rollback = (@($calibrationEvents | Where-Object { $_.operation -eq "Rollback" -and $_.success }))[-1]
    if ($null -eq $rollback) {
        throw "Calibration completed without a successful rollback trace; no curve may be enabled."
    }
    $firmwareReset = (@($trace | Where-Object { $_.capabilityId -eq $CapabilityId -and $_.operation -eq "ResetToDefault" -and $_.success }))[-1]
    if ($null -eq $firmwareReset) {
        throw "No successful firmware/default reset evidence exists for this controller."
    }
    $report.calibrationRestore = $rollback
    $report.priorFirmwareDefaultResetEvidence = $firmwareReset

    $post = Invoke-JsonCli @("probe", "--json")
    $report.postCalibrationProbe = @($post.sensors | Where-Object { $_.sensorId -eq $RpmSensorId })[0]
    if ($null -eq $report.postCalibrationProbe -or $report.postCalibrationProbe.quality -ne "Good") {
        throw "The RPM sensor was not healthy in the post-calibration probe."
    }
    $report.succeeded = $true
}
catch {
    if ($report.outcome -eq "NotRun") {
        $report.outcome = "FailedBeforeCalibration"
    }
    $report.failure = $_ | Out-String
    throw
}
finally {
    $report.completedAt = [DateTimeOffset]::UtcNow
    Write-Report $report $reportPath
}

$report
