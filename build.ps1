# Build Jellyfin.Plugin.AIRecommendations (Release)
$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot "Jellyfin.Plugin.AIRecommendations.csproj"
$OutputDir = Join-Path $ProjectRoot "bin\Release\net8.0"
$DllName = "Jellyfin.Plugin.AIRecommendations.dll"
$ZipName = "Jellyfin.Plugin.AIRecommendations.zip"

Write-Host "=== AI Recommendations Plugin Build ===" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: .NET SDK not found. Install .NET 8 SDK:" -ForegroundColor Red
    Write-Host "  winget install Microsoft.DotNet.SDK.8" -ForegroundColor Yellow
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
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $dllPath -DestinationPath $zipPath -Force

$hash = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLower()
Write-Host ""
Write-Host "Build successful!" -ForegroundColor Green
Write-Host "  DLL: $dllPath"
Write-Host "  ZIP: $zipPath"
Write-Host "  MD5: $hash"
Write-Host ""
Write-Host "Deploy locally:  .\deploy-local.ps1" -ForegroundColor Cyan
