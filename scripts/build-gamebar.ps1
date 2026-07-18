[CmdletBinding()]
param(
    [string]$Version = "0.5.5-alpha",
    [Parameter(Mandatory)]
    [string]$SigningCertificateThumbprint,
    [string]$Publisher,
    [string]$TimestampServer = "https://timestamp.digicert.com",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\gamebar"
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Game Bar output must remain inside the repository: $outputRoot"
}
if ([string]::IsNullOrWhiteSpace($TimestampServer) -or -not $TimestampServer.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Game Bar production signing requires an HTTPS RFC-3161 timestamp server."
}

function Resolve-MsBuild {
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vsWhere) {
        $installation = & $vsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installation)) {
            $candidate = Join-Path $installation.Trim() "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }
    $fallback = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $fallback) {
        return $fallback.Source
    }
    throw "MSBuild was not found. Install Visual Studio Build Tools with the UWP/MSIX workload."
}

function ConvertTo-AppxVersion([string]$InputVersion) {
    if ($InputVersion -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)(?:\.(?<revision>\d+))?(?:[-+].*)?$') {
        throw "Version must begin with three or four numeric components."
    }
    $revision = if ([string]::IsNullOrWhiteSpace($Matches.revision)) { "0" } else { $Matches.revision }
    foreach ($component in @($Matches.major, $Matches.minor, $Matches.build, $revision)) {
        if ([int64]$component -gt 65535) {
            throw "MSIX version components must be in the range 0-65535."
        }
    }
    return "$($Matches.major).$($Matches.minor).$($Matches.build).$revision"
}

function Read-PackageIdentity([string]$PackagePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "The generated MSIX has no AppxManifest.xml."
        }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }
        return [pscustomobject]@{
            Name = [string]$manifest.Package.Identity.Name
            Publisher = [string]$manifest.Package.Identity.Publisher
            Version = [string]$manifest.Package.Identity.Version
        }
    } finally {
        $archive.Dispose()
    }
}

$signing = & (Join-Path $PSScriptRoot "Test-CodeSigningPrerequisites.ps1") -CertificateThumbprint $SigningCertificateThumbprint -RequireSigning
if ($null -eq $signing -or -not $signing.Ready) {
    throw "No unambiguous valid code-signing identity is available for Game Bar packaging."
}

if ([string]::IsNullOrWhiteSpace($Publisher)) {
    $Publisher = $signing.CertificateSubject
}
if (-not [string]::Equals($Publisher, $signing.CertificateSubject, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "-Publisher must exactly match the selected code-signing certificate subject."
}

$appxVersion = ConvertTo-AppxVersion $Version
$sourceProject = Join-Path $repoRoot "src\PCHelper.GameBarWidget"
$projectFile = Join-Path $sourceProject "PCHelper.GameBarWidget.csproj"
if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "The Game Bar project was not found: $projectFile"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$workRoot = Join-Path $outputRoot ("work-" + [Guid]::NewGuid().ToString("N"))
$workProject = Join-Path $workRoot "PCHelper.GameBarWidget"
$packageOutput = Join-Path $outputRoot "package"
try {
    Copy-Item -LiteralPath $sourceProject -Destination $workProject -Recurse -Force
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $workProject "Package.appxmanifest") -Raw
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Package.Identity.Version = $appxVersion
    $manifest.Save((Join-Path $workProject "Package.appxmanifest"))

    $msbuild = Resolve-MsBuild
    New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
    $packageDirectoryProperty = [System.IO.Path]::GetFullPath($packageOutput).TrimEnd('\') + '\'
    & $msbuild (Join-Path $workProject "PCHelper.GameBarWidget.csproj") /restore /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false /p:AppxPackageDir=$packageDirectoryProperty
    if ($LASTEXITCODE -ne 0) {
        throw "Game Bar MSIX build failed. Confirm the Visual Studio UWP/MSIX workload and Xbox Game Bar SDK package restore are available."
    }

    $package = Get-ChildItem -LiteralPath $packageOutput -Recurse -File | Where-Object { $_.Extension -in ".msix", ".appx" } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $package) {
        throw "The Game Bar build produced no MSIX or APPX package."
    }
    $identity = Read-PackageIdentity $package.FullName
    if (-not [string]::Equals($identity.Name, "RigPilot.GameBarWidget", [System.StringComparison]::OrdinalIgnoreCase) -or -not [string]::Equals($identity.Publisher, $Publisher, [System.StringComparison]::OrdinalIgnoreCase) -or -not [string]::Equals($identity.Version, $appxVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The generated Game Bar package identity does not match the selected signing identity and requested version."
    }

    & $signing.SignToolPath sign /fd SHA256 /sha $signing.CertificateThumbprint /tr $TimestampServer /td SHA256 /v $package.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $($package.FullName)."
    }
    & $signing.SignToolPath verify /pa /tw /v $package.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for $($package.FullName)."
    }

    $hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $result = [pscustomobject]@{
        PackagePath = $package.FullName
        Sha256 = $hash
        PackageName = $identity.Name
        Publisher = $identity.Publisher
        Version = $identity.Version
        Message = "Signed Game Bar package created. Install it through the approved deployment path, then restart the RigPilot tray agent so it resolves the exact package SID."
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot "GameBarPackageManifest.json") -Encoding UTF8
    $result
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
