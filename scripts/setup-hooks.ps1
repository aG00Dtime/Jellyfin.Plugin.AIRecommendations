$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot | Split-Path -Parent
Set-Location $Root

git config core.hooksPath .githooks
Write-Host "Git hooks path set to .githooks" -ForegroundColor Green
Write-Host ""
Write-Host "  pre-commit  verify VERSION.txt matches csproj + manifest"
Write-Host "  pre-push    bump patch version and commit before push"
Write-Host ""
Write-Host "Skip auto-bump: `$env:SKIP_VERSION_BUMP=1; git push"
