[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$TemplateRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Output
)

<#
.SYNOPSIS
Builds an UNSIGNED RigPilot adapter pack (.pcha) from an SDK template directory.

.DESCRIPTION
Reads <TemplateRoot>\manifest.template.json, hashes every file under
<TemplateRoot>\payload\ into payloadHashes, writes the final manifest.json, and
zips manifest + payload into the .pcha archive. It performs NO signing: the
archive fails publisher-signature verification by design and can only be
installed through the explicit development-trust route (PCHELPER_DEV_PACK_HASHES
on a DEBUG service build). See docs/adapter-pack-sdk.md.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$templatePath = [System.IO.Path]::GetFullPath($TemplateRoot)
$manifestTemplate = Join-Path $templatePath "manifest.template.json"
$payloadRoot = Join-Path $templatePath "payload"
if (-not (Test-Path -LiteralPath $manifestTemplate -PathType Leaf)) {
    throw "manifest.template.json not found under $templatePath"
}
if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
    throw "payload directory not found under $templatePath"
}

$outputPath = [System.IO.Path]::GetFullPath($Output)
if (-not $outputPath.EndsWith(".pcha", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Adapter packs must use the .pcha extension."
}
if (Test-Path -LiteralPath $outputPath) {
    throw "Output already exists: $outputPath. Remove it first."
}

$manifest = Get-Content -LiteralPath $manifestTemplate -Raw | ConvertFrom-Json

# Hash every payload file (forward-slash relative paths, lower-case hex SHA-256).
$payloadHashes = [ordered]@{}
$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File)
if ($payloadFiles.Count -eq 0) {
    throw "The payload directory is empty; a pack must ship at least its entry point."
}
foreach ($file in $payloadFiles) {
    $relative = "payload/" + $file.FullName.Substring($payloadRoot.Length + 1).Replace("\", "/")
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $payloadHashes[$relative] = $hash
}

$entryPoint = [string]$manifest.entryPoint
if (-not $payloadHashes.Contains($entryPoint)) {
    throw "entryPoint '$entryPoint' is not among the payload files: $($payloadHashes.Keys -join ', ')"
}

# Rebuild the manifest with the computed hashes, preserving declared fields.
$final = [ordered]@{}
foreach ($property in $manifest.PSObject.Properties) {
    if ($property.Name -ne "payloadHashes") {
        $final[$property.Name] = $property.Value
    }
}
$final["payloadHashes"] = $payloadHashes
$manifestJson = ($final | ConvertTo-Json -Depth 8)

# Assemble the archive in a staging directory so entry paths are exact.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("pcha-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    [System.IO.File]::WriteAllText((Join-Path $staging "manifest.json"), $manifestJson)
    Copy-Item -LiteralPath $payloadRoot -Destination (Join-Path $staging "payload") -Recurse

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging, $outputPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

$packageHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    Package = $outputPath
    PackageSha256 = $packageHash
    DevelopmentTrust = "`$env:PCHELPER_DEV_PACK_HASHES = `"$packageHash`" (DEBUG service builds only)"
    Signed = $false
    Note = "Unsigned by design; production signing happens through the publisher signing service after containment tests."
}
