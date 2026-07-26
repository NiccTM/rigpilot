[CmdletBinding()]
param(
    [string]$QualificationLedger,
    [string]$SigningCertificateThumbprint,
    [string]$PayloadRoot,
    [switch]$RequireSignedRelease,
    [switch]$RequireVersionOne
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($QualificationLedger)) {
    $QualificationLedger = Join-Path $repoRoot "docs\qualification\reference-system.json"
}
$ledgerPath = [System.IO.Path]::GetFullPath($QualificationLedger)
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

$signing = & (Join-Path $PSScriptRoot "Test-CodeSigningPrerequisites.ps1") -CertificateThumbprint $SigningCertificateThumbprint
$qualificationExit = 1
$qualificationOutput = @()
if (Test-Path -LiteralPath $ledgerPath -PathType Leaf) {
    $qualificationOutput = @(& $dotnet run --project (Join-Path $repoRoot "src\PCHelper.Cli") --configuration Release --no-build -- qualification --ledger $ledgerPath --json 2>&1)
    $qualificationExit = $LASTEXITCODE
}

# Distribution hygiene (publisher metadata, unpacked managed body, non-elevated manifest) is
# only evaluated when a built payload is supplied; without one it is reported as not-checked
# rather than assumed pass, and it never blocks on its own unless a signed release is required.
$hygieneReady = $null
$hygieneFailures = @()
if (-not [string]::IsNullOrWhiteSpace($PayloadRoot)) {
    try {
        $hygiene = & (Join-Path $PSScriptRoot "Test-DistributionHygiene.ps1") -PayloadRoot $PayloadRoot
        $hygieneReady = [bool]$hygiene.Ready
        $hygieneFailures = @($hygiene.Failures)
    }
    catch {
        $hygieneReady = $false
        $hygieneFailures = @("$_")
    }
}

$signedGate = [bool]$signing.Ready -and ($hygieneReady -ne $false)

$result = [pscustomobject]@{
    SigningReady = [bool]$signing.Ready
    SigningMessage = [string]$signing.Message
    QualificationLedger = $ledgerPath
    QualificationReady = ($qualificationExit -eq 0)
    QualificationExitCode = $qualificationExit
    QualificationOutput = ($qualificationOutput -join [Environment]::NewLine)
    DistributionHygieneReady = $hygieneReady
    DistributionHygieneFailures = ($hygieneFailures -join [Environment]::NewLine)
    CanPublishSignedAlpha = $signedGate
    CanPublishVersionOne = ($signedGate -and $qualificationExit -eq 0)
}

$result
if ($RequireSignedRelease -and -not $result.CanPublishSignedAlpha) {
    if ($hygieneReady -eq $false) {
        throw "Signed release readiness failed: distribution hygiene failed. $($result.DistributionHygieneFailures)"
    }
    throw "Signed release readiness failed: $($result.SigningMessage)"
}
if ($RequireVersionOne -and -not $result.CanPublishVersionOne) {
    throw "Version 1.0 release readiness failed. A valid signing identity and a passing physical qualification ledger are both required."
}
