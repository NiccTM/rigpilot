<#
.SYNOPSIS
    Asserts that a published RigPilot payload carries the traits that keep it from tripping
    antivirus heuristics: publisher metadata, a real (unpacked) managed body, and - for the
    dashboard executable - a non-elevated application manifest.

.DESCRIPTION
    Antivirus engines flag unsigned, metadata-less, or packed binaries. Signing is handled
    elsewhere (Test-CodeSigningPrerequisites / the release pipeline); this checks the traits
    that must hold regardless of signing. It is read-only.

    For each first-party PCHelper binary it verifies:
      * CompanyName and ProductName version-resource fields are present (publisher identity).
      * The file is a genuine managed .NET assembly (AssemblyName resolves), i.e. not packed
        or replaced by an opaque blob.
    For the dashboard host (PCHelper.App.exe) it also verifies an embedded application
    manifest requesting `asInvoker` (no silent elevation).

    Third-party dependencies are not checked; their publishers own their metadata.

.PARAMETER PayloadRoot
    A published payload directory (e.g. artifacts\publish) to scan recursively.

.OUTPUTS
    A result object with Ready (bool), Checked (int), and Failures (string[]). Exit code is
    non-zero when any check fails, so it can gate a release step.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PayloadRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($PayloadRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Payload root does not exist: $root"
}

# First-party binaries only: our managed .dll assemblies and their .NET apphost .exe
# launchers. A .NET apphost .exe is a NATIVE bootstrapper paired with a same-named managed
# .dll, so the managed-assembly check applies to the .dll, not the .exe. Third-party
# dependencies (SkiaSharp, SQLite, ...) own their own metadata and are not checked here.
$binaries = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    ($_.Extension -ieq '.exe' -or $_.Extension -ieq '.dll') -and
    ($_.BaseName -like 'PCHelper.*' -or $_.BaseName -ieq 'PCHelper' -or $_.BaseName -ieq 'pchelper-cli')
})
$failures = [System.Collections.Generic.List[string]]::new()
$checked = 0

function Test-ManagedAssembly([string]$path) {
    try {
        [void][System.Reflection.AssemblyName]::GetAssemblyName($path)
        return $true
    }
    catch {
        return $false
    }
}

foreach ($binary in $binaries) {
    $checked++
    $name = $binary.Name

    # Every first-party binary - managed .dll and native apphost .exe alike - carries the
    # publisher version resource; the apphost copies it from the app at publish time.
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($binary.FullName)
    if ([string]::IsNullOrWhiteSpace($info.CompanyName)) {
        $failures.Add("$name : missing CompanyName version metadata")
    }
    if ([string]::IsNullOrWhiteSpace($info.ProductName)) {
        $failures.Add("$name : missing ProductName version metadata")
    }

    if ($binary.Extension -ieq '.dll') {
        # The managed body must be a genuine assembly, not a packed or obfuscated blob.
        if (-not (Test-ManagedAssembly $binary.FullName)) {
            $failures.Add("$name : not a valid managed assembly (packed, obfuscated, or corrupt?)")
        }
    }
    else {
        # The apphost .exe must embed a manifest, and the dashboard host must not self-elevate.
        $bytes = [System.IO.File]::ReadAllBytes($binary.FullName)
        $text = [System.Text.Encoding]::ASCII.GetString($bytes)
        if ($text -notmatch 'requestedExecutionLevel') {
            $failures.Add("$name : no embedded application manifest (requestedExecutionLevel absent)")
        }
        elseif ($name -ieq 'PCHelper.App.exe' -and $text -notmatch 'asInvoker') {
            $failures.Add("$name : dashboard manifest does not request asInvoker (must not self-elevate)")
        }
    }
}

if ($checked -eq 0) {
    $failures.Add("No first-party PCHelper binaries were found under $root")
}

$result = [pscustomobject]@{
    Ready    = ($failures.Count -eq 0)
    Checked  = $checked
    Failures = $failures.ToArray()
}
$result

if (-not $result.Ready) {
    Write-Error ("Distribution hygiene failed:`n  " + ($failures -join "`n  "))
    exit 1
}
