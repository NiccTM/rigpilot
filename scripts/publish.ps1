[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.2.0",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\publish"
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must remain inside the repository: $outputRoot"
}

$localDotnet = Join-Path $HOME ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$projects = [ordered]@{
    "app" = "src\PCHelper.App\PCHelper.App.csproj"
    "service" = "src\PCHelper.Service\PCHelper.Service.csproj"
    "adapter-host" = "src\PCHelper.AdapterHost\PCHelper.AdapterHost.csproj"
    "cli" = "src\PCHelper.Cli\PCHelper.Cli.csproj"
}

foreach ($entry in $projects.GetEnumerator()) {
    $projectPath = Join-Path $repoRoot $entry.Value
    $destination = Join-Path $outputRoot $entry.Key
    & $dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained false `
        --output $destination `
        -p:Version=$Version `
        -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($entry.Value)."
    }
}

Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "COMPATIBILITY.md") -Destination $outputRoot

Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object {
        $relativePath = $_.Path.Substring($outputRoot.Length).TrimStart('\')
        "{0} *{1}" -f $_.Hash.ToLowerInvariant(), $relativePath
    } |
    Set-Content -LiteralPath (Join-Path $outputRoot "SHA256SUMS.txt") -Encoding UTF8

Write-Host "Published PC Helper $Version to $outputRoot"
