[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.5.0-alpha",
    [string]$OutputDirectory,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer = "https://timestamp.digicert.com",
    [switch]$RequireSigning
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

function Get-MajorMinor([string]$Value) {
    $match = [System.Text.RegularExpressions.Regex]::Match($Value, '^\s*(\d+)\.(\d+)')
    if (-not $match.Success) {
        throw "A major.minor version prefix is required: $Value"
    }
    return "$($match.Groups[1].Value).$($match.Groups[2].Value)"
}

$localDotnet = Join-Path $HOME ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

function Resolve-SignTool {
    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $kitRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitRoot -Recurse -Filter signtool.exe -File |
            Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

function Test-CodeSigningEku([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -ne "2.5.29.37") {
            continue
        }
        $usages = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
            $extension.RawData,
            $extension.Critical).EnhancedKeyUsages
        return @($usages | Where-Object { $_.Value -eq $codeSigningOid }).Count -gt 0
    }
    return $false
}

function Get-ReleaseSigningCertificate([string]$Thumbprint) {
    $now = Get-Date
    $normalisedThumbprint = $Thumbprint -replace "\s", ""
    $candidates = @(Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.HasPrivateKey -and
            $_.NotBefore -le $now -and
            $_.NotAfter -gt $now -and
            (Test-CodeSigningEku $_) -and
            ([string]::IsNullOrWhiteSpace($normalisedThumbprint) -or $_.Thumbprint -eq $normalisedThumbprint)
        } |
        Sort-Object NotAfter -Descending)
    if ($candidates.Count -eq 0) {
        return $null
    }
    if (-not [string]::IsNullOrWhiteSpace($normalisedThumbprint)) {
        return $candidates[0]
    }
    if ($candidates.Count -gt 1) {
        throw "More than one valid code-signing certificate was found. Specify -SigningCertificateThumbprint explicitly."
    }
    return $candidates[0]
}

$signingCertificate = $null
$signTool = $null
if ($RequireSigning -or -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    if ([string]::IsNullOrWhiteSpace($TimestampServer) -or -not $TimestampServer.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production signing requires an HTTPS RFC-3161 timestamp server."
    }
    $signingCertificate = Get-ReleaseSigningCertificate $SigningCertificateThumbprint
    if ($null -eq $signingCertificate) {
        throw "No current code-signing certificate with a private key and the Code Signing EKU was found. Production publish is blocked."
    }
    $signTool = Resolve-SignTool
    if ([string]::IsNullOrWhiteSpace($signTool)) {
        throw "signtool.exe was not found. Install the Windows SDK signing tools before publishing a production package."
    }
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$projects = [ordered]@{
    "app" = "src\PCHelper.App\PCHelper.App.csproj"
    "service" = "src\PCHelper.Service\PCHelper.Service.csproj"
    "adapter-host" = "src\PCHelper.AdapterHost\PCHelper.AdapterHost.csproj"
    "automation-host" = "src\PCHelper.AutomationHost\PCHelper.AutomationHost.csproj"
    "effect-host" = "src\PCHelper.EffectHost\PCHelper.EffectHost.csproj"
    "cli" = "src\PCHelper.Cli\PCHelper.Cli.csproj"
}

$runtimeExecutables = [ordered]@{
    "app" = "app\PCHelper.App.exe"
    "service" = "service\PCHelper.Service.exe"
    "adapter-host" = "adapter-host\PCHelper.AdapterHost.exe"
    "automation-host" = "automation-host\PCHelper.AutomationHost.exe"
    "effect-host" = "effect-host\PCHelper.EffectHost.exe"
    "cli" = "cli\pchelper-cli.exe"
}

foreach ($entry in $projects.GetEnumerator()) {
    $projectPath = Join-Path $repoRoot $entry.Value
    $destination = Join-Path $outputRoot $entry.Key
    & $dotnet restore $projectPath `
        --runtime $Runtime `
        --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed for $($entry.Value)."
    }

    & $dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained false `
        --no-restore `
        --output $destination `
        -p:Version=$Version `
        -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($entry.Value)."
    }
}

if ($null -ne $signingCertificate) {
    $signingTargets = Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
        Where-Object {
            $_.Extension -in ".exe", ".dll" -and
            ($_.Name -like "PCHelper.*" -or $_.Name -like "pchelper-cli.*")
        } |
        Sort-Object FullName
    if ($signingTargets.Count -eq 0) {
        throw "No RigPilot binaries were found to sign."
    }

    foreach ($target in $signingTargets) {
        & $signTool sign /fd SHA256 /sha $signingCertificate.Thumbprint /tr $TimestampServer /td SHA256 /v $target.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed for $($target.FullName)."
        }
        & $signTool verify /pa /tw /v $target.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode verification failed for $($target.FullName)."
        }
    }
}

Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "COMPATIBILITY.md") -Destination $outputRoot

$expectedMajorMinor = Get-MajorMinor $Version
$runtimeComponents = foreach ($entry in $runtimeExecutables.GetEnumerator()) {
    $relativePath = $entry.Value
    $fullPath = Join-Path $outputRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Published runtime component is missing: $relativePath"
    }

    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath).FileVersion
    if ([string]::IsNullOrWhiteSpace($fileVersion)) {
        throw "Published runtime component has no file version: $relativePath"
    }
    if ((Get-MajorMinor $fileVersion) -ne $expectedMajorMinor) {
        throw "Published runtime component version does not match ${Version}: $relativePath ($fileVersion)"
    }

    [ordered]@{
        id = $entry.Key
        relativePath = $relativePath.Replace('\', '/')
        fileVersion = $fileVersion
        sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$runtimeContract = [ordered]@{
    schemaVersion = 1
    product = "RigPilot"
    productVersion = $Version
    protocolVersion = 2
    requiredServiceFeatures = @(
        "service-status",
        "capability-v2",
        "fan-commissioning",
        "fan-calibrations",
        "reliability",
        "adapter-trace",
        "cooling-output-roles"
    )
    components = @($runtimeComponents)
}
$runtimeContract | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot "runtime-contract.json") -Encoding UTF8

Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object {
        $relativePath = $_.Path.Substring($outputRoot.Length).TrimStart('\')
        "{0} *{1}" -f $_.Hash.ToLowerInvariant(), $relativePath
    } |
    Set-Content -LiteralPath (Join-Path $outputRoot "SHA256SUMS.txt") -Encoding UTF8

if ($null -ne $signingCertificate) {
    Write-Host "Published signed RigPilot $Version to $outputRoot"
} else {
    Write-Host "Published unsigned development RigPilot $Version to $outputRoot. Automatic takeover remains hard-blocked."
}
