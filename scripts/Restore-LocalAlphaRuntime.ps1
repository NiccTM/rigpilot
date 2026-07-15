[CmdletBinding()]
param(
    [string]$DeploymentRoot,
    [ValidateRange(10, 90)]
    [int]$ServiceTimeoutSeconds = 45
)

<#
.SYNOPSIS
Restores the installed PCHelper service from a local RigPilot alpha deployment.

.DESCRIPTION
The script imports only the registry backup created by
Install-LocalAlphaRuntime.ps1, verifies the exact original ImagePath, and
restores the service to its original running/stopped state. It deliberately
does not delete the staged alpha payload or restore the optional data snapshot:
keeping both makes rollback auditable and avoids discarding user changes made
while the alpha was active.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$serviceName = "PCHelper"
$serviceRegistryPath = "SYSTEM\CurrentControlSet\Services\$serviceName"

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

function Get-PCHelperImagePath {
    $serviceKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($serviceRegistryPath, $false)
    if ($null -eq $serviceKey) {
        throw "Service registry key was not found: HKLM:\$serviceRegistryPath"
    }
    try {
        if ($serviceKey.GetValueKind("ImagePath") -ne [Microsoft.Win32.RegistryValueKind]::ExpandString) {
            throw "Service ImagePath is not REG_EXPAND_SZ."
        }
        $value = $serviceKey.GetValue("ImagePath", $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($null -eq $value) {
            throw "Service ImagePath is missing."
        }
        return [string]$value
    }
    finally {
        $serviceKey.Dispose()
    }
}

function Write-Manifest($Manifest, [string]$Path) {
    $Manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding utf8
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required. Start this script through Windows UAC."
}
if ([string]::IsNullOrWhiteSpace($DeploymentRoot)) {
    $alphaRoot = Join-Path $env:ProgramData "RigPilot\LocalAlpha"
    $latestManifest = Get-ChildItem -LiteralPath $alphaRoot -Filter "deployment.json" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $latestManifest) {
        throw "No local alpha deployment manifest was found below $alphaRoot."
    }
    $DeploymentRoot = Split-Path -Path $latestManifest.FullName -Parent
}

$stageRoot = [System.IO.Path]::GetFullPath($DeploymentRoot)
$manifestPath = Join-Path $stageRoot "deployment.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Deployment manifest was not found: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.serviceName -ne $serviceName) {
    throw "Deployment manifest is not a supported PCHelper local alpha deployment."
}
$registryBackupPath = [string]$manifest.registryBackupPath
$expectedImagePath = [string]$manifest.originalImagePath
$expectedState = [string]$manifest.originalServiceState
$invalidBackup = [string]::IsNullOrWhiteSpace($registryBackupPath) -or [string]::IsNullOrWhiteSpace($expectedImagePath) -or $expectedState -notin @("Running", "Stopped") -or -not (Test-Path -LiteralPath $registryBackupPath -PathType Leaf)
if ($invalidBackup) {
    throw "Deployment manifest does not contain a valid original service backup."
}

$service = Get-Service -Name $serviceName -ErrorAction Stop
if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $serviceName -ErrorAction Stop
    Wait-ServiceState "Stopped"
}
& reg.exe import $registryBackupPath | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Service registry restore failed with exit code $LASTEXITCODE."
}
if ((Get-PCHelperImagePath) -cne $expectedImagePath) {
    throw "Restored service ImagePath did not match the manifest."
}
if ($expectedState -eq "Running") {
    Start-Service -Name $serviceName -ErrorAction Stop
    Wait-ServiceState "Running"
}
if ((Get-Service -Name $serviceName -ErrorAction Stop).Status.ToString() -ne $expectedState) {
    throw "Service did not return to its original state '$expectedState'."
}

$manifest | Add-Member -NotePropertyName restoredAt -NotePropertyValue ([DateTimeOffset]::UtcNow) -Force
$manifest | Add-Member -NotePropertyName restoredBy -NotePropertyValue "Restore-LocalAlphaRuntime.ps1" -Force
Write-Manifest $manifest $manifestPath
$manifest
