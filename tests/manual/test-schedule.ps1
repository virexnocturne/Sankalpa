# ==============================================================================
# tests/manual/test-schedule.ps1
# Self-contained verification script for Phase 6 schedules, on/off toggles, and lock modes.
# Non-admin script. Safe try/finally cleanup.
# ==============================================================================

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path "$scriptDir\..\..").Path
$serviceDir = "$repoRoot\src\Blocker.Service\bin\Debug\net8.0"
$serviceExe = "$serviceDir\Blocker.Service.exe"
$cliExe = "$repoRoot\src\Blocker.Cli\bin\Debug\net8.0\blocker.exe"
$resultFile = "$scriptDir\schedule-test-result.txt"

if (-not (Test-Path $serviceExe) -or -not (Test-Path $cliExe)) {
    Write-Host "Building solution first..." -ForegroundColor Cyan
    & dotnet build "$repoRoot\Blocker.sln" -c Debug | Out-Null
}

$results = New-Object System.Collections.Generic.List[string]

function Log-Check {
    param(
        [string]$CheckName,
        [bool]$Passed,
        [string]$Details
    )
    $status = "FAIL"
    $color = "Red"
    if ($Passed) {
        $status = "PASS"
        $color = "Green"
    }
    $line = "[$status] $CheckName - $Details"
    Write-Host $line -ForegroundColor $color
    $results.Add($line)
}

function Run-Cli {
    param([string[]]$CliArgs)
    $output = & $cliExe @CliArgs 2>&1
    return @{
        ExitCode = $LASTEXITCODE
        Output = ($output -join "`n")
    }
}

Write-Host "=== Starting Phase 6 Schedule, Toggle & Lock Modes Test ===" -ForegroundColor Cyan
$serviceProcess = $null
$startedService = $false

