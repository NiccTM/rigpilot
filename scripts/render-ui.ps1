[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory,
    [ValidateRange(960, 3840)]
    [int]$Width = 1240,
    [ValidateRange(640, 2160)]
    [int]$Height = 800
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\ui-snapshots"
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "UI snapshot output must remain inside the repository: $outputRoot"
}

$localDotnet = Join-Path $HOME ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$project = Join-Path $repoRoot "tools\PCHelper.UiSnapshot\PCHelper.UiSnapshot.csproj"

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
& $dotnet build $project --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "The UI snapshot host did not build."
}

for ($page = 0; $page -lt 9; $page++) {
    & $dotnet run `
        --project $project `
        --configuration $Configuration `
        --no-build `
        -- `
        $outputRoot `
        $page `
        $Width `
        $Height
    if ($LASTEXITCODE -ne 0) {
        throw "UI snapshot rendering failed for page index $page."
    }
}

Write-Host "Rendered all RigPilot pages to $outputRoot"
