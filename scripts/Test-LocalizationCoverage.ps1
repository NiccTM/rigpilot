<#
.SYNOPSIS
    Reports how much of the dashboard's user-visible text is localizable, and what is wrong
    with the resource files.

.DESCRIPTION
    The localization pipeline is complete — `{loc:Loc Key}` in XAML, `L10n.Get` in code,
    English fallback, and a bracketed key for anything missing — but most user-visible text
    is still hardcoded English. That gap is invisible without measuring it, and "extract the
    strings" is not reviewable work unless the remaining count is a number that goes down.

    This is read-only. It reports four things:
      * Localized vs hardcoded user-visible strings in XAML, with a coverage percentage.
      * Keys referenced by XAML or code that do NOT exist in the neutral resource file —
        these render as "[Key]" at runtime and are the only failures that are user-visible.
      * Keys present in the neutral file that nothing references (dead weight).
      * Per-culture translation gaps against the neutral file. These are NOT failures:
        .NET resource fallback returns English, so a partially translated language is always
        safe to ship, and treating it as an error would punish adding a language early.

    Attributes are chosen to match what a user actually reads: Text, Content, ToolTip, and
    the accessibility name. Bindings, StaticResources, glyph fonts, and single-character
    values are skipped — they are markup, not prose.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER FailOnMissingKeys
    Exit non-zero when a referenced key is absent from the neutral resource file. Intended
    for CI: a missing key is a visible defect, unlike an untranslated one.

.OUTPUTS
    A summary object plus detail lists.
#>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$FailOnMissingKeys
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$appRoot = Join-Path $Root "src\PCHelper.App"
$localizationRoot = Join-Path $appRoot "Localization"
$neutralResx = Join-Path $localizationRoot "Strings.resx"
if (-not (Test-Path -LiteralPath $neutralResx)) {
    throw "Neutral resource file not found: $neutralResx"
}

function Get-ResxKeys([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $xml = [xml](Get-Content -LiteralPath $Path -Raw)
    return @($xml.root.data | Where-Object { $_.name } | ForEach-Object { $_.name })
}

$neutralKeys = @(Get-ResxKeys $neutralResx)

# --- Referenced keys -------------------------------------------------------------------
$referenced = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
# Literal prefixes of keys assembled at runtime, e.g. "Onboarding_Title" from
# L10n.Get($"Onboarding_Title{step}").
$interpolatedPrefixes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File |
    Where-Object { $_.Extension -in '.xaml', '.cs' -and $_.FullName -notmatch '\\(bin|obj)\\' }) {
    # An empty file yields $null from -Raw, which Matches() rejects.
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ([string]::IsNullOrEmpty($text)) { continue }
    # Every loc: extension, not only the bare one. {loc:LocShortcut Key, 7} references its
    # key just as really; missing it reported live keys as orphans, and acting on that
    # would have deleted strings the navigation rail resolves at load.
    foreach ($match in [regex]::Matches($text, '\{loc:Loc[A-Za-z]*\s+([A-Za-z0-9_.]+)')) {
        [void]$referenced.Add($match.Groups[1].Value)
    }
    # Get and Format both take the key first. Scanning only Get reported every composed
    # string's format as an orphan, which is the shape of finding that gets a live resource
    # deleted because the tool called it dead.
    foreach ($match in [regex]::Matches($text, 'L10n\.(?:Get|Format)\(\s*"([^"]+)"')) {
        [void]$referenced.Add($match.Groups[1].Value)
    }
    # Keys built by interpolation — L10n.Get($"Onboarding_Title{step}") — are real
    # references with no literal to match. Treat the literal prefix as covering every key
    # that starts with it; without this the tool reports live keys as dead weight, and
    # acting on that would delete strings the UI still resolves at runtime.
    foreach ($match in [regex]::Matches($text, 'L10n\.Get\(\s*\$"([^"{]+)\{')) {
        $prefix = $match.Groups[1].Value
        if ($prefix.Length -gt 0) {
            [void]$interpolatedPrefixes.Add($prefix)
        }
    }
}