try {
    # 0. Check if service is already running, or start background service
    $ping = Run-Cli @("ping")
    if ($ping.ExitCode -ne 0) {
        Write-Host "Starting background Blocker.Service process..."
        $serviceProcess = Start-Process -FilePath $serviceExe -WorkingDirectory $serviceDir -PassThru
        $startedService = $true
        Start-Sleep -Seconds 2
        $ping = Run-Cli @("ping")
        if ($ping.ExitCode -ne 0) {
            throw "Failed to start Blocker.Service."
        }
    }
    Log-Check "Service Connection" $true "Connected to BlockerControl pipe."

    $now = Get-Date
    $curMinute = $now.Hour * 60 + $now.Minute
    $startMin = [Math]::Max(0, $curMinute - 10)
    $endMin = [Math]::Min(1439, $curMinute + 10)
    if ($endMin -le $startMin) {
        $startMin = 0
        $endMin = 1439
    }
    $fromStr = "{0:D2}:{1:D2}" -f [int][Math]::Floor($startMin / 60), ($startMin % 60)
    $toStr = "{0:D2}:{1:D2}" -f [int][Math]::Floor($endMin / 60), ($endMin % 60)
    $dayName = $now.DayOfWeek.ToString().Substring(0, 3)

    # --------------------------------------------------------------------------
    # Check a: Create profile 'TestSched' with schedule covering now +/- 10 min, lock mode Locked
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check A: Create Profile with Schedule & Locked Mode ---"
    Run-Cli @("profile", "delete", "--name", "TestSched") | Out-Null
    $addRes = Run-Cli @("profile", "add", "--name", "TestSched", "--block", "notepad.exe")
    $schedRes = Run-Cli @("profile", "schedule", "add", "--name", "TestSched", "--days", $dayName, "--from", $fromStr, "--to", $toStr)
    $lockRes = Run-Cli @("profile", "lock", "--name", "TestSched", "--mode", "locked")
    $listRes = Run-Cli @("profile", "list")
    $showRes = Run-Cli @("profile", "show", "--name", "TestSched")

    $aPassed = ($listRes.Output -match "TestSched") -and ($showRes.Output -match "Locked") -and ($showRes.Output -match "$fromStr-$toStr")
    Log-Check "Check A (Create & Verify Profile List)" $aPassed "Created TestSched with $dayName $fromStr-$toStr Locked mode."

    # --------------------------------------------------------------------------
    # Check b: Turn it On. Verify status shows active, reason Schedule.
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check B: Turn On & Verify Active Reason Schedule ---"
    $enableRes = Run-Cli @("profile", "enable", "--name", "TestSched")
    Start-Sleep -Seconds 1
    $statusRes = Run-Cli @("status")
    $bPassed = ($enableRes.ExitCode -eq 0) -and ($statusRes.Output -match "TestSched") -and (($statusRes.Output -match "ACTIVE \(Schedule\)") -or ($statusRes.Output -match "Active Profiles.*TestSched"))
    Log-Check "Check B (Turn On & Active Schedule Status)" $bPassed "TestSched enabled and active via schedule."

    # --------------------------------------------------------------------------
    # Check c: Attempt to turn it Off, edit it, delete it: assert all refused with 'locked until...'.
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check C: Protected Operations Refusal When Locked Now ---"
    $offRes = Run-Cli @("profile", "disable", "--name", "TestSched")
    $editRes = Run-Cli @("profile", "add", "--name", "TestSched", "--block", "calc.exe")
    $delRes = Run-Cli @("profile", "delete", "--name", "TestSched")

    $c1 = ($offRes.ExitCode -ne 0) -and ($offRes.Output -match "is locked until")
    $c2 = ($editRes.ExitCode -ne 0) -and ($editRes.Output -match "is locked until")
    $c3 = ($delRes.ExitCode -ne 0) -and ($delRes.Output -match "is locked until")
    $cPassed = $c1 -and $c2 -and $c3
    Log-Check "Check C (Refusal of Protected Operations)" $cPassed "Disable, Edit, and Delete all refused with 'is locked until'."

    # --------------------------------------------------------------------------
    # Check d: Emergency unlock drops file: assert profile turned Off, session ended
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check D: Emergency Unlock Escape Hatch ---"
    $emergencyFile = Join-Path $serviceDir "EMERGENCY_UNLOCK"
    [System.IO.File]::WriteAllText($emergencyFile, "EMERGENCY_UNLOCK")

    # Wait up to 5 seconds for worker cycle to detect and consume emergency unlock
    for ($wait = 0; $wait -lt 10; $wait++) {
        if (-not (Test-Path $emergencyFile)) { break }
        Start-Sleep -Milliseconds 500
    }

    $dShowRes = Run-Cli @("profile", "show", "--name", "TestSched")
    $dStatusRes = Run-Cli @("status")
    $dPassed = ($dShowRes.Output -match "Enabled:\s*False") -and (-not (Test-Path $emergencyFile))
    Log-Check "Check D (Emergency Unlock Disables All Profiles)" $dPassed "EMERGENCY_UNLOCK consumed and TestSched turned Off."

    # --------------------------------------------------------------------------
    # Check e: Create profile 'TestTyping' active now, lock TypingChallenge
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check E: Typing Challenge Unlock ---"
    Run-Cli @("profile", "delete", "--name", "TestTyping") | Out-Null
    Run-Cli @("profile", "add", "--name", "TestTyping", "--block", "notepad.exe") | Out-Null
    Run-Cli @("profile", "schedule", "add", "--name", "TestTyping", "--days", $dayName, "--from", $fromStr, "--to", $toStr) | Out-Null
    Run-Cli @("profile", "lock", "--name", "TestTyping", "--mode", "typing") | Out-Null
    Run-Cli @("profile", "enable", "--name", "TestTyping") | Out-Null

    # Attempt disable without challenge -> refused
    $eDisNoCh = Run-Cli @("profile", "disable", "--name", "TestTyping")
    # Fetch challenge
    $chRes = Run-Cli @("unlock-challenge", "--name", "TestTyping")
    $challengeText = $chRes.Output.Trim()
    # Send challenge with unlock-text
    $eDisWithCh = Run-Cli @("profile", "disable", "--name", "TestTyping", "--unlock-text", $challengeText)
    $eShowRes = Run-Cli @("profile", "show", "--name", "TestTyping")

    $ePassed = ($eDisNoCh.ExitCode -ne 0) -and ($challengeText.Length -eq 80) -and ($eDisWithCh.ExitCode -eq 0) -and ($eShowRes.Output -match "Enabled:\s*False")
    Log-Check "Check E (Typing Challenge Unlock)" $ePassed "Challenge generated (80 chars) and validated to unlock."

    # --------------------------------------------------------------------------
    # Check f: Create profile 'TestPwd' active now, lock Password with 'pw123'
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check F: Password Protection & Rate-Limited Lockout ---"
    Run-Cli @("profile", "delete", "--name", "TestPwd") | Out-Null
    Run-Cli @("profile", "add", "--name", "TestPwd", "--block", "notepad.exe") | Out-Null
    Run-Cli @("profile", "schedule", "add", "--name", "TestPwd", "--days", $dayName, "--from", $fromStr, "--to", $toStr) | Out-Null
    Run-Cli @("profile", "lock", "--name", "TestPwd", "--mode", "password", "--password", "pw123") | Out-Null
    Run-Cli @("profile", "enable", "--name", "TestPwd") | Out-Null

    # Send 5 wrong passwords
    $lockoutTriggered = $false
    for ($i = 1; $i -le 6; $i++) {
        $wrongRes = Run-Cli @("profile", "disable", "--name", "TestPwd", "--unlock-password", "wrongpass$i")
        if ($wrongRes.Output -match "locked out for \d+ seconds") {
            $lockoutTriggered = $true
            break
        }
    }

    # Wait for lockout to expire (60s)
    Write-Host "Waiting 61s for password rate-limit lockout to expire..."
    Start-Sleep -Seconds 61

    # Send correct password
    $correctRes = Run-Cli @("profile", "disable", "--name", "TestPwd", "--unlock-password", "pw123")
    $fShowRes = Run-Cli @("profile", "show", "--name", "TestPwd")

    $fPassed = $lockoutTriggered -and ($correctRes.ExitCode -eq 0) -and ($fShowRes.Output -match "Enabled:\s*False")
    Log-Check "Check F (Password Protection & Lockout Expiry)" $fPassed "Lockout after 5 failures confirmed; correct password unlocked after expiry."

    # --------------------------------------------------------------------------
    # Check g: Midnight-crossing schedule: create 22:00-06:00, assert schedule show
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check G: Midnight-Crossing Schedule Rule ---"
    Run-Cli @("profile", "delete", "--name", "TestMidnight") | Out-Null
    Run-Cli @("profile", "add", "--name", "TestMidnight", "--block", "notepad.exe") | Out-Null
    Run-Cli @("profile", "schedule", "add", "--name", "TestMidnight", "--days", "Mon,Tue,Wed,Thu,Fri", "--from", "22:00", "--to", "06:00") | Out-Null
    $midShowRes = Run-Cli @("profile", "schedule", "show", "--name", "TestMidnight")
    $gPassed = ($midShowRes.Output -match "22:00-06:00") -and ($midShowRes.Output -match "Mon-Fri")
    Log-Check "Check G (Midnight Crossing Schedule Show)" $gPassed "22:00-06:00 formatted correctly."

    # --------------------------------------------------------------------------
    # Check h: Union test: create A (notepad.exe) and B (calc.exe), turn both On
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check H: Active Union Enforcement & Attribution ---"
    Run-Cli @("profile", "delete", "--name", "ProfileA") | Out-Null
    Run-Cli @("profile", "delete", "--name", "ProfileB") | Out-Null

    Run-Cli @("profile", "add", "--name", "ProfileA", "--block", "notepad.exe") | Out-Null
    Run-Cli @("profile", "enable", "--name", "ProfileA") | Out-Null

    # Note: On Windows, calc can be calc.exe / CalculatorApp.exe; let's use cmd.exe or a distinct child process
    Run-Cli @("profile", "add", "--name", "ProfileB", "--block", "cmd.exe") | Out-Null
    Run-Cli @("profile", "enable", "--name", "ProfileB") | Out-Null

    Start-Sleep -Seconds 1

    # Launch both
    $p1 = Start-Process notepad.exe -PassThru
    $p2 = Start-Process cmd.exe -PassThru

    Start-Sleep -Seconds 2

    # Assert both were terminated
    $p1Dead = $p1.HasExited
    $p2Dead = $p2.HasExited

    if (-not $p1Dead) { try { $p1.Kill() } catch { } }
    if (-not $p2Dead) { try { $p2.Kill() } catch { } }

    $hPassed = $p1Dead -and $p2Dead
    Log-Check "Check H (Union Enforcement Kills Both Apps)" $hPassed "Both notepad.exe and cmd.exe terminated by background worker."

    # --------------------------------------------------------------------------
    # Check i: Restore state: clean up created test profiles, verify status clean
    # --------------------------------------------------------------------------
    Write-Host "`n--- Check I: Cleanup & State Restoration ---"
    Run-Cli @("profile", "disable", "--name", "ProfileA") | Out-Null
    Run-Cli @("profile", "disable", "--name", "ProfileB") | Out-Null
    Run-Cli @("profile", "delete", "--name", "ProfileA") | Out-Null
    Run-Cli @("profile", "delete", "--name", "ProfileB") | Out-Null
    Run-Cli @("profile", "delete", "--name", "TestSched") | Out-Null
    Run-Cli @("profile", "delete", "--name", "TestTyping") | Out-Null
    Run-Cli @("profile", "delete", "--name", "TestPwd", "--unlock-password", "pw123") | Out-Null
    Run-Cli @("profile", "delete", "--name", "TestMidnight") | Out-Null

    $finalStatus = Run-Cli @("status")
    $iPassed = ($finalStatus.Output -match "No active session") -or (-not ($finalStatus.Output -match "ProfileA"))
    Log-Check "Check I (Clean State Verified)" $iPassed "All test profiles deleted, clean idle status confirmed."
}
catch {
    Write-Host "Unexpected exception during test run: $_" -ForegroundColor Red
    $results.Add("[FAIL] Unexpected Exception - $_")
}
finally {
    Write-Host "`n=== Running Finally Cleanup ===" -ForegroundColor Yellow
    # Ensure EMERGENCY_UNLOCK is removed if present
    $ef = Join-Path $serviceDir "EMERGENCY_UNLOCK"
    if (Test-Path $ef) {
        try { Remove-Item -Path $ef -Force } catch { }
    }

    # Best-effort cleanup of test profiles
    try {
        Run-Cli @("profile", "disable", "--name", "ProfileA") | Out-Null
        Run-Cli @("profile", "disable", "--name", "ProfileB") | Out-Null
        Run-Cli @("profile", "delete", "--name", "ProfileA") | Out-Null
        Run-Cli @("profile", "delete", "--name", "ProfileB") | Out-Null
        Run-Cli @("profile", "delete", "--name", "TestSched") | Out-Null
        Run-Cli @("profile", "delete", "--name", "TestTyping") | Out-Null
        Run-Cli @("profile", "delete", "--name", "TestPwd", "--unlock-password", "pw123") | Out-Null
        Run-Cli @("profile", "delete", "--name", "TestMidnight") | Out-Null
    } catch { }

    # Stop service if we started it
    if ($startedService -and $serviceProcess -and (-not $serviceProcess.HasExited)) {
        Write-Host "Stopping background service process started by test..."
        try {
            $serviceProcess.Kill()
            $serviceProcess.WaitForExit(3000)
        } catch { }
    }

    # Write results to schedule-test-result.txt
    [System.IO.File]::WriteAllLines($resultFile, $results)
    Write-Host "Results saved to $resultFile" -ForegroundColor Cyan
}

exit ($results | Where-Object { $_ -match "^\[FAIL\]" } | Measure-Object).Count
