# Set manifest.json checksum + timestamp for VERSION.txt from the release zip.
param(
    [string]$ZipPath = ""
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$ManifestPath = Join-Path $Root 'manifest.json'
$VersionFile = Join-Path $Root 'VERSION.txt'

if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $Root 'bin\Release\net9.0\Jellyfin.Plugin.AIRecommendations.zip'
}

if (-not (Test-Path $ZipPath)) {
    throw "Zip not found: $ZipPath (run .\build.ps1 first)"
}

$semver = (Get-Content $VersionFile -Raw).Trim()
$versionFour = "$semver.0"
$checksum = (Get-FileHash $ZipPath -Algorithm MD5).Hash.ToLower()
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$content = [System.IO.File]::ReadAllText($ManifestPath)
$escapedVersion = [regex]::Escape($versionFour)

$checksumPattern = '(?s)("version"\s*:\s*"' + $escapedVersion + '".*?"checksum"\s*:\s*")[^"]*(")'
if (-not ([regex]::IsMatch($content, $checksumPattern))) {
    throw "manifest.json has no checksum field for version $versionFour"
}
$content = [regex]::Replace($content, $checksumPattern, '${1}' + $checksum + '${2}')

$timestampPattern = '(?s)("version"\s*:\s*"' + $escapedVersion + '".*?"timestamp"\s*:\s*")[^"]*(")'
if (-not ([regex]::IsMatch($content, $timestampPattern))) {
    throw "manifest.json has no timestamp field for version $versionFour"
}
$content = [regex]::Replace($content, $timestampPattern, '${1}' + $timestamp + '${2}')

$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($ManifestPath, $content, $utf8)

Write-Host "manifest.json checksum for $versionFour -> $checksum"
