# Download the latest GitHub release zip and install to Jellyfin (works with private repos).
param(
    [string]$JellyfinPluginsPath = "",
    [string]$GitHubToken = "",
    [string]$Repo = "aG00Dtime/Jellyfin.Plugin.AIRecommendations",
    [string]$Tag = "",
    [switch]$RestartJellyfin
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
    $GitHubToken = $env:GITHUB_TOKEN
}

if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
    Write-Host "GitHub token required for a private repo." -ForegroundColor Red
    Write-Host "Set GITHUB_TOKEN or pass -GitHubToken (needs repo scope)." -ForegroundColor Yellow
    exit 1
}

$headers = @{
    Authorization = "Bearer $GitHubToken"
    Accept        = "application/vnd.github+json"
    "User-Agent"  = "Jellyfin-AIRecommendations-Installer"
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
} else {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $headers
}

$asset = $release.assets | Where-Object { $_.name -eq "Jellyfin.Plugin.AIRecommendations.zip" } | Select-Object -First 1
if (-not $asset) {
    throw "Release zip not found on $($release.tag_name)"
}

$versionTag = $release.tag_name.TrimStart('v')
$pluginFolderName = "AI Recommendations_$versionTag.0"

if ([string]::IsNullOrWhiteSpace($JellyfinPluginsPath)) {
    $defaultPath = Join-Path $env:APPDATA "Jellyfin\Server\plugins"
    if (Test-Path $defaultPath) {
        $JellyfinPluginsPath = $defaultPath
    } else {
        Write-Host "Jellyfin plugins folder not found. Pass -JellyfinPluginsPath." -ForegroundColor Red
        exit 1
    }
}

$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) "Jellyfin.Plugin.AIRecommendations.zip"
$tempExtract = Join-Path ([System.IO.Path]::GetTempPath()) "Jellyfin.Plugin.AIRecommendations.extract"

Write-Host "Downloading $($release.tag_name)..." -ForegroundColor Cyan
Invoke-RestMethod -Uri $asset.url -Headers ($headers + @{ Accept = "application/octet-stream" }) -OutFile $tempZip

if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
New-Item -ItemType Directory -Path $tempExtract | Out-Null
Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

$dll = Get-ChildItem -Path $tempExtract -Filter "Jellyfin.Plugin.AIRecommendations.dll" -Recurse | Select-Object -First 1
if (-not $dll) {
    throw "DLL not found in release zip"
}

$destDir = Join-Path $JellyfinPluginsPath $pluginFolderName
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Copy-Item $dll.FullName -Destination (Join-Path $destDir $dll.Name) -Force

Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Installed to: $destDir" -ForegroundColor Green
Write-Host "Restart Jellyfin, then configure under Dashboard -> Plugins -> AI Recommendations." -ForegroundColor Cyan

if ($RestartJellyfin) {
    $service = Get-Service -Name "Jellyfin Server" -ErrorAction SilentlyContinue
    if ($service) {
        Restart-Service "Jellyfin Server"
        Write-Host "Jellyfin Server restarted." -ForegroundColor Green
    }
}
