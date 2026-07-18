[CmdletBinding()]
param(
    [switch]$IncludeSuffix
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$propsPath = Join-Path $PSScriptRoot "..\Directory.Build.props"
if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
    throw "Directory.Build.props was not found: $propsPath"
}

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$prefixNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
$suffixNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionSuffix')
$prefix = if ($null -eq $prefixNode) { "" } else { $prefixNode.InnerText.Trim() }
$suffix = if ($null -eq $suffixNode) { "" } else { $suffixNode.InnerText.Trim() }

if ($prefix -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props must define a three-component VersionPrefix: $prefix"
}
if (-not [string]::IsNullOrWhiteSpace($suffix) -and $suffix -notmatch '^[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*$') {
    throw "Directory.Build.props contains an invalid VersionSuffix: $suffix"
}

if ($IncludeSuffix -and -not [string]::IsNullOrWhiteSpace($suffix)) {
    "$prefix-$suffix"
} else {
    $prefix
}
