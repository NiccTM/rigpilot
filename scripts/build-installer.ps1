[CmdletBinding()]
param(
    [string]$Version = "0.2.0",
    [string]$RuntimeInstaller,
    [switch]$SkipPublish,
    [switch]$SkipMsiBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$localDotnet = Join-Path $HOME ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish.ps1") -Version $Version
}

$publishDirectory = Join-Path $repoRoot "artifacts\publish"
$installerOutput = Join-Path $repoRoot "artifacts\installer"
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

if (-not $SkipMsiBuild) {
    & $dotnet build (Join-Path $repoRoot "installer\PCHelper.Installer.wixproj") `
        --configuration Release `
        -p:ProductVersion=$Version `
        -p:PublishDir=$publishDirectory `
        -p:OutputPath=$installerOutput
    if ($LASTEXITCODE -ne 0) {
        throw "MSI build failed."
    }
}

if (-not [string]::IsNullOrWhiteSpace($RuntimeInstaller)) {
    $runtimePath = [System.IO.Path]::GetFullPath($RuntimeInstaller)
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw "The .NET Desktop Runtime installer does not exist: $runtimePath"
    }

    $msiPath = Get-ChildItem -LiteralPath $installerOutput -Filter "*.msi" -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($msiPath)) {
        throw "The MSI output could not be located."
    }

    & $dotnet build (Join-Path $repoRoot "installer\PCHelper.Bundle.wixproj") `
        --configuration Release `
        -p:ProductVersion=$Version `
        -p:RuntimeInstaller=$runtimePath `
        -p:MsiPath=$msiPath `
        -p:OutputPath=$installerOutput
    if ($LASTEXITCODE -ne 0) {
        throw "WiX bundle build failed."
    }
}

if ($SkipMsiBuild -and [string]::IsNullOrWhiteSpace($RuntimeInstaller)) {
    throw "-SkipMsiBuild is valid only when building a bundle around an existing MSI."
}

Write-Host "Installer artifacts are in $installerOutput"
