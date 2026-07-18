[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PayloadRoot,
    [switch]$RequireSigning,
    [ValidateSet("SignPathSignedRelease", "PfxSignedRelease", "UnsignedDevelopment", "PublicUnsignedPreview")]
    [string]$Policy = "SignPathSignedRelease"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-ContainedFile([string]$Root, [string]$RelativePath) {
    $normalised = $RelativePath.Replace('/', '\')
    if ([System.IO.Path]::IsPathRooted($normalised) -or $normalised.Split('\') -contains '..') {
        throw "Runtime contract component path is not contained: $RelativePath"
    }
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $normalised))
    $rootPrefix = $Root.TrimEnd('\') + '\'
    if (-not $candidate.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime contract component escapes payload root: $RelativePath"
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Runtime contract component is missing: $RelativePath"
    }
    return $candidate
}

$root = [System.IO.Path]::GetFullPath($PayloadRoot)
$contractPath = Join-Path $root "runtime-contract.json"
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw "Runtime payload contract is missing: $contractPath"
}

$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ([int]$contract.schemaVersion -ne 1 -or [string]$contract.product -ne "RigPilot") {
    throw "Only the RigPilot runtime-contract schema 1 can be refreshed."
}

foreach ($component in @($contract.components)) {
    $file = Resolve-ContainedFile $root ([string]$component.relativePath)
    if ($RequireSigning) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Runtime component is not Authenticode-valid: $($component.relativePath) ($($signature.Status))"
        }
    }
    $component.fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file).FileVersion
    $component.sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
}

$signed = $Policy -in "SignPathSignedRelease", "PfxSignedRelease"
$locked = $Policy -eq "PublicUnsignedPreview"
$contract.releaseTrust.signed = $signed
$contract.releaseTrust.serviceWritesLocked = $locked
$contract.releaseTrust.policy = $Policy
$contract | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $contractPath -Encoding UTF8

& (Join-Path $PSScriptRoot "Test-RuntimePayload.ps1") `
    -PayloadRoot $root `
    -ExpectedProductVersion ([string]$contract.productVersion) `
    -RequireServiceWritesLocked:$locked
