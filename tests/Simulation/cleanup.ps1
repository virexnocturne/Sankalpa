# cleanup.ps1 - Sankalpa Security Simulation Environment Cleanup
# Removes only temporary simulation artifacts, never touching actual application data or system configuration.

$ErrorActionPreference = "Continue"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Sankalpa Simulation Environment: Cleanup" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$tempDir = Join-Path $scriptDir "fixtures\temp"

if (Test-Path $tempDir) {
    Write-Host "Removing temporary simulation fixtures: $tempDir" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $tempDir
    Write-Host "  [OK] Cleaned temporary simulation fixtures." -ForegroundColor Green
} else {
    Write-Host "  [INFO] No temporary simulation fixtures found to clean." -ForegroundColor Gray
}

# Remove any test output logs created in Simulation directory
$testLogs = Get-ChildItem -Path $scriptDir -Filter "*.log" -ErrorAction SilentlyContinue
foreach ($log in $testLogs) {
    Remove-Item -Force $log.FullName
    Write-Host "  [OK] Removed test log: $($log.Name)" -ForegroundColor Green
}

Write-Host "Cleanup completed safely. Real host environment remained completely untouched.`n" -ForegroundColor Green
exit 0
