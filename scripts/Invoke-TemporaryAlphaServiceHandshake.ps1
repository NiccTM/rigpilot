[CmdletBinding()]
param(
    [string]$PayloadRoot,
    [ValidateRange(10, 60)]
    [int]$ServiceTimeoutSeconds = 30,
    [string]$DiagnosticLogPath,
    [switch]$VerifyImagePathWriteOnly,
    [switch]$RunCommissioningPreflight,
    [switch]$RunCommissioningPulse,
    [string]$CommissionCapabilityId,
    [string]$CommissionRpmSensorId,
    [string]$CommissionHeaderAlias,
    [string]$ProtectedCapabilityId,
    [ValidateRange(2, 5)]
    [int]$CommissionPulseSeconds = 2,
    [ValidateRange(1, 30)]
    [int]$ControllerStabilitySamples = 1,
    [ValidateRange(250, 5000)]
    [int]$ControllerStabilityIntervalMilliseconds = 1100
)

<#
.SYNOPSIS
Runs a reversible protocol-2 alpha service smoke test.

.DESCRIPTION
Stages the already-validated 0.4 alpha payload under ProgramData, starts it
with a private state directory, verifies a protocol-2 handshake and read-only
inventory probe, and restores the installed PCHelper service before returning.

By default this script is read-only. -RunCommissioningPreflight is also
read-only: it sends only the Adapter Host Prepare request and never issues
Apply, Verify, Rollback, or Reset. -RunCommissioningPulse is an explicit,
one-output exception: it runs one 2-5 second identity pulse with no fan-stop,
no calibration, no physical-header certification, and automatic firmware/default
reset. Either commissioning path must be run from an elevated PowerShell session.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testPayloadScript = Join-Path $PSScriptRoot "Test-RuntimePayload.ps1"
$serviceName = "PCHelper"

if ([string]::IsNullOrWhiteSpace($PayloadRoot)) {
    # Use the newest validated publish output rather than a manually copied
    # staging directory, which can silently leave the CLI and service out of sync.
    $PayloadRoot = Join-Path $repoRoot "artifacts\publish"
}
if ([string]::IsNullOrWhiteSpace($DiagnosticLogPath)) {
    $DiagnosticLogPath = Join-Path $env:ProgramData "RigPilot\Commissioning\last-alpha-handshake-error.txt"
}
$runCommissioning = $RunCommissioningPulse -or $RunCommissioningPreflight
if ($RunCommissioningPulse -and $RunCommissioningPreflight) {
    throw "Choose either -RunCommissioningPreflight or -RunCommissioningPulse, not both."
}
if ($runCommissioning) {
    foreach ($required in @{
            CommissionCapabilityId = $CommissionCapabilityId
            CommissionRpmSensorId = $CommissionRpmSensorId
            CommissionHeaderAlias = $CommissionHeaderAlias
            ProtectedCapabilityId = $ProtectedCapabilityId
        }.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$required.Value)) {
            throw "-$($required.Key) is required with -RunCommissioningPulse."
        }
    }
    if ($CommissionCapabilityId -eq $ProtectedCapabilityId) {
        throw "The selected capability is explicitly protected and cannot receive a commissioning pulse."
    }
}

trap {
    try {
        $directory = Split-Path -Path $DiagnosticLogPath -Parent
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $entry = "$(Get-Date -Format o)`r`n$($_ | Out-String)"
        [System.IO.File]::WriteAllText($DiagnosticLogPath, $entry)
    }
    catch {
        # Preserve the original failure even if diagnostic persistence fails.
    }
    exit 1
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-ServiceState([string]$ExpectedState) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ServiceTimeoutSeconds)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        if ($service.Status.ToString() -eq $ExpectedState) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Service '$serviceName' did not reach '$ExpectedState' within $ServiceTimeoutSeconds seconds."
}

function Stop-PCHelperService {
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -ErrorAction Stop
        Wait-ServiceState "Stopped"
    }
}

function Start-PCHelperService {
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Start-Service -Name $serviceName -ErrorAction Stop
        Wait-ServiceState "Running"
    }
}

