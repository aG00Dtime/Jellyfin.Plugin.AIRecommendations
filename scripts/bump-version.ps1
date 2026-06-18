# Bump VERSION.txt and sync Jellyfin.Plugin.AIRecommendations.csproj + manifest.json
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VersionFile = Join-Path $Root 'VERSION.txt'
$Csproj = Join-Path $Root 'Jellyfin.Plugin.AIRecommendations.csproj'
$Manifest = Join-Path $Root 'manifest.json'

$current = (Get-Content $VersionFile -Raw).Trim()
if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "VERSION.txt must be semver (major.minor.patch), got: $current"
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]

switch ($Part) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}

$newVersion = "$major.$minor.$patch"
$newVersionFour = "$newVersion.0"

Set-Content -Path $VersionFile -Value $newVersion -NoNewline

$csprojContent = Get-Content $Csproj -Raw
$csprojContent = $csprojContent -replace '<AssemblyVersion>[\d.]+</AssemblyVersion>', "<AssemblyVersion>$newVersionFour</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[\d.]+</FileVersion>', "<FileVersion>$newVersionFour</FileVersion>"
Set-Content -Path $Csproj -Value $csprojContent -NoNewline

$manifestContent = Get-Content $Manifest -Raw
$manifestContent = $manifestContent -replace '"version": "[^"]+"', "`"version`": `"$newVersionFour`""
$manifestContent = $manifestContent -replace '"sourceUrl": "[^"]+"', "`"sourceUrl`": `"https://github.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/releases/download/v$newVersion/Jellyfin.Plugin.AIRecommendations.zip`""
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$manifestContent = $manifestContent -replace '"timestamp": "[^"]+"', "`"timestamp`": `"$timestamp`""
$manifestContent = $manifestContent -replace '"changelog": "[^"]+"', "`"changelog`": `"Build $newVersion`""
Set-Content -Path $Manifest -Value $manifestContent -NoNewline

Write-Host "Version bumped to $newVersion ($newVersionFour)"
