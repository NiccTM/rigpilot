[CmdletBinding()]
param(
    [string]$PayloadRoot,
    [string]$DeploymentRoot,
    [ValidateRange(10, 90)]
    [int]$ServiceTimeoutSeconds = 45,
    [switch]$ReplaceExistingLocalAlpha
)

<#
.SYNOPSIS
Stages a reversible, local RigPilot alpha service deployment.

.DESCRIPTION
This development-only deployment never overwrites the installed PC Helper
files. It copies one validated, matching payload beneath ProgramData, backs up
the complete PCHelper service registry key and data directory, then points the
existing PCHelper service at the staged alpha executable. A protocol-2
handshake and read-only inventory probe must pass before the deployment is
left active. Any deployment failure restores the original service registry
configuration and starts the original service again.

Run this script through normal Windows UAC. It does not bypass UAC, install a
driver, alter firmware, or send a hardware control command. Use
Restore-LocalAlphaRuntime.ps1 to return to the installed service later.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testPayloadScript = Join-Path $PSScriptRoot "Test-RuntimePayload.ps1"
$serviceName = "PCHelper"
$serviceRegistryPath = "SYSTEM\CurrentControlSet\Services\$serviceName"
$serviceDataRoot = Join-Path $env:ProgramData "PCHelper"

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
    $serviceKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($serviceRegistryPath, $false)
    if ($null -eq $serviceKey) {
        throw "Service registry key was not found: HKLM:\$serviceRegistryPath"
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

    $serviceKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($serviceRegistryPath, $true)
    if ($null -eq $serviceKey) {
        throw "Service registry key was not found: HKLM:\$serviceRegistryPath"
    }

    try {
        # Do not use sc.exe or the registry provider: both can normalise quote
        # characters in an executable command line.
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

function Invoke-RigPilotCli([string]$Executable, [string[]]$Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
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
            $detail = [string]::Join(" ", [string[]]@($output, $error | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }))
            $lastFailure = "CLI exit code $($result.ExitCode): $detail"
        }
        if ([DateTimeOffset]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds 1
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The staged alpha service did not become pipe-ready within $ServiceTimeoutSeconds seconds. Last result: $lastFailure"
}

function Write-Manifest([System.Collections.IDictionary]$Manifest, [string]$Path) {
    $Manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Restore-OriginalService([string]$RegistryBackupPath, [string]$ExpectedImagePath, [string]$OriginalState) {
    Stop-PCHelperService
    # reg.exe reports success on stderr; scope ErrorActionPreference so that native
    # stderr line does not become a terminating error that aborts a working restore.
    $registryImportOutput = & {
        $ErrorActionPreference = "Continue"
        reg.exe import $RegistryBackupPath 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Service registry restore failed with exit code ${LASTEXITCODE}: $registryImportOutput"
    }
    if ((Get-PCHelperImagePath) -cne $ExpectedImagePath) {
        throw "Original service ImagePath was not restored exactly."
    }
    if ($OriginalState -eq "Running") {
        Start-PCHelperService
    }
    $actualState = (Get-Service -Name $serviceName -ErrorAction Stop).Status.ToString()
    if ($actualState -ne $OriginalState) {
        throw "Original service state was not restored. Expected '$OriginalState', got '$actualState'."
    }
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required. Start this script through Windows UAC."
}
if ([string]::IsNullOrWhiteSpace($PayloadRoot)) {
    $PayloadRoot = Join-Path $repoRoot "artifacts\publish"
}

$payload = [System.IO.Path]::GetFullPath($PayloadRoot)
if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
    throw "Runtime payload does not exist: $payload"
}
& $testPayloadScript -PayloadRoot $payload -ExpectedProductVersion "0.5.0"

if ($null -ne (Get-Process -Name "PCHelper.App" -ErrorAction SilentlyContinue)) {
    throw "Close the RigPilot dashboard before switching the service runtime."
}

$originalService = Get-Service -Name $serviceName -ErrorAction Stop
$originalState = $originalService.Status.ToString()
$originalImagePath = Get-PCHelperImagePath
if ($originalImagePath -match '(?i)\\RigPilot\\LocalAlpha\\' -and -not $ReplaceExistingLocalAlpha) {
    throw "A local alpha service is already active. Restore it first with Restore-LocalAlphaRuntime.ps1, or explicitly use -ReplaceExistingLocalAlpha after inspecting its deployment manifest."
}
if ($ReplaceExistingLocalAlpha) {
    throw "Replacing an existing local alpha in place is intentionally unsupported. Restore the active alpha first so a known registry backup remains available."
}

if ([string]::IsNullOrWhiteSpace($DeploymentRoot)) {
    $DeploymentRoot = Join-Path $env:ProgramData ("RigPilot\LocalAlpha\0.5.0-alpha-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$stageRoot = [System.IO.Path]::GetFullPath($DeploymentRoot)
if (Test-Path -LiteralPath $stageRoot) {
    throw "Deployment directory already exists: $stageRoot"
}

$stagePayload = Join-Path $stageRoot "payload"
$registryBackupPath = Join-Path $stageRoot "PCHelper-service-before.reg"
$stateBackupPath = Join-Path $stageRoot "PCHelper-data-before"
$manifestPath = Join-Path $stageRoot "deployment.json"
$alphaService = Join-Path $stagePayload "service\PCHelper.Service.exe"
$alphaCli = Join-Path $stagePayload "cli\pchelper-cli.exe"
$alphaImagePath = '"{0}"' -f $alphaService
$manifest = [ordered]@{
    schemaVersion = 1
    product = "RigPilot"
    serviceName = $serviceName
    createdAt = [DateTimeOffset]::UtcNow
    deploymentRoot = $stageRoot
    payloadRoot = $payload
    originalImagePath = $originalImagePath
    originalServiceState = $originalState
    alphaImagePath = $alphaImagePath
    registryBackupPath = $registryBackupPath
    serviceDataRoot = $serviceDataRoot
    stateBackupPath = $stateBackupPath
    stateBackupCreated = $false
    handshake = $null
    probe = $null
    deployed = $false
    restoredAfterFailure = $false
    failure = $null
}

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    Copy-Item -LiteralPath $payload -Destination $stagePayload -Recurse -Force
    & $testPayloadScript -PayloadRoot $stagePayload -ExpectedProductVersion "0.5.0"
    if (-not (Test-Path -LiteralPath $alphaService -PathType Leaf) -or -not (Test-Path -LiteralPath $alphaCli -PathType Leaf)) {
        throw "Validated payload does not contain the required alpha service and CLI executables."
    }
    & icacls.exe $stageRoot /grant "*S-1-5-18:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not grant LocalSystem full control of the alpha staging directory."
    }
    $registryExportOutput = & {
        $ErrorActionPreference = "Continue"
        reg.exe export "HKLM\$serviceRegistryPath" $registryBackupPath /y 2>&1
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $registryBackupPath -PathType Leaf)) {
        throw "The installed service registry backup could not be created: $registryExportOutput"
    }
    Write-Manifest $manifest $manifestPath

    Stop-PCHelperService
    if (Test-Path -LiteralPath $serviceDataRoot -PathType Container) {
        New-Item -ItemType Directory -Path $stateBackupPath -Force | Out-Null
        Get-ChildItem -LiteralPath $serviceDataRoot -Force | ForEach-Object {
            # SQLite -shm/-wal sidecar files are transient: they can vanish
            # between enumeration and copy when the service releases the
            # database. A file that no longer exists needs no backup.
            try {
                Copy-Item -LiteralPath $_.FullName -Destination $stateBackupPath -Recurse -Force -ErrorAction Stop
            }
            catch [System.Management.Automation.ItemNotFoundException] {
                Write-Verbose "Skipped transient file that disappeared during backup: $($_.FullName)"
            }
        }
        $manifest.stateBackupCreated = $true
        Write-Manifest $manifest $manifestPath
    }

    Set-PCHelperImagePath $alphaImagePath
    Start-PCHelperService
    $handshake = Wait-ForAlphaRuntimeHandshake $alphaCli
    $manifest.handshake = $handshake

    $probeResult = Invoke-RigPilotCli $alphaCli @("probe", "--json")
    if ($probeResult.ExitCode -notin @(0, 3) -or [string]::IsNullOrWhiteSpace($probeResult.StandardOutput)) {
        throw "The staged alpha read-only inventory probe failed: $($probeResult.StandardError.Trim())"
    }
    $probe = $probeResult.StandardOutput | ConvertFrom-Json
    $probeResult.StandardOutput | Set-Content -LiteralPath (Join-Path $stageRoot "alpha-probe.json") -Encoding utf8
    $manifest.probe = [ordered]@{
        capturedAt = $probe.capturedAt
        deviceCount = @($probe.devices).Count
        sensorCount = @($probe.sensors).Count
        capabilityCount = @($probe.capabilities).Count
        warningCount = @($probe.warnings).Count
    }
    $manifest.deployed = $true
    $manifest.deployedAt = [DateTimeOffset]::UtcNow
    Write-Manifest $manifest $manifestPath
}
catch {
    $manifest.failure = $_ | Out-String
    try {
        if (Test-Path -LiteralPath $registryBackupPath -PathType Leaf) {
            Restore-OriginalService $registryBackupPath $originalImagePath $originalState
            $manifest.restoredAfterFailure = $true
        }
    }
    finally {
        if (Test-Path -LiteralPath $stageRoot) {
            Write-Manifest $manifest $manifestPath
        }
    }
    throw
}

$manifest
