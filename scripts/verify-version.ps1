# Fail if VERSION.txt, csproj, and manifest.json disagree
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VersionFile = Join-Path $Root 'VERSION.txt'
$Csproj = Join-Path $Root 'Jellyfin.Plugin.AIRecommendations.csproj'
$Manifest = Join-Path $Root 'manifest.json'

$semver = (Get-Content $VersionFile -Raw).Trim()
if ($semver -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    Write-Error "VERSION.txt must be major.minor.patch"
}

$expectedFour = "$semver.0"
$csprojContent = Get-Content $Csproj -Raw
$manifestContent = Get-Content $Manifest -Raw

if ($csprojContent -notmatch "<AssemblyVersion>$([regex]::Escape($expectedFour))</AssemblyVersion>") {
    Write-Error "AssemblyVersion in csproj does not match VERSION.txt ($expectedFour)"
}

if ($csprojContent -notmatch "<FileVersion>$([regex]::Escape($expectedFour))</FileVersion>") {
    Write-Error "FileVersion in csproj does not match VERSION.txt ($expectedFour)"
}

if ($manifestContent -notmatch "`"version`": `"$([regex]::Escape($expectedFour))`"") {
    Write-Error "manifest.json version does not match VERSION.txt ($expectedFour)"
}

exit 0
