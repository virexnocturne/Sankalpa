# run-dev.ps1 - Development launcher for Blocker Service and Desktop UI

$repoRoot = $PSScriptRoot

# Elevation check
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "WARNING: Not running as Administrator. Website blocking via hosts file will NOT work. Run PowerShell as Administrator to enable website blocking."
} else {
    Write-Host "Running as Administrator. Full process and website blocking enabled." -ForegroundColor Green
}

Write-Host "Building Blocker solution..." -ForegroundColor Cyan
dotnet build "$repoRoot\Blocker.sln" -c Debug
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed. Aborting."
    exit 1
}

$serviceExe = "$repoRoot\src\Blocker.Service\bin\Debug\net8.0\Blocker.Service.exe"
$serviceDir = Split-Path $serviceExe
$uiExe = "$repoRoot\src\Blocker.App\bin\Debug\net8.0-windows\Sankalpa.exe"
$uiDir = Split-Path $uiExe

# Check if service is already running
$existingService = Get-Process -Name "Blocker.Service" -ErrorAction SilentlyContinue
if (-not $existingService) {
    Write-Host "Starting Blocker.Service in a new window..." -ForegroundColor Cyan
    Start-Process -FilePath $serviceExe -WorkingDirectory $serviceDir
    Start-Sleep -Seconds 1
} else {
    Write-Host "Blocker.Service is already running (PID: $($existingService[0].Id))." -ForegroundColor Yellow
}

Write-Host "Launching Blocker UI..." -ForegroundColor Green
Start-Process -FilePath $uiExe -WorkingDirectory $uiDir
