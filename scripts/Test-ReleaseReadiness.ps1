[CmdletBinding()]
param(
    [string]$QualificationLedger,
    [string]$SigningCertificateThumbprint,
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

$result = [pscustomobject]@{
    SigningReady = [bool]$signing.Ready
    SigningMessage = [string]$signing.Message
    QualificationLedger = $ledgerPath
    QualificationReady = ($qualificationExit -eq 0)
    QualificationExitCode = $qualificationExit
    QualificationOutput = ($qualificationOutput -join [Environment]::NewLine)
    CanPublishSignedAlpha = [bool]$signing.Ready
    CanPublishVersionOne = ([bool]$signing.Ready -and $qualificationExit -eq 0)
}

$result
if ($RequireSignedRelease -and -not $result.CanPublishSignedAlpha) {
    throw "Signed release readiness failed: $($result.SigningMessage)"
}
if ($RequireVersionOne -and -not $result.CanPublishVersionOne) {
    throw "Version 1.0 release readiness failed. A valid signing identity and a passing physical qualification ledger are both required."
}
