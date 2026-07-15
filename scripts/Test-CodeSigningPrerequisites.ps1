[CmdletBinding()]
param(
    [string]$CertificateThumbprint,
    [switch]$RequireSigning
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-CodeSigningEku([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -ne "2.5.29.37") {
            continue
        }
        $usages = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
            $extension.RawData,
            $extension.Critical).EnhancedKeyUsages
        return @($usages | Where-Object { $_.Value -eq "1.3.6.1.5.5.7.3.3" }).Count -gt 0
    }
    return $false
}

$now = Get-Date
$normalisedThumbprint = $CertificateThumbprint -replace "\s", ""
$certificates = @(Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object {
        $_.HasPrivateKey -and
        $_.NotBefore -le $now -and
        $_.NotAfter -gt $now -and
        (Test-CodeSigningEku $_) -and
        ([string]::IsNullOrWhiteSpace($normalisedThumbprint) -or $_.Thumbprint -eq $normalisedThumbprint)
    } |
    Sort-Object NotAfter -Descending)

$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($null -eq $signTool) {
    $kitRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitRoot) {
        $signTool = Get-ChildItem -LiteralPath $kitRoot -Recurse -Filter signtool.exe -File |
            Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
    }
}

$signToolPath = $null
if ($null -ne $signTool) {
    if ($signTool.PSObject.Properties.Name -contains "Path") {
        $signToolPath = $signTool.Path
    }
    if ([string]::IsNullOrWhiteSpace($signToolPath) -and ($signTool.PSObject.Properties.Name -contains "FullName")) {
        $signToolPath = $signTool.FullName
    }
}

$ready = $certificates.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($signToolPath)
$result = [pscustomobject]@{
    Ready = $ready
    CertificateCount = $certificates.Count
    CertificateThumbprint = if ($certificates.Count -eq 1) { $certificates[0].Thumbprint } else { $null }
    CertificateSubject = if ($certificates.Count -eq 1) { $certificates[0].Subject } else { $null }
    SignToolPath = $signToolPath
    Message = if ($ready) {
        "A single valid code-signing certificate and signtool are available."
    } elseif ($certificates.Count -eq 0) {
        "No current code-signing certificate with a private key and Code Signing EKU was found."
    } elseif ($certificates.Count -gt 1) {
        "Multiple valid code-signing certificates were found; select one by thumbprint."
    } else {
        "signtool.exe was not found."
    }
}

$result
if ($RequireSigning -and -not $ready) {
    throw $result.Message
}
