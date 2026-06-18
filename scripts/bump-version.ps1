# Bump VERSION.txt and sync Jellyfin.Plugin.AIRecommendations.csproj + manifest.json
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VersionFile = Join-Path $Root 'VERSION.txt'
$Csproj = Join-Path $Root 'Jellyfin.Plugin.AIRecommendations.csproj'
$ManifestPath = Join-Path $Root 'manifest.json'

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

$csprojContent = Get-Content $Csproj -Raw
if ($csprojContent -match 'Jellyfin\.Controller" Version="(\d+)\.(\d+)') {
    $targetAbi = "$($Matches[1]).$($Matches[2]).0.0"
} else {
    $targetAbi = '10.11.0.0'
}

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$content = [System.IO.File]::ReadAllText($ManifestPath)

if ($content -match ('"version"\s*:\s*"' + [regex]::Escape($newVersionFour) + '"')) {
    $content = [regex]::Replace(
        $content,
        '(?s)("version"\s*:\s*"' + [regex]::Escape($newVersionFour) + '".*?"checksum"\s*:\s*")[^"]*(")',
        '${1}${2}',
        1
    )
} else {
    $newEntry = @"
      {
        "version": "$newVersionFour",
        "changelog": "Build $newVersion",
        "targetAbi": "$targetAbi",
        "sourceUrl": "https://github.com/aG00Dtime/Jellyfin.Plugin.AIRecommendations/releases/download/v$newVersion/Jellyfin.Plugin.AIRecommendations.zip",
        "checksum": "",
        "timestamp": "$timestamp"
      },
"@
    $content = [regex]::Replace($content, '("versions"\s*:\s*\[)', "`${1}`n$newEntry", 1)
}

$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($ManifestPath, $content, $utf8)

Write-Host "Version bumped to $newVersion ($newVersionFour) - run build.ps1 to sync checksum"
