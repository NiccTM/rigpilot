[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PayloadRoot,
    [string]$ExpectedProductVersion,
    [switch]$RequireServiceWritesLocked
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-MajorMinor([string]$Value) {
    $match = [System.Text.RegularExpressions.Regex]::Match($Value, '^\s*(\d+)\.(\d+)')
    if (-not $match.Success) {
        throw "A major.minor version prefix is required: $Value"
    }
    return "$($match.Groups[1].Value).$($match.Groups[2].Value)"
}

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
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Runtime payload root does not exist: $root"
}

$contractPath = Join-Path $root "runtime-contract.json"
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw "Runtime payload contract is missing: $contractPath"
}

$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
if ($contract.schemaVersion -ne 1) {
    throw "Unsupported runtime contract schema: $($contract.schemaVersion)"
}
if ([string]$contract.product -ne "RigPilot") {
    throw "Runtime contract product identity is invalid: $($contract.product)"
}
if ([int]$contract.protocolVersion -ne 2) {
    throw "Runtime contract protocol version is invalid: $($contract.protocolVersion)"
}
if ($RequireServiceWritesLocked -and -not [bool]$contract.releaseTrust.serviceWritesLocked) {
    throw "The runtime payload is not build-locked for an unsigned public preview."
}
if ($RequireServiceWritesLocked) {
    $servicePath = Resolve-ContainedFile $root "service\PCHelper.Service.exe"
    $policyOutput = (& $servicePath --print-release-policy | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $policyOutput -ne "publicUnsignedPreview=true;writesAllowed=false") {
        throw "The published service does not report the required preview write lock: $policyOutput"
    }
}
if ([string]::IsNullOrWhiteSpace([string]$contract.productVersion)) {
    throw "Runtime contract productVersion is required."
}
$expectedMajorMinor = Get-MajorMinor ([string]$contract.productVersion)
if ((-not [string]::IsNullOrWhiteSpace($ExpectedProductVersion)) -and ((Get-MajorMinor $ExpectedProductVersion) -ne $expectedMajorMinor)) {
    throw "Runtime payload $($contract.productVersion) does not match installer release line $ExpectedProductVersion."
}
$components = @($contract.components)
if ($components.Count -eq 0) {
    throw "Runtime contract has no component records."
}

$requiredFeatures = @("service-status", "capability-v2", "fan-commissioning", "fan-calibrations", "reliability", "adapter-trace", "cooling-output-roles", "release-write-policy")
$advertisedFeatures = @($contract.requiredServiceFeatures | ForEach-Object { [string]$_ })
foreach ($requiredFeature in $requiredFeatures) {
    if ($advertisedFeatures -notcontains $requiredFeature) {
        throw "Runtime contract is missing required service feature '$requiredFeature'."
    }
}

$requiredIds = @("app", "service", "adapter-host", "automation-host", "effect-host", "cli")
$actualIds = @($components | ForEach-Object { [string]$_.id })
foreach ($requiredId in $requiredIds) {
    if ($actualIds -notcontains $requiredId) {
        throw "Runtime contract is missing required component '$requiredId'."
    }
}
if (@($actualIds | Select-Object -Unique).Count -ne $actualIds.Count) {
    throw "Runtime contract contains duplicate component IDs."
}

foreach ($component in $components) {
    $id = [string]$component.id
    $relativePath = [string]$component.relativePath
    $expectedHash = [string]$component.sha256
    $expectedFileVersion = [string]$component.fileVersion
    if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($relativePath)) {
        throw "Runtime contract has an incomplete component record."
    }
    if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Runtime contract component '$id' has an invalid SHA-256."
    }

    $file = Resolve-ContainedFile $root $relativePath
    $actualHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if (-not [string]::Equals($expectedHash, $actualHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime component hash mismatch: $relativePath"
    }

    $actualFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file).FileVersion
    if ([string]::IsNullOrWhiteSpace($actualFileVersion) -or [string]::IsNullOrWhiteSpace($expectedFileVersion)) {
        throw "Runtime component '$id' has no comparable file version."
    }
    if (-not [string]::Equals($expectedFileVersion, $actualFileVersion, [System.StringComparison]::Ordinal)) {
        throw "Runtime component file version changed after contract generation: $relativePath"
    }
    if ((Get-MajorMinor $actualFileVersion) -ne $expectedMajorMinor) {
        throw "Runtime component major/minor does not match product version: $relativePath ($actualFileVersion)"
    }
}

[pscustomobject]@{
    PayloadRoot = $root
    ProductVersion = [string]$contract.productVersion
    ExpectedProductVersion = $ExpectedProductVersion
    ProtocolVersion = [int]$contract.protocolVersion
    Components = $components.Count
    Passed = $true
}