function Get-PCHelperImagePath {
    $registryPath = "SYSTEM\CurrentControlSet\Services\$serviceName"
    $serviceKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($registryPath, $false)
    if ($null -eq $serviceKey) {
        throw "Service registry key was not found: HKLM:\$registryPath"
    }

    try {
        $kind = $serviceKey.GetValueKind("ImagePath")
        if ($kind -ne [Microsoft.Win32.RegistryValueKind]::ExpandString) {
            throw "Service ImagePath must be REG_EXPAND_SZ. Actual type: $kind"
        }

        $value = $serviceKey.GetValue(
            "ImagePath",
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($null -eq $value) {
            throw "Service ImagePath is missing."
        }

        return [string]$value
    }
    finally {
        $serviceKey.Dispose()
    }
}

function Set-PCHelperImagePath([string]$ImagePath) {
    if ([string]::IsNullOrWhiteSpace($ImagePath)) {
        throw "Refusing to set an empty service ImagePath."
    }

    # Do not use sc.exe or the PowerShell registry provider here. Both have
    # previously normalised away literal command-line quote characters.
    $registryPath = "SYSTEM\CurrentControlSet\Services\$serviceName"
    $serviceKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($registryPath, $true)
    if ($null -eq $serviceKey) {
        throw "Service registry key was not found: HKLM:\$registryPath"
    }

    try {
        $serviceKey.SetValue(
            "ImagePath",
            [string]$ImagePath,
            [Microsoft.Win32.RegistryValueKind]::ExpandString)
    }
    finally {
        $serviceKey.Dispose()
    }

    $actual = Get-PCHelperImagePath
    if ($actual -cne $ImagePath) {
        throw "Service image-path read-back did not match the requested value. Actual: $actual"
    }
}

function Restore-InstalledPCHelperService([string]$RegistryBackupPath, [string]$ExpectedImagePath) {
    if (-not (Test-Path -LiteralPath $RegistryBackupPath -PathType Leaf)) {
        throw "Cannot restore the installed service because its registry backup is missing: $RegistryBackupPath"
    }

    Stop-PCHelperService
    & reg.exe import $RegistryBackupPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Service registry restore failed with exit code $LASTEXITCODE."
    }

    $restoredPath = Get-PCHelperImagePath
    if ($restoredPath -cne $ExpectedImagePath) {
        throw "Installed service registry restore verification failed. ImagePath=$restoredPath"
    }

    Start-PCHelperService

    $service = Get-Service -Name $serviceName -ErrorAction Stop
    $actual = Get-PCHelperImagePath
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running -or $actual -cne $ExpectedImagePath) {
        throw "Installed service restore verification failed. Status=$($service.Status); ImagePath=$actual"
    }
}

function Invoke-RigPilotCli([string]$Executable, [string[]]$Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    # The arguments used by this commissioning helper are fixed command names
    # and flags. Keep them out of a PowerShell native-command invocation so a
    # non-zero, expected readiness result cannot become a terminating error.
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

function Wait-ForAlphaRuntimeHandshake([string]$CliExecutable) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ServiceTimeoutSeconds)
    $lastFailure = "No handshake attempt was made."

    do {
        $result = Invoke-RigPilotCli $CliExecutable @("runtime-preflight", "--json")
        $output = $result.StandardOutput.Trim()
        $error = $result.StandardError.Trim()

        if ($result.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($output)) {
            try {
                $preflight = $output | ConvertFrom-Json
                if ($preflight.state -eq "Ready" -and $preflight.canUseServiceWrites) {
                    return $preflight
                }

                $lastFailure = "Handshake returned state '$($preflight.state)' with canUseServiceWrites='$($preflight.canUseServiceWrites)'."
            }
            catch {
                $lastFailure = "Handshake returned invalid JSON: $output"
            }
        }
        else {
            $detailParts = @($output, $error) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            $detail = [string]::Join(" ", [string[]]$detailParts)
            $lastFailure = "CLI exit code $($result.ExitCode): $detail"
        }

        if ([DateTimeOffset]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds 1
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The alpha service did not become pipe-ready within $ServiceTimeoutSeconds seconds. Last result: $lastFailure"
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required. Start PowerShell with Run as administrator, then rerun this script."
}

$payload = [System.IO.Path]::GetFullPath($PayloadRoot)
if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
    throw "Runtime payload does not exist: $payload"
}

# Contract validation occurs before any service configuration change.
& $testPayloadScript -PayloadRoot $payload -ExpectedProductVersion "0.5.0"

$appProcess = Get-Process -Name "PCHelper.App" -ErrorAction SilentlyContinue
if ($null -ne $appProcess) {
    throw "Close the RigPilot dashboard before the temporary service handshake test."
}

