[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\dependencies"
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Dependency output must remain inside the repository: $outputRoot"
}

$metadata = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json"
$release = if ([string]::IsNullOrWhiteSpace($Version)) {
    $metadata.releases | Select-Object -First 1
} else {
    $metadata.releases | Where-Object { $_.'release-version' -eq $Version } | Select-Object -First 1
}
if ($null -eq $release) {
    throw "No .NET 10 release metadata matched '$Version'."
}

$file = $release.windowsdesktop.files |
    Where-Object { $_.rid -eq "win-x64" -and $_.url -like "*.exe" } |
    Select-Object -First 1
if ($null -eq $file) {
    throw "The release metadata did not contain a Windows Desktop Runtime x64 executable."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$fileName = [System.IO.Path]::GetFileName(([Uri]$file.url).AbsolutePath)
$destination = Join-Path $outputRoot $fileName
Invoke-WebRequest -Uri $file.url -OutFile $destination

$actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA512).Hash.ToLowerInvariant()
$expectedHash = ([string]$file.hash).ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    Remove-Item -LiteralPath $destination -Force
    throw "The downloaded .NET Desktop Runtime failed SHA-512 verification."
}

Write-Host "Verified .NET $($release.'release-version') Desktop Runtime: $actualHash"
Write-Output $destination
