[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256,

    [Parameter(Mandatory)]
    [string]$ExpectedPublisher,

    [Parameter(Mandatory)]
    [string]$ExactDeviceId,

    [Parameter(Mandatory)]
    [string]$RecoveryMethod
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedPath = [System.IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
    throw "Firmware package was not found."
}
if ([string]::IsNullOrWhiteSpace($RecoveryMethod)) {
    throw "A documented vendor recovery method is required."
}
$signature = Get-AuthenticodeSignature -LiteralPath $resolvedPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "The vendor firmware package is not Authenticode-valid: $($signature.Status)"
}
if (-not [string]::Equals($signature.SignerCertificate.Subject, $ExpectedPublisher, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Firmware package signer does not match -ExpectedPublisher."
}
$hash = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash
if (-not [string]::Equals($hash, $ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Firmware package SHA-256 does not match -ExpectedSha256."
}
$escapedDeviceId = $ExactDeviceId.Replace("'", "''")
$device = Get-CimInstance Win32_PnPEntity -Filter "PNPDeviceID = '$escapedDeviceId'" |
    Where-Object { $_.PNPDeviceID -eq $ExactDeviceId } |
    Select-Object -First 1
if ($null -eq $device) {
    throw "Exact firmware target device was not found."
}

[pscustomobject]@{
    PackagePath = $resolvedPath
    PackageSha256 = $hash.ToLowerInvariant()
    Publisher = $signature.SignerCertificate.Subject
    ExactDeviceId = $ExactDeviceId
    DeviceName = $device.Name
    RecoveryMethod = $RecoveryMethod
    ApplyBlocked = $true
    Message = "Preflight passed. RigPilot intentionally has no generic firmware writer; use only the exact model's vendor-signed updater, ESRT capsule, or documented UEFI workflow after separate BitLocker and power checks."
}
