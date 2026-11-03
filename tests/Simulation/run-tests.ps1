# run-tests.ps1 - Sankalpa Test Runner
# Runs the full security simulation, security regression tests, core test suite, and extension tests.

$ErrorActionPreference = "Continue"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Sankalpa Isolated Security Simulation & Test Runner" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = (Resolve-Path (Join-Path $scriptDir "..\..")).Path

$results = @()
$totalPassed = 0
$totalFailed = 0

function Record-Result($suiteName, $status, $details) {
    $global:results += [PSCustomObject]@{
        Suite = $suiteName
        Status = $status
        Details = $details
    }
}

# 1. Run Security Regression & Simulation Tests
Write-Host "`n[1/3] Running Sankalpa Security Regression & Simulation Tests..." -ForegroundColor Yellow
$securityCsproj = Join-Path $rootDir "tests\Sankalpa.Security.Tests\Sankalpa.Security.Tests.csproj"
if (Test-Path $securityCsproj) {
    dotnet test $securityCsproj --verbosity normal --logger "console;verbosity=detailed"
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  [PASS] Sankalpa.Security.Tests succeeded." -ForegroundColor Green
        Record-Result "Sankalpa.Security.Tests" "PASS" "All security unit and simulation tests passed."
        $totalPassed++
    } else {
        Write-Host "  [FAIL] Sankalpa.Security.Tests failed." -ForegroundColor Red
        Record-Result "Sankalpa.Security.Tests" "FAIL" "One or more security tests failed."
        $totalFailed++
    }
} else {
    Write-Host "  [SKIP] Sankalpa.Security.Tests project not found." -ForegroundColor DarkYellow
    Record-Result "Sankalpa.Security.Tests" "NOT RUN" "Project file not found."
}

# 2. Run Core Engine & Application Logic Tests
Write-Host "`n[2/3] Running Sankalpa Core Regression Tests..." -ForegroundColor Yellow
$coreCsproj = Join-Path $rootDir "tests\Sankalpa.Tests\Sankalpa.Tests.csproj"
if (Test-Path $coreCsproj) {
    dotnet test $coreCsproj --verbosity quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  [PASS] Sankalpa.Tests succeeded." -ForegroundColor Green
        Record-Result "Sankalpa.Tests" "PASS" "Baseline core engine tests passed."
        $totalPassed++
    } else {
        Write-Host "  [FAIL] Sankalpa.Tests failed." -ForegroundColor Red
        Record-Result "Sankalpa.Tests" "FAIL" "One or more baseline core tests failed."
        $totalFailed++
    }
} else {
    Write-Host "  [SKIP] Sankalpa.Tests project not found." -ForegroundColor DarkYellow
    Record-Result "Sankalpa.Tests" "NOT RUN" "Project file not found."
}

# 3. Run Browser Extension & Heartbeat Scenarios (Node.js)
Write-Host "`n[3/3] Running Extension & Heartbeat Timeout Scenarios (Node.js)..." -ForegroundColor Yellow
$nodeTests = Get-ChildItem -Path (Join-Path $scriptDir "scenarios") -Filter "*.test.mjs" -ErrorAction SilentlyContinue
$extTests = Get-ChildItem -Path (Join-Path $rootDir "extension") -Filter "*.test.mjs" -ErrorAction SilentlyContinue
$allMjs = @($nodeTests) + @($extTests)

if ($allMjs.Count -gt 0) {
    $nodeFailed = $false
    foreach ($testFile in $allMjs) {
        Write-Host "  Executing: $($testFile.Name)" -ForegroundColor Cyan
        node --test $testFile.FullName
        if ($LASTEXITCODE -ne 0) {
            $nodeFailed = $true
        }
    }

    if (-not $nodeFailed) {
        Write-Host "  [PASS] Node.js extension and heartbeat scenarios passed." -ForegroundColor Green
        Record-Result "Extension & Heartbeat Scenarios" "PASS" "All JavaScript heartbeat and staleness tests passed."
        $totalPassed++
    } else {
        Write-Host "  [FAIL] One or more Node.js extension scenarios failed." -ForegroundColor Red
        Record-Result "Extension & Heartbeat Scenarios" "FAIL" "Staleness or heartbeat test failed."
        $totalFailed++
    }
} else {
    Write-Host "  [SKIP] No .test.mjs files found in simulation or extension." -ForegroundColor DarkYellow
    Record-Result "Extension & Heartbeat Scenarios" "NOT RUN" "No test files found."
}

# Summary Report
Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "                 TEST EXECUTION SUMMARY" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
$results | Format-Table -AutoSize

if ($totalFailed -gt 0) {
    Write-Host "Simulation run completed with $totalFailed FAILURE(S). Total Passed: $totalPassed`n" -ForegroundColor Red
    exit 1
} else {
    Write-Host "All test suites PASSED successfully ($totalPassed passed, 0 failed).`n" -ForegroundColor Green
    exit 0
}
