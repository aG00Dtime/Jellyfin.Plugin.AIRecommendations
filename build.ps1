# Build Jellyfin.Plugin.AIRecommendations (Release)
$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot "Jellyfin.Plugin.AIRecommendations.csproj"
$OutputDir = Join-Path $ProjectRoot "bin\Release\net9.0"
$DllName = "Jellyfin.Plugin.AIRecommendations.dll"
$ZipName = "Jellyfin.Plugin.AIRecommendations.zip"

Write-Host "=== AI Recommendations Plugin Build ===" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: .NET SDK not found. Install .NET 9 SDK:" -ForegroundColor Red
    Write-Host "  winget install Microsoft.DotNet.SDK.9" -ForegroundColor Yellow
    exit 1
}

Write-Host "dotnet version: $(dotnet --version)"

dotnet restore $ProjectFile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $ProjectFile -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dllPath = Join-Path $OutputDir $DllName
if (-not (Test-Path $dllPath)) {
    Write-Host "ERROR: Build output not found at $dllPath" -ForegroundColor Red
    exit 1
}

$zipPath = Join-Path $OutputDir $ZipName

# Generate meta.json (required by Jellyfin 10.10+ to read plugin metadata)
$semver = (Get-Content (Join-Path $ProjectRoot 'VERSION.txt') -Raw).Trim()
$manifest = Get-Content (Join-Path $ProjectRoot 'manifest.json') -Raw | ConvertFrom-Json
$plugin = if ($manifest -is [System.Array]) { $manifest[0] } else { $manifest }
$entry = $plugin.versions | Where-Object { $_.version -eq "$semver.0" } | Select-Object -First 1
$targetAbi = if ($entry -and $entry.targetAbi) { $entry.targetAbi } else { "10.11.0.0" }

$meta = [ordered]@{
    category    = "General"
    changelog   = if ($entry -and $entry.changelog) { $entry.changelog } else { "Build $semver" }
    description = "Per-user AI movie and TV recommendations synced to virtual libraries on all Jellyfin clients"
    guid        = "7c4a9e2b-3f1d-4a8c-b6e5-2d9f8a1c0b3e"
    imageUrl    = $null
    name        = "AI Recommendations"
    overview    = "LLM-powered recommendations via OpenAI, OpenRouter, or Ollama"
    owner       = "aG00Dtime"
    targetAbi   = $targetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version     = "$semver.0"
}
$metaPath = Join-Path $OutputDir "meta.json"
$meta | ConvertTo-Json | Set-Content -Path $metaPath -Encoding UTF8

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $dllPath, $metaPath -DestinationPath $zipPath -Force

$hash = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLower()
& (Join-Path $PSScriptRoot "scripts\sync-manifest-checksum.ps1") -ZipPath $zipPath
Write-Host ""
Write-Host "Build successful!" -ForegroundColor Green
Write-Host "  DLL: $dllPath"
Write-Host "  ZIP: $zipPath"
Write-Host "  MD5: $hash"
Write-Host ""
Write-Host "Deploy locally:  .\deploy-local.ps1" -ForegroundColor Cyan
