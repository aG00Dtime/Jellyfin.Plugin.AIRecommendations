# Fail if VERSION.txt, csproj, manifest.json disagree or checksum is missing/invalid.
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VersionFile = Join-Path $Root 'VERSION.txt'
$Csproj = Join-Path $Root 'Jellyfin.Plugin.AIRecommendations.csproj'
$ManifestPath = Join-Path $Root 'manifest.json'
$ZipPath = Join-Path $Root 'bin\Release\net9.0\Jellyfin.Plugin.AIRecommendations.zip'

$semver = (Get-Content $VersionFile -Raw).Trim()
if ($semver -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    Write-Error 'VERSION.txt must be major.minor.patch'
}

$expectedFour = "$semver.0"
$csprojContent = Get-Content $Csproj -Raw

if ($csprojContent -notmatch "<AssemblyVersion>$([regex]::Escape($expectedFour))</AssemblyVersion>") {
    Write-Error "AssemblyVersion in csproj does not match VERSION.txt ($expectedFour)"
}

if ($csprojContent -notmatch "<FileVersion>$([regex]::Escape($expectedFour))</FileVersion>") {
    Write-Error "FileVersion in csproj does not match VERSION.txt ($expectedFour)"
}

$parsed = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$plugin = if ($parsed -is [System.Array]) { $parsed[0] } else { $parsed }
$entry = $plugin.versions | Where-Object { $_.version -eq $expectedFour } | Select-Object -First 1

if (-not $entry) {
    Write-Error "manifest.json has no entry for version $expectedFour"
}

if ([string]::IsNullOrWhiteSpace($entry.checksum)) {
    Write-Error "manifest.json checksum is empty for $expectedFour - run .\build.ps1"
}

if ($env:VERIFY_ZIP_CHECKSUM -eq '1' -and (Test-Path $ZipPath)) {
    $expectedChecksum = (Get-FileHash $ZipPath -Algorithm MD5).Hash.ToLower()
    if ($entry.checksum -ne $expectedChecksum) {
        Write-Error "manifest checksum ($($entry.checksum)) does not match zip ($expectedChecksum) - run .\build.ps1"
    }
}

exit 0