function Test-Referenced([string]$Key) {
    if ($referenced.Contains($Key)) { return $true }
    foreach ($prefix in $interpolatedPrefixes) {
        if ($Key.StartsWith($prefix, [StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

$missingKeys = @($referenced | Where-Object { $neutralKeys -notcontains $_ } | Sort-Object)
$orphanKeys = @($neutralKeys | Where-Object { -not (Test-Referenced $_) } | Sort-Object)

# --- XAML coverage ---------------------------------------------------------------------
# Only attributes a user reads. A literal is "hardcoded" when it is prose rather than markup.
$attributes = 'Text', 'Content', 'ToolTip', 'automation:AutomationProperties.Name'
$localizedCount = 0
$hardcoded = [System.Collections.Generic.List[object]]::new()

foreach ($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File |
    Where-Object { $_.Extension -eq '.xaml' -and $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $lines = @(Get-Content -LiteralPath $file.FullName)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        foreach ($attribute in $attributes) {
            # The leading lookbehind is load-bearing: without it "Content" matches inside
            # SizeToContent="WidthAndHeight", reporting a layout enum as untranslated prose.
            # It rejects a preceding word character only, so attached properties that
            # legitimately end in one of these names (ToolTipService.ToolTip) still count.
            $pattern = '(?<!\w)' + [regex]::Escape($attribute) + '\s*=\s*"([^"]*)"'
            foreach ($match in [regex]::Matches($lines[$index], $pattern)) {
                $value = $match.Groups[1].Value
                # Matches every loc: extension, not just the bare one — {loc:LocShortcut …}
                # composes a translated name with a translated modifier and is as localized
                # as {loc:Loc …} is. Requiring a word boundary here counted it as neither
                # localized nor hardcoded, so the composed tooltips vanished from the total.
                if ($value -match '^\{loc:Loc') { $localizedCount++; continue }
                # Markup, not prose: bindings, resources, glyphs, and trivia.
                if ($value -match '^\s*\{') { continue }
                if ($value.Trim().Length -le 1) { continue }
                if ($value -match '^[\s\p{P}\p{S}]+$') { continue }
                if ($value -match '^&#x[0-9A-Fa-f]+;$') { continue }
                if ($value -notmatch '[A-Za-z]{2}') { continue }
                $hardcoded.Add([pscustomobject]@{
                    File      = $file.FullName.Replace("$Root\", '')
                    Line      = $index + 1
                    Attribute = $attribute
                    Value     = if ($value.Length -gt 60) { $value.Substring(0, 60) + '…' } else { $value }
                })
            }
        }
    }
}

# --- Per-culture translation gaps ------------------------------------------------------
$cultures = @()
foreach ($file in Get-ChildItem -LiteralPath $localizationRoot -Filter 'Strings.*.resx' -File) {
    $culture = [System.IO.Path]::GetFileNameWithoutExtension($file.Name) -replace '^Strings\.', ''
    $keys = @(Get-ResxKeys $file.FullName)
    $untranslated = @($neutralKeys | Where-Object { $keys -notcontains $_ })
    $cultures += [pscustomobject]@{
        Culture            = $culture
        Translated         = $keys.Count
        Untranslated       = $untranslated.Count
        CoveragePercent    = if ($neutralKeys.Count -gt 0) { [math]::Round(100.0 * $keys.Count / $neutralKeys.Count, 1) } else { 0 }
    }
}

$totalStrings = $localizedCount + $hardcoded.Count
$summary = [pscustomobject]@{
    NeutralKeys          = $neutralKeys.Count
    ReferencedKeys       = $referenced.Count
    MissingKeys          = $missingKeys.Count
    OrphanKeys           = $orphanKeys.Count
    LocalizedStrings     = $localizedCount
    HardcodedStrings     = $hardcoded.Count
    XamlCoveragePercent  = if ($totalStrings -gt 0) { [math]::Round(100.0 * $localizedCount / $totalStrings, 1) } else { 100 }
    Cultures             = $cultures
}

$summary
if ($missingKeys.Count -gt 0) {
    Write-Host "`nReferenced but MISSING from Strings.resx (these render as [Key]):" -ForegroundColor Yellow
    $missingKeys | ForEach-Object { Write-Host "  $_" }
}
if ($orphanKeys.Count -gt 0) {
    Write-Host "`nIn Strings.resx but never referenced:" -ForegroundColor DarkGray
    $orphanKeys | ForEach-Object { Write-Host "  $_" }
}
if ($hardcoded.Count -gt 0) {
    Write-Host "`nTop hardcoded strings awaiting extraction:" -ForegroundColor DarkGray
    $hardcoded | Select-Object -First 15 | Format-Table -AutoSize | Out-String -Width 160 | Write-Host
}

if ($FailOnMissingKeys -and $missingKeys.Count -gt 0) {
    Write-Error "$($missingKeys.Count) referenced localization key(s) are missing from Strings.resx."
    exit 1
}
