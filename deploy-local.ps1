# Deploy plugin DLL to local Jellyfin plugins folder
param(
    [string]$JellyfinPluginsPath = "",
    [switch]$RestartJellyfin
)

$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$DllName = "Jellyfin.Plugin.AIRecommendations.dll"
$SourceDll = Join-Path $ProjectRoot "bin\Release\net9.0\$DllName"
$version = (Get-Content (Join-Path $ProjectRoot "VERSION.txt") -Raw).Trim()
$PluginFolderName = "AI Recommendations_$version.0"

if (-not (Test-Path $SourceDll)) {
    Write-Host "DLL not found. Running build first..." -ForegroundColor Yellow
    & (Join-Path $ProjectRoot "build.ps1")
}

if ([string]::IsNullOrWhiteSpace($JellyfinPluginsPath)) {
    $defaultPath = Join-Path $env:APPDATA "Jellyfin\Server\plugins"
    if (Test-Path $defaultPath) {
        $JellyfinPluginsPath = $defaultPath
    } else {
        Write-Host "Jellyfin plugins folder not found at default location." -ForegroundColor Red
        Write-Host "Pass -JellyfinPluginsPath 'C:\path\to\jellyfin\plugins'" -ForegroundColor Yellow
        exit 1
    }
}

$destDir = Join-Path $JellyfinPluginsPath $PluginFolderName
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

Copy-Item $SourceDll -Destination (Join-Path $destDir $DllName) -Force

Write-Host "Deployed to: $destDir" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Restart Jellyfin server"
Write-Host "  2. Dashboard -> Plugins -> AI Recommendations"
Write-Host "  3. Configure API keys + TMDB key"
Write-Host "  4. Dashboard -> Scheduled Tasks -> AI Recommendations Sync"

if ($RestartJellyfin) {
    $service = Get-Service -Name "Jellyfin Server" -ErrorAction SilentlyContinue
    if ($service) {
        Write-Host "Restarting Jellyfin Server service..." -ForegroundColor Yellow
        Restart-Service "Jellyfin Server"
        Write-Host "Service restarted." -ForegroundColor Green
    } else {
        Write-Host "Jellyfin Server Windows service not found — restart Jellyfin manually." -ForegroundColor Yellow
    }
}
