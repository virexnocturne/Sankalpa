# setup.ps1 - Sankalpa Security Simulation Environment Setup
# Prepares the isolated testing environment without modifying host system configuration.

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Sankalpa Simulation Environment: Setup & Prereqs Check" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Verify .NET SDK
Write-Host "[1/5] Checking .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = dotnet --version
    Write-Host "  [OK] .NET SDK installed: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Error "  [FAIL] .NET SDK is required but not found in PATH."
    exit 1
}

# 2. Verify Node.js
Write-Host "[2/5] Checking Node.js runtime..." -ForegroundColor Yellow
try {
    $nodeVersion = node --version
    Write-Host "  [OK] Node.js installed: $nodeVersion" -ForegroundColor Green
} catch {
    Write-Error "  [FAIL] Node.js is required for extension test simulation."
    exit 1
}

# 3. Create isolated sandbox directories
Write-Host "[3/5] Setting up isolated fixture and scratch directories..." -ForegroundColor Yellow
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$tempDir = Join-Path $scriptDir "fixtures\temp"
if (Test-Path $tempDir) {
    Remove-Item -Recurse -Force $tempDir
}
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
Write-Host "  [OK] Temporary sandbox fixture directory ready: $tempDir" -ForegroundColor Green

# 4. Verify Safety Boundaries
Write-Host "[4/5] Verifying safety boundaries..." -ForegroundColor Yellow
$realHostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
Write-Host "  [SAFETY] Real hosts file at '$realHostsPath' will NOT be touched." -ForegroundColor Cyan
Write-Host "  [SAFETY] Real Windows service 'SankalpaService' will NOT be touched." -ForegroundColor Cyan
Write-Host "  [SAFETY] System DNS and network configuration will NOT be modified." -ForegroundColor Cyan
Write-Host "  [SAFETY] Administrator privileges are NOT required for ordinary test suite." -ForegroundColor Cyan

# 5. Restore dependencies
Write-Host "[5/5] Restoring project dependencies..." -ForegroundColor Yellow
$solutionPath = Join-Path $scriptDir "..\..\Blocker.sln"
dotnet restore $solutionPath --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to restore .NET dependencies."
    exit 1
}
Write-Host "  [OK] Solution dependencies restored successfully." -ForegroundColor Green

Write-Host "`nSimulation environment is fully configured and ready.`n" -ForegroundColor Green
exit 0
