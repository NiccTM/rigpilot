[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PayloadRoot,
    [string]$DeploymentRoot,
    [ValidateRange(10, 90)]
    [int]$ServiceTimeoutSeconds = 45
)

<#
.SYNOPSIS
Safely replaces an active local RigPilot alpha runtime with a newer validated payload.

.DESCRIPTION
This development-only helper uses the existing reversible deployment contract:
when PCHelper currently points to a LocalAlpha service it restores the exact
registry backup first, then stages the requested validated payload through
Install-LocalAlphaRuntime.ps1. It never overwrites Program Files, drivers,
firmware, or the saved alpha payloads. Both child scripts require normal UAC.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PCHelperImagePath {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\PCHelper", $false)
    if ($null -eq $key) {
        throw "The PCHelper service registry key was not found."
    }

    try {
        $value = $key.GetValue("ImagePath", $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ([string]::IsNullOrWhiteSpace([string]$value)) {
            throw "The PCHelper service ImagePath is missing."
        }
        return [string]$value
    }
    finally {
        $key.Dispose()
    }
}

if (-not (Test-Administrator)) {
    throw "Administrator rights are required. Start this script through Windows UAC."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$payload = [System.IO.Path]::GetFullPath($PayloadRoot)
if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
    throw "Runtime payload does not exist: $payload"
}

$restoreScript = Join-Path $PSScriptRoot "Restore-LocalAlphaRuntime.ps1"
$installScript = Join-Path $PSScriptRoot "Install-LocalAlphaRuntime.ps1"
if (-not (Test-Path -LiteralPath $restoreScript -PathType Leaf) -or -not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
    throw "The local alpha deployment scripts are incomplete."
}

$previousImagePath = Get-PCHelperImagePath
$restored = $false
if ($previousImagePath -match '(?i)\\RigPilot\\LocalAlpha\\') {
    # The child script guards its own reg.exe stderr, but under this script's
    # ErrorActionPreference=Stop a stray native-stderr ErrorRecord crossing the
    # script boundary still terminates a restore that actually worked. Run the
    # child with Continue; a real `throw` from the child still propagates, and
    # the deterministic post-condition below decides success.
    & {
        $ErrorActionPreference = "Continue"
        & $restoreScript -ServiceTimeoutSeconds $ServiceTimeoutSeconds
    } | Out-Null
    if ((Get-PCHelperImagePath) -match '(?i)\\RigPilot\\LocalAlpha\\') {
        throw "Restore did not return the PCHelper service to its original image path."
    }
    $restored = $true
}

$installArguments = @{
    PayloadRoot = $payload
    ServiceTimeoutSeconds = $ServiceTimeoutSeconds
}
if (-not [string]::IsNullOrWhiteSpace($DeploymentRoot)) {
    $installArguments.DeploymentRoot = $DeploymentRoot
}

$reinstalledPreviousAlpha = $false
try {
    $deployment = & $installScript @installArguments
}
catch {
    # The restore-first step has already returned the service to the original
    # Program Files image, so a failed install would otherwise strand the
    # machine on the old runtime ("rolled back" from the operator's point of
    # view). The previously active LocalAlpha payload directory is retained on
    # disk, so best-effort re-install it before surfacing the failure.
    $installError = $_
    if ($restored -and $previousImagePath -match '(?i)^"?(?<exe>.+?PCHelper\.Service\.exe)') {
        $previousPayload = Split-Path -Parent (Split-Path -Parent $Matches.exe)
        if (Test-Path -LiteralPath (Join-Path $previousPayload "service\PCHelper.Service.exe") -PathType Leaf) {
            try {
                & $installScript -PayloadRoot $previousPayload -ServiceTimeoutSeconds $ServiceTimeoutSeconds | Out-Null
                $reinstalledPreviousAlpha = (Get-PCHelperImagePath) -match '(?i)\\RigPilot\\LocalAlpha\\'
            }
            catch {
                # Fall through: the original registry backup restore already ran.
            }
        }
    }
    if ($reinstalledPreviousAlpha) {
        throw "Installing the new payload failed and the previously active LocalAlpha runtime was re-installed instead: $installError"
    }
    throw
}

[pscustomobject]@{
    PreviousImagePath = $previousImagePath
    RestoredExistingLocalAlpha = $restored
    ReinstalledPreviousAlphaAfterFailure = $reinstalledPreviousAlpha
    Deployment = $deployment
}
