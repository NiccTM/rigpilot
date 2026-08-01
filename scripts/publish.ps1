[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [string]$OutputDirectory,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer = "https://timestamp.digicert.com",
    [switch]$RequireSigning,
    [switch]$LockServiceWrites
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = & (Join-Path $PSScriptRoot "Get-ProductVersion.ps1") -IncludeSuffix
}
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

. (Join-Path $PSScriptRoot "PayloadPromotion.ps1")

$projects = [ordered]@{
    "app" = "src\PCHelper.App\PCHelper.App.csproj"
    "service" = "src\PCHelper.Service\PCHelper.Service.csproj"
    "adapter-host" = "src\PCHelper.AdapterHost\PCHelper.AdapterHost.csproj"
    "automation-host" = "src\PCHelper.AutomationHost\PCHelper.AutomationHost.csproj"
    "workload-host" = "src\PCHelper.WorkloadHost\PCHelper.WorkloadHost.csproj"
    "effect-host" = "src\PCHelper.EffectHost\PCHelper.EffectHost.csproj"
    "cli" = "src\PCHelper.Cli\PCHelper.Cli.csproj"
}

$runtimeExecutables = [ordered]@{
    "app" = "app\PCHelper.App.exe"
    "service" = "service\PCHelper.Service.exe"
    "adapter-host" = "adapter-host\PCHelper.AdapterHost.exe"
    "automation-host" = "automation-host\PCHelper.AutomationHost.exe"
    "workload-host" = "workload-host\PCHelper.WorkloadHost.exe"
    "effect-host" = "effect-host\PCHelper.EffectHost.exe"
    "cli" = "cli\pchelper-cli.exe"
}


# The payload is built into a staging directory and promoted over the destination
# only once it is complete and validated. Building straight into the destination
# meant clearing the last known-good payload first, which destroyed it outright
# whenever a RigPilot process was running from that directory: the delete stopped
# on the locked file and left a half-removed tree behind. Nothing here touches the
# destination until Invoke-PayloadReplacement has proven it replaceable.
$buildStaging = {
    param([string]$stagingRoot)

    foreach ($entry in $projects.GetEnumerator()) {
        $projectPath = Join-Path $repoRoot $entry.Value
        $projectOutput = Join-Path $stagingRoot $entry.Key
        & $dotnet restore $projectPath `
            --runtime $Runtime `
            --locked-mode
        if ($LASTEXITCODE -ne 0) {
            throw "Locked restore failed for $($entry.Value)."
        }

        $writeLockProperty = if ($LockServiceWrites -and $entry.Key -eq "service") { "true" } else { "false" }
        & $dotnet publish $projectPath `
            --configuration $Configuration `
            --runtime $Runtime `
            --self-contained false `
            --no-restore `
            --output $projectOutput `
            -p:Version=$Version `
            -p:RigPilotPublicUnsignedPreview=$writeLockProperty `
            -p:ContinuousIntegrationBuild=true
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $($entry.Value)."
        }
    }

    # Reject links before anything walks the tree. Signing and hashing below both
    # use Get-ChildItem -Recurse, which follows a junction or directory symbolic
    # link; a stray link in a project output would put signtool and Get-FileHash
    # on files outside the payload. The shared validator from PayloadPromotion.ps1
    # is used so this is the same rule the promotion step enforces, not a second
    # implementation that could drift from it.
    Assert-PayloadFreeOfReparsePoint -Root $stagingRoot -Stage "in the staged payload after the build"

    if ($null -ne $signingCertificate) {
        $signingTargets = Get-ChildItem -LiteralPath $stagingRoot -Recurse -File |
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

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $stagingRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination $stagingRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "COMPATIBILITY.md") -Destination $stagingRoot

    # Re-checked after signing and the licence copies, because the contract and
    # SHA256SUMS.txt below are generated by walking this tree and must describe
    # only files that are genuinely inside the payload.
    Assert-PayloadFreeOfReparsePoint -Root $stagingRoot -Stage "in the staged payload before the runtime contract is generated"

    $expectedMajorMinor = Get-MajorMinor $Version
    $runtimeComponents = foreach ($entry in $runtimeExecutables.GetEnumerator()) {
        $relativePath = $entry.Value
        $fullPath = Join-Path $stagingRoot $relativePath
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
        releaseTrust = [ordered]@{
            signed = $null -ne $signingCertificate
            serviceWritesLocked = [bool]$LockServiceWrites
            policy = if ($LockServiceWrites) { "PublicUnsignedPreview" } elseif ($null -ne $signingCertificate) { "SignedRelease" } else { "UnsignedDevelopment" }
        }
        requiredServiceFeatures = @(
            "service-status",
            "capability-v2",
            "fan-commissioning",
            "fan-calibrations",
            "auto-oc-workload-v1",
            "reliability",
            "adapter-trace",
            "cooling-output-roles",
            "release-write-policy",
            "auto-oc-v3",
            "profile-dry-run-v1",
            "auto-oc-validation-v1"
        )
        components = @($runtimeComponents)
    }
    $runtimeContract | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stagingRoot "runtime-contract.json") -Encoding UTF8

    Get-ChildItem -LiteralPath $stagingRoot -Recurse -File |
        Get-FileHash -Algorithm SHA256 |
        ForEach-Object {
            $relativePath = $_.Path.Substring($stagingRoot.Length).TrimStart('\')
            "{0} *{1}" -f $_.Hash.ToLowerInvariant(), $relativePath
        } |
        Set-Content -LiteralPath (Join-Path $stagingRoot "SHA256SUMS.txt") -Encoding UTF8
}

# Validation runs against staging, so a payload that fails it is discarded with the
# previous one still in place. A payload that never passed these checks must never
# become the published payload.
$validateStaging = {
    param([string]$stagingRoot)

    & (Join-Path $PSScriptRoot "Test-RuntimePayload.ps1") `
        -PayloadRoot $stagingRoot `
        -ExpectedProductVersion $Version `
        -RequireServiceWritesLocked:$LockServiceWrites | Out-Null

    $hygiene = & (Join-Path $PSScriptRoot "Test-DistributionHygiene.ps1") -PayloadRoot $stagingRoot
    if ($null -eq $hygiene) {
        throw "Distribution hygiene produced no result for the staged payload."
    }
    if (-not $hygiene.Ready) {
        throw "Distribution hygiene failed for the staged payload: $(@($hygiene.Failures) -join '; ')"
    }
}

Invoke-PayloadReplacement `
    -DestinationRoot $outputRoot `
    -BuildStaging $buildStaging `
    -ValidateStaging $validateStaging | Out-Null

if ($null -ne $signingCertificate) {
    Write-Host "Published signed RigPilot $Version to $outputRoot"
} else {
    $writeStatus = if ($LockServiceWrites) { "All service mutations are build-locked." } else { "This payload is for local development only; service mutations are not release-locked." }
    Write-Host "Published unsigned RigPilot $Version to $outputRoot. $writeStatus"
}
