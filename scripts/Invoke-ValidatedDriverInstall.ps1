[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InfPath,

    [Parameter(Mandatory)]
    [string]$DeviceInstanceId,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedPackageSha256,

    [Parameter(Mandatory)]
    [string]$ExpectedPublisher,

    [Parameter(Mandatory)]
    [string]$ExpectedDriverVersion,

    [string]$RigPilotServiceImage,
    [string]$RollbackDirectory,
    [switch]$ConfirmExactDevice,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-DirectorySha256([string]$Directory) {
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $root = [System.IO.Path]::GetFullPath($Directory).TrimEnd($separator) + $separator
    $lines = foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length).Replace($separator, [char]'/').ToLowerInvariant()
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative`:$hash"
    }
    if ($null -eq $lines -or @($lines).Count -eq 0) {
        throw "The driver package directory contains no files."
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((@($lines) -join "`n"))
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-InfCatalogPath([string]$ResolvedInfPath) {
    $catalog = $null
    foreach ($line in Get-Content -LiteralPath $ResolvedInfPath) {
        if ($line -match '^\s*CatalogFile(?:\.[^=]+)?\s*=\s*(?<catalog>[^;\r\n]+)') {
            $catalog = $Matches.catalog.Trim()
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($catalog)) {
        throw "The driver INF has no CatalogFile declaration. Unsigned driver packages are rejected."
    }
    if ([System.IO.Path]::GetFileName($catalog) -ne $catalog) {
        throw "The driver INF has an unsafe CatalogFile path."
    }
    $path = Join-Path (Split-Path -Parent $ResolvedInfPath) $catalog
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The catalog declared by the driver INF was not found: $path"
    }
    return [System.IO.Path]::GetFullPath($path)
}

function Get-ExactDevice([string]$InstanceId) {
    $escaped = $InstanceId.Replace("'", "''")
    $devices = @(Get-CimInstance Win32_PnPEntity -Filter "PNPDeviceID = '$escaped'" |
        Where-Object { $_.PNPDeviceID -eq $InstanceId })
    if ($devices.Count -ne 1) {
        throw "The exact PnP device instance was not found or was ambiguous: $InstanceId"
    }
    return $devices[0]
}

function Get-CurrentDriver([string]$InstanceId) {
    $drivers = @(Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceID -eq $InstanceId })
    if ($drivers.Count -ne 1) {
        throw "The current signed driver for the exact device could not be determined: $InstanceId"
    }
    return $drivers[0]
}

function Test-StableExternalPower {
    $batteries = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
    if ($batteries.Count -eq 0) {
        return $true
    }
    # Win32_Battery 2/6/7/8/9 are charging or on external power. Unknown and
    # discharging states are deliberately rejected for an update operation.
    return @($batteries | Where-Object { $_.BatteryStatus -notin 2, 6, 7, 8, 9 }).Count -eq 0
}

$resolvedInf = [System.IO.Path]::GetFullPath($InfPath)
if (-not (Test-Path -LiteralPath $resolvedInf -PathType Leaf) -or
    -not [System.IO.Path]::GetExtension($resolvedInf).Equals('.inf', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "-InfPath must be an existing .inf file."
}

$packageRoot = Split-Path -Parent $resolvedInf
$catalogPath = Get-InfCatalogPath $resolvedInf
$catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalogPath
if ($catalogSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "The driver catalog is not Authenticode-valid: $($catalogSignature.Status)"
}
$publisher = $catalogSignature.SignerCertificate.Subject
if (-not [string]::Equals($publisher, $ExpectedPublisher, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The catalog signer does not match -ExpectedPublisher. Observed '$publisher'."
}

$packageHash = Get-DirectorySha256 $packageRoot
if (-not [string]::Equals($packageHash, $ExpectedPackageSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The canonical package SHA-256 does not match -ExpectedPackageSha256."
}

$device = Get-ExactDevice $DeviceInstanceId
$currentDriver = Get-CurrentDriver $DeviceInstanceId
if (-not (Test-StableExternalPower)) {
    throw "The machine is not on verified external power. Driver installation is blocked."
}

$pnputil = Join-Path $env:WINDIR "System32\pnputil.exe"
if (-not (Test-Path -LiteralPath $pnputil -PathType Leaf)) {
    throw "The Windows PnPUtil executable was not found."
}

$plan = [pscustomobject]@{
    DeviceInstanceId = $DeviceInstanceId
    DeviceName = $device.Name
    CurrentDriverVersion = $currentDriver.DriverVersion
    CurrentInfName = $currentDriver.InfName
    ExpectedDriverVersion = $ExpectedDriverVersion
    InfPath = $resolvedInf
    CatalogPath = $catalogPath
    Publisher = $publisher
    PackageSha256 = $packageHash
    WillApply = [bool]$Apply
    RollbackDirectory = if ([string]::IsNullOrWhiteSpace($RollbackDirectory)) {
        Join-Path $env:ProgramData ("RigPilot\DriverRollback\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    } else {
        [System.IO.Path]::GetFullPath($RollbackDirectory)
    }
}

if (-not $Apply) {
    $plan
    return
}

if (-not $ConfirmExactDevice) {
    throw "-Apply requires -ConfirmExactDevice after reviewing the exact device, package hash, publisher, and rollback destination."
}
if ([string]::IsNullOrWhiteSpace($RigPilotServiceImage) -or -not (Test-Path -LiteralPath $RigPilotServiceImage -PathType Leaf)) {
    throw "-Apply requires -RigPilotServiceImage pointing to the installed signed service assembly."
}
$serviceSignature = Get-AuthenticodeSignature -LiteralPath $RigPilotServiceImage
if ($serviceSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "The supplied RigPilot service image is not Authenticode-valid. Driver installation is blocked."
}
if ([string]::IsNullOrWhiteSpace($currentDriver.InfName) -or $currentDriver.InfName -notmatch '^oem\d+\.inf$') {
    throw "The current driver has no exportable third-party OEM INF. Automatic rollback is unavailable."
}

New-Item -ItemType Directory -Path $plan.RollbackDirectory -Force | Out-Null
& $pnputil /export-driver $currentDriver.InfName $plan.RollbackDirectory
if ($LASTEXITCODE -ne 0) {
    throw "PnPUtil failed to export the current driver package; installation was not started."
}

try {
    & $pnputil /add-driver $resolvedInf /install
    if ($LASTEXITCODE -notin 0, 3010, 1641) {
        throw "PnPUtil failed with exit code $LASTEXITCODE."
    }

    if ($LASTEXITCODE -eq 0) {
        $updatedDriver = Get-CurrentDriver $DeviceInstanceId
        if (-not [string]::Equals($updatedDriver.DriverVersion, $ExpectedDriverVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The installed driver version '$($updatedDriver.DriverVersion)' did not match '$ExpectedDriverVersion'."
        }
    }

    $plan | Add-Member -NotePropertyName Result -NotePropertyValue (
        if ($LASTEXITCODE -eq 0) { "Installed and verified." } else { "PnPUtil requested reboot (exit $LASTEXITCODE). Verify the version after reboot." })
    $plan
}
catch {
    $rollbackInfs = @(Get-ChildItem -LiteralPath $plan.RollbackDirectory -Recurse -Filter *.inf -File)
    if ($rollbackInfs.Count -gt 0) {
        & $pnputil /add-driver (Join-Path $plan.RollbackDirectory "*.inf") /subdirs /install
    }
    throw
}
