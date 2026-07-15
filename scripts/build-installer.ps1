[CmdletBinding()]
param(
    [string]$Version = "0.4.0",
    [string]$RuntimeInstaller,
    [switch]$SkipPublish,
    [switch]$SkipMsiBuild,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer = "https://timestamp.digicert.com",
    [switch]$RequireSigning
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$localDotnet = Join-Path $HOME ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }

$signing = $null
if ($RequireSigning -or -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    if ([string]::IsNullOrWhiteSpace($TimestampServer) -or -not $TimestampServer.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production signing requires an HTTPS RFC-3161 timestamp server."
    }
    $signing = & (Join-Path $PSScriptRoot "Test-CodeSigningPrerequisites.ps1") -CertificateThumbprint $SigningCertificateThumbprint
    if ($null -eq $signing -or -not $signing.Ready) {
        throw "No unambiguous valid code-signing identity is available for installer signing."
    }
}

function Sign-InstallerArtifact([string]$Path) {
    if ($null -eq $signing) {
        return
    }
    & $signing.SignToolPath sign /fd SHA256 /sha $signing.CertificateThumbprint /tr $TimestampServer /td SHA256 /v $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $Path"
    }
    & $signing.SignToolPath verify /pa /tw /v $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for $Path"
    }
}

if (-not $SkipPublish) {
    $publishArguments = @{ Version = $Version }
    if ($null -ne $signing) {
        $publishArguments.SigningCertificateThumbprint = $signing.CertificateThumbprint
        $publishArguments.TimestampServer = $TimestampServer
        $publishArguments.RequireSigning = $true
    }
    & (Join-Path $PSScriptRoot "publish.ps1") @publishArguments
}

$publishDirectory = Join-Path $repoRoot "artifacts\publish"
$runtimePayloadCheck = Join-Path $PSScriptRoot "Test-RuntimePayload.ps1"
& $runtimePayloadCheck -PayloadRoot $publishDirectory -ExpectedProductVersion $Version
if ($LASTEXITCODE -ne 0) {
    throw "Runtime payload contract verification failed."
}
$installerOutput = Join-Path $repoRoot "artifacts\installer"
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

if (-not $SkipMsiBuild) {
    $installerProject = Join-Path $repoRoot "installer\PCHelper.Installer.wixproj"
    & $dotnet restore $installerProject --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed for the MSI project."
    }

    & $dotnet build $installerProject `
        --configuration Release `
        --no-restore `
        -p:ProductVersion=$Version `
        -p:PublishDir=$publishDirectory `
        -p:OutputPath=$installerOutput
    if ($LASTEXITCODE -ne 0) {
        throw "MSI build failed."
    }
}

$msiPath = Get-ChildItem -LiteralPath $installerOutput -Filter "*.msi" -Recurse |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($msiPath)) {
    throw "The MSI output could not be located."
}
Sign-InstallerArtifact $msiPath

if (-not [string]::IsNullOrWhiteSpace($RuntimeInstaller)) {
    $runtimePath = [System.IO.Path]::GetFullPath($RuntimeInstaller)
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw "The .NET Desktop Runtime installer does not exist: $runtimePath"
    }

    $bundleProject = Join-Path $repoRoot "installer\PCHelper.Bundle.wixproj"
    & $dotnet restore $bundleProject --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed for the bundle project."
    }

    & $dotnet build $bundleProject `
        --configuration Release `
        --no-restore `
        -p:ProductVersion=$Version `
        -p:RuntimeInstaller=$runtimePath `
        -p:MsiPath=$msiPath `
        -p:OutputPath=$installerOutput
    if ($LASTEXITCODE -ne 0) {
        throw "WiX bundle build failed."
    }

    $bundlePath = Get-ChildItem -LiteralPath $installerOutput -Filter "*.exe" -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($bundlePath)) {
        throw "The bundle output could not be located."
    }
    Sign-InstallerArtifact $bundlePath
}

if ($SkipMsiBuild -and [string]::IsNullOrWhiteSpace($RuntimeInstaller)) {
    throw "-SkipMsiBuild is valid only when building a bundle around an existing MSI."
}

Write-Host "Installer artifacts are in $installerOutput"
