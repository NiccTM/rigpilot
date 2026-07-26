<#
.SYNOPSIS
    Reports interactive controls a screen reader would announce with no usable name.

.DESCRIPTION
    A control is reachable by keyboard long before it is understandable by a screen reader.
    UI Automation derives a name from an element's text content, so a button labelled "Apply"
    is already announced correctly and does NOT need an explicit AutomationProperties.Name —
    flagging it would bury the real defects in noise. What cannot be derived is a control with
    no text at all: an icon-only button, a slider, a text box, a combo box. Those announce as
    "button", "edit", "combo box" and nothing else, which makes the control unusable without
    sight.

    So this reports exactly that class: interactive elements with neither an explicit
    accessible name nor text a screen reader could fall back on. It parses the XAML as XML
    rather than by regex, because these elements span many lines and attribute order varies.

    Read-only. Intended both as a report and, with -FailOnMissingNames, as a CI gate.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER FailOnMissingNames
    Exit non-zero when any interactive control lacks an accessible name.
#>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$FailOnMissingNames
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$appRoot = Join-Path $Root "src\PCHelper.App"

# Controls a user operates. Anything here is announced by a screen reader as an interactive
# element, so an unnamed one is a real barrier rather than a cosmetic omission.
$interactive = @(
    'Button', 'RepeatButton', 'ToggleButton', 'CheckBox', 'RadioButton',
    'Slider', 'TextBox', 'PasswordBox', 'ComboBox', 'ListBox', 'ListView',
    'TabControl', 'Expander', 'Hyperlink'
)

# Controls whose own text content gives UI Automation a usable name.
$namedByContent = @('Button', 'RepeatButton', 'ToggleButton', 'CheckBox', 'RadioButton', 'Expander', 'Hyperlink')

$automationNs = 'clr-namespace:System.Windows.Automation;assembly=PresentationCore'
$findings = [System.Collections.Generic.List[object]]::new()
$checked = 0
$named = 0

function Get-AccessibleName($node) {
    foreach ($attribute in $node.Attributes) {
        # Matches both automation:AutomationProperties.Name and any other prefix bound to the
        # same namespace, so a renamed xmlns prefix does not silently disable the check.
        if ($attribute.LocalName -eq 'AutomationProperties.Name' -or $attribute.Name -like '*AutomationProperties.Name') {
            if (-not [string]::IsNullOrWhiteSpace($attribute.Value)) { return $attribute.Value }
        }
    }
    return $null
}

# A markup extension is normally not usable as a name, because {Binding …} resolves to a
# value this script cannot see. {loc:Loc …} is the exception: it resolves to a fixed string
# from the resx at load time, so a control named by one is exactly as nameable as a literal.
# Without this, localizing a control's own label REMOVED its accessible name from the count —
# a false regression that would have arrived once per extraction batch, forever.
function Test-IsNameText([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    if ($Value -match '^\s*\{loc:Loc') { return $true }
    return $Value -notmatch '^\s*\{' -and $Value -match '[A-Za-z]{2}'
}

function Test-HasTextContent($node) {
    # Literal Content/Text on the element itself.
    foreach ($name in 'Content', 'Text') {
        if (Test-IsNameText $node.GetAttribute($name)) {
            return $true
        }
    }
    # Or descendant text — a Button wrapping a TextBlock is still named by that text. A glyph
    # from the icon font is not text a screen reader can use, so it does not count.
    foreach ($descendant in $node.SelectNodes('.//*')) {
        if ($descendant.LocalName -ne 'TextBlock' -and $descendant.LocalName -ne 'Run') { continue }
        if ($descendant.GetAttribute('FontFamily') -match 'IconFont') { continue }
        if (Test-IsNameText $descendant.GetAttribute('Text')) {
            return $true
        }
        if (-not [string]::IsNullOrWhiteSpace($descendant.InnerText) -and $descendant.InnerText -match '[A-Za-z]{2}') {
            return $true
        }
    }
    return $false
}

foreach ($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File |
    Where-Object { $_.Extension -eq '.xaml' -and $_.FullName -notmatch '\\(bin|obj)\\' }) {

    try {
        $xml = New-Object System.Xml.XmlDocument
        $xml.PreserveWhitespace = $true
        $xml.Load($file.FullName)
    }
    catch {
        Write-Warning "Could not parse $($file.Name): $($_.Exception.Message)"
        continue
    }

    foreach ($node in $xml.SelectNodes('//*')) {
        if ($interactive -notcontains $node.LocalName) { continue }

        # Skip the internals of a ControlTemplate. A Slider's repeat buttons or an Expander's
        # header toggle are PARTS of one control, not controls in their own right: UI
        # Automation announces the templated parent, and the parts are deliberately absent
        # from the tree. Naming them individually would be wrong, and reporting them buries
        # real defects — every finding in the first run of this script was one of these.
        $inTemplate = $false
        for ($ancestor = $node.ParentNode; $ancestor -ne $null -and $ancestor.NodeType -eq 'Element'; $ancestor = $ancestor.ParentNode) {
            if ($ancestor.LocalName -eq 'ControlTemplate') { $inTemplate = $true; break }
        }
        if ($inTemplate) { continue }

        $checked++
        if (Get-AccessibleName $node) { $named++; continue }
        if ($namedByContent -contains $node.LocalName -and (Test-HasTextContent $node)) { $named++; continue }

        $findings.Add([pscustomobject]@{
            File    = $file.FullName.Replace("$Root\", '')
            Control = $node.LocalName
            Hint    = if ($node.GetAttribute('x:Name')) { $node.GetAttribute('x:Name') }
                      elseif ($node.GetAttribute('Command')) { $node.GetAttribute('Command') }
                      elseif ($node.GetAttribute('Style')) { $node.GetAttribute('Style') }
                      else { '(no identifying attribute)' }
        })
    }
}

$summary = [pscustomobject]@{
    InteractiveControls = $checked
    WithAccessibleName  = $named
    MissingName         = $findings.Count
    CoveragePercent     = if ($checked -gt 0) { [math]::Round(100.0 * $named / $checked, 1) } else { 100 }
}
$summary

if ($findings.Count -gt 0) {
    Write-Host "`nInteractive controls a screen reader cannot name:" -ForegroundColor Yellow
    $findings |
        Group-Object File |
        ForEach-Object {
            Write-Host "  $($_.Name)  ($($_.Count))"
            $_.Group | Select-Object -First 10 | ForEach-Object {
                Write-Host "      $($_.Control.PadRight(14)) $($_.Hint)"
            }
        }
}

if ($FailOnMissingNames -and $findings.Count -gt 0) {
    Write-Error "$($findings.Count) interactive control(s) have no accessible name."
    exit 1
}