$stageRoot = Join-Path $env:ProgramData ("RigPilot\Commissioning\alpha-handshake-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$stagePayload = Join-Path $stageRoot "payload"
$stageState = Join-Path $stageRoot "state"
$originalImagePath = Get-PCHelperImagePath
$alphaImagePath = '"{0}" --data-dir "{1}"' -f (Join-Path $stagePayload "service\PCHelper.Service.exe"), $stageState
$restored = $false
$report = [ordered]@{
    schemaVersion = 1
    startedAt = [DateTimeOffset]::UtcNow
    stageRoot = $stageRoot
    originalImagePath = $originalImagePath
    alphaImagePath = $alphaImagePath
    stateDirectory = $stageState
    handshake = $null
    probe = $null
    probeStability = $null
    commissioning = $null
    commissioningMode = $null
    commissioningExitCode = $null
    restored = $false
}

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    Copy-Item -LiteralPath $payload -Destination $stagePayload -Recurse -Force
    & $testPayloadScript -PayloadRoot $stagePayload -ExpectedProductVersion "0.5.0"

    $acl = & icacls.exe $stageRoot
    if (-not ($acl | Where-Object { $_ -match 'SYSTEM:.*\(F\)' })) {
        throw "The staging directory does not grant LocalSystem full control."
    }

    & reg.exe export "HKLM\SYSTEM\CurrentControlSet\Services\$serviceName" (Join-Path $stageRoot "PCHelper-service-before.reg") /y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The service registry backup could not be created."
    }

    if ($VerifyImagePathWriteOnly) {
        # This exercises the exact registry write/read path without loading the
        # alpha service or issuing any hardware-related IPC command.
        Set-PCHelperImagePath $originalImagePath
        $report.handshake = [ordered]@{
            state = "RegistryWriteVerified"
            serviceImagePath = Get-PCHelperImagePath
        }
        return
    }

    Stop-PCHelperService
    Set-PCHelperImagePath $alphaImagePath
    Start-PCHelperService

    $sourceCli = Join-Path $stagePayload "cli\pchelper-cli.exe"
    $preflight = Wait-ForAlphaRuntimeHandshake $sourceCli
    $report.handshake = $preflight

    $probeResult = Invoke-RigPilotCli $sourceCli @("probe", "--json")
    if ($probeResult.ExitCode -notin @(0, 3)) {
        $probeDetailParts = @(
            $probeResult.StandardOutput.Trim(),
            $probeResult.StandardError.Trim()) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $probeDetail = [string]::Join(" ", [string[]]$probeDetailParts)
        throw "The alpha read-only inventory probe failed: $probeDetail"
    }
    $probe = $probeResult.StandardOutput | ConvertFrom-Json
    # Preserve the unmodified read-only probe alongside the summary. This is
    # evidence for physical header mapping; it is never used to infer a header
    # name or authorise a write.
    $probeResult.StandardOutput | Set-Content -LiteralPath (Join-Path $stageRoot "alpha-probe.json") -Encoding utf8
    $report.probe = [ordered]@{
        capturedAt = $probe.capturedAt
        deviceCount = @($probe.devices).Count
        sensorCount = @($probe.sensors).Count
        capabilityCount = @($probe.capabilities).Count
        warningCount = @($probe.warnings).Count
        adapterHealth = @($probe.adapterHealth | ForEach-Object {
            [ordered]@{ adapterId = $_.adapterId; healthy = $_.healthy; message = $_.message }
        })
    }

    $probeSamples = @($probe)
    for ($sampleIndex = 1; $sampleIndex -lt $ControllerStabilitySamples; $sampleIndex++) {
        Start-Sleep -Milliseconds $ControllerStabilityIntervalMilliseconds
        $sampleResult = Invoke-RigPilotCli $sourceCli @("probe", "--json")
        if (($sampleResult.ExitCode -notin @(0, 3)) -or [string]::IsNullOrWhiteSpace($sampleResult.StandardOutput)) {
            $sampleDetails = @($sampleResult.StandardOutput.Trim(), $sampleResult.StandardError.Trim()) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            throw "The alpha controller-stability probe #$($sampleIndex + 1) failed: $([string]::Join(' ', [string[]]$sampleDetails))"
        }
        $probeSamples += ($sampleResult.StandardOutput | ConvertFrom-Json)
    }
    $report.probeStability = @($probeSamples | ForEach-Object {
        [ordered]@{
            capturedAt = $_.capturedAt
            deviceCount = @($_.devices).Count
            sensorCount = @($_.sensors).Count
            capabilityCount = @($_.capabilities).Count
            lhmCoolingCapabilityCount = @($_.capabilities | Where-Object { $_.id -like 'lhm.control:*' }).Count
        }
    })

    if ($runCommissioning) {
        foreach ($sample in $probeSamples) {
            $hasCapability = @($sample.capabilities | Where-Object { $_.id -eq $CommissionCapabilityId }).Count -eq 1
            $hasRpmSensor = @($sample.sensors | Where-Object { $_.sensorId -eq $CommissionRpmSensorId }).Count -eq 1
            if (-not $hasCapability -or -not $hasRpmSensor) {
                throw "The selected controller was not stable across every pre-pulse alpha snapshot. Commissioning is blocked before any write."
            }
        }
        $capability = @($probe.capabilities | Where-Object { $_.id -eq $CommissionCapabilityId }) | Select-Object -First 1
        $rpmSensor = @($probe.sensors | Where-Object { $_.sensorId -eq $CommissionRpmSensorId }) | Select-Object -First 1
        if ($null -eq $capability -or $null -eq $rpmSensor) {
            throw "The selected commissioning control or RPM sensor was not present in the alpha read-only probe."
        }
        if ($capability.adapterId -ne $rpmSensor.adapterId -or $capability.deviceId -ne $rpmSensor.deviceId -or $rpmSensor.unit -ne "RPM") {
            throw "Commissioning requires an RPM sensor from the same exact adapter and device."
        }
        $expectedRpmSensor = ($CommissionCapabilityId -replace '^lhm\.control:', 'lhm.sensor:' -replace '/control/', '/fan/')
        if ($CommissionRpmSensorId -ne $expectedRpmSensor) {
            throw "The RPM sensor must be the index-matched sensor for the selected LHM fan control. Expected: $expectedRpmSensor"
        }
        if ($capability.name -match '(?i)cpu|pump' -or $CommissionHeaderAlias -match '(?i)pump|cpu') {
            throw "CPU and pump outputs are prohibited from this provisional chassis-fan pulse."
        }

        $commissionCommand = if ($RunCommissioningPulse) { "commission-pulse" } else { "commission-preflight" }
        $commissionArguments = @(
            $commissionCommand,
            "--capability", $CommissionCapabilityId,
            "--rpm-sensor", $CommissionRpmSensorId,
            "--header", $CommissionHeaderAlias)
        if ($RunCommissioningPulse) {
            $commissionArguments += @("--duration-seconds", $CommissionPulseSeconds.ToString())
        }
        $commissionArguments += @(
            "--confirm-experimental",
            "--confirm-device",
            "--provisional-case-alias",
            "--json")
        $commissionResult = Invoke-RigPilotCli $sourceCli $commissionArguments
        $commissionDetailParts = @(
            $commissionResult.StandardOutput.Trim(),
            $commissionResult.StandardError.Trim()) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $commissionDetail = [string]::Join(" ", [string[]]$commissionDetailParts)
        if ([string]::IsNullOrWhiteSpace($commissionResult.StandardOutput)) {
            [ordered]@{
                schemaVersion = 1
                exitCode = $commissionResult.ExitCode
                standardError = $commissionResult.StandardError.Trim()
                standardOutput = $commissionResult.StandardOutput.Trim()
                capturedAt = [DateTimeOffset]::UtcNow
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stageRoot ("{0}-failure.json" -f $commissionCommand)) -Encoding utf8
            throw "The commissioning command did not return a report. Exit code $($commissionResult.ExitCode): $commissionDetail"
        }
        $commissioning = $commissionResult.StandardOutput | ConvertFrom-Json
        $commissionResult.StandardOutput | Set-Content -LiteralPath (Join-Path $stageRoot ("{0}.json" -f $commissionCommand)) -Encoding utf8
        $report.commissioning = $commissioning
        $report.commissioningMode = if ($RunCommissioningPulse) { "Pulse" } else { "NoWritePreflight" }
        $report.commissioningExitCode = $commissionResult.ExitCode
        if ($RunCommissioningPulse) {
            if ($commissionResult.ExitCode -ne 0 -or $commissioning.operation.state -ne "Completed") {
                throw "The commissioning pulse did not complete successfully. Exit code $($commissionResult.ExitCode): $commissionDetail"
            }
            if ($commissioning.operation.message -notmatch '(?i)firmware/default control was restored') {
                throw "The commissioning pulse completed without explicit firmware/default reset evidence."
            }
        }
        else {
            if ($null -eq $commissioning.preflight) {
                throw "The no-write commissioning preflight returned no structured preflight result. Exit code $($commissionResult.ExitCode): $commissionDetail"
            }
            if ($commissioning.preflight.applyIssued -eq $true -or
                $commissioning.preflight.rollbackIssued -eq $true -or
                $commissioning.preflight.resetIssued -eq $true) {
                throw "The no-write commissioning preflight reported a forbidden hardware control operation."
            }
            if ($commissionResult.ExitCode -notin @(0, 3)) {
                throw "The no-write commissioning preflight exited unexpectedly. Exit code $($commissionResult.ExitCode): $commissionDetail"
            }
        }
    }
}
finally {
    try {
        Restore-InstalledPCHelperService (Join-Path $stageRoot "PCHelper-service-before.reg") $originalImagePath
        $restored = $true
    }
    finally {
        $report.restored = $restored
        $report.completedAt = [DateTimeOffset]::UtcNow
        if (Test-Path -LiteralPath $stageRoot) {
            $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $stageRoot "handshake-report.json") -Encoding utf8
        }
    }
}

if (-not $restored) {
    throw "The installed service could not be restored automatically. Use the registry backup in '$stageRoot' and inspect the service before any hardware operation."
}

$report
