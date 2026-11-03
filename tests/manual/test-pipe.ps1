# test-pipe.ps1 - Manual verification script for Phase 4 Named Pipe Control Channel
# Can run from a standard non-admin PowerShell.

$results = [System.Collections.Generic.List[string]]::new()

function Record-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )
    $status = "FAIL"
    if ($Passed) {
        $status = "PASS"
    }
    $line = "[$status] ${Name}: $Detail"
    $results.Add($line)
    Write-Host $line
}

$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$serviceExe = "$repoRoot\src\Blocker.Service\bin\Debug\net8.0\Blocker.Service.exe"
$cliExe = "$repoRoot\src\Blocker.Cli\bin\Debug\net8.0\blocker.exe"
$serviceDir = Split-Path $serviceExe
$emergencyFile = Join-Path $serviceDir "EMERGENCY_UNLOCK"
$resultFile = Join-Path $PSScriptRoot "pipe-test-result.txt"

$serviceProc = $null

try {
    Write-Host "Building solution to ensure latest binaries..."
    dotnet build "$repoRoot\Blocker.sln" -c Debug | Out-Null

    # Check 1: Blocker.Cli.csproj does not reference Sqlite
    $cliCsprojPath = "$repoRoot\src\Blocker.Cli\Blocker.Cli.csproj"
    $cliCsprojContent = [System.IO.File]::ReadAllText($cliCsprojPath)
    $hasSqlite = $cliCsprojContent -match "Microsoft\.Data\.Sqlite"
    Record-Check "Check 1 - Blocker.Cli has no Sqlite reference" (-not $hasSqlite) "Sqlite found in csproj: $hasSqlite"

    # Start the service in the background
    Write-Host "Starting Blocker Service in background..."
    $serviceProc = Start-Process -FilePath $serviceExe -WorkingDirectory $serviceDir -PassThru
    Start-Sleep -Seconds 2

    # Check 2: Run ping
    $pingOut = (& $cliExe ping 2>&1) | Out-String
    $pingExit = $LASTEXITCODE
    $pingPass = ($pingExit -eq 0 -and $pingOut -match "pong")
    Record-Check "Check 2 - CLI ping command" $pingPass "ExitCode=$pingExit, Output='$($pingOut.Trim())'"

    # Check 3: Create profile
    $createOut = (& $cliExe profile add --name PipeTestProfile --block calc.exe,notepad.exe --site example.com 2>&1) | Out-String
    $createExit = $LASTEXITCODE
    $createPass = ($createExit -eq 0 -and $createOut -match "PipeTestProfile")
    Record-Check "Check 3 - Create profile" $createPass "ExitCode=$createExit, Output='$($createOut.Trim())'"

    # Check 4: Start 20-second locked session
    $startOut = (& $cliExe start --profile PipeTestProfile --seconds 20 --locked 2>&1) | Out-String
    $startExit = $LASTEXITCODE
    $startPass = ($startExit -eq 0 -and $startOut -match "Session started")
    Record-Check "Check 4 - Start 20s locked session" $startPass "ExitCode=$startExit, Output='$($startOut.Trim())'"

    # Check 5: Confirm stop is refused
    $stopOut = (& $cliExe stop 2>&1) | Out-String
    $stopExit = $LASTEXITCODE
    $stopPass = ($stopExit -eq 1 -and $stopOut -match "refused")
    Record-Check "Check 5 - Locked stop refused" $stopPass "ExitCode=$stopExit, Output='$($stopOut.Trim())'"

    # Check 6: Confirm editing profile during locked session is refused
    $editOut = (& $cliExe profile add --name PipeTestProfile --block calc.exe 2>&1) | Out-String
    $editExit = $LASTEXITCODE
    $editPass = ($editExit -eq 1 -and $editOut -match "Refused")
    Record-Check "Check 6 - Edit profile during locked session refused" $editPass "ExitCode=$editExit, Output='$($editOut.Trim())'"

    # Check 7: Send raw garbage to the pipe with NamedPipeClientStream and confirm ping still works
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "BlockerControl", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(2000)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.WriteLine("THIS_IS_RAW_GARBAGE_MALFORMED_NOT_JSON")
    $writer.Flush()
    $pipe.Dispose()
    Start-Sleep -Milliseconds 300

    $pingAfterGarbage = (& $cliExe ping 2>&1) | Out-String
    $pingGarbageExit = $LASTEXITCODE
    $pingGarbagePass = ($pingGarbageExit -eq 0 -and $pingAfterGarbage -match "pong")
    Record-Check "Check 7 - Raw garbage rejected and ping still works" $pingGarbagePass "ExitCode=$pingGarbageExit, Output='$($pingAfterGarbage.Trim())'"

    # Check 8: Kill service mid-session, restart it, and confirm status shows session resumed
    if ($serviceProc -and -not $serviceProc.HasExited) {
        Stop-Process -Id $serviceProc.Id -Force
    }
    Start-Sleep -Seconds 1
    $serviceProc = Start-Process -FilePath $serviceExe -WorkingDirectory $serviceDir -PassThru
    Start-Sleep -Seconds 2

    $statusOut = (& $cliExe status 2>&1) | Out-String
    $statusExit = $LASTEXITCODE
    $statusPass = ($statusExit -eq 0 -and $statusOut -match "Active Session" -and $statusOut -match "PipeTestProfile")
    Record-Check "Check 8 - Service restart resumes active session" $statusPass "ExitCode=$statusExit, Output='$($statusOut.Trim())'"

    # Check 9: Confirm second service instance exits
    $secondInstance = Start-Process -FilePath $serviceExe -WorkingDirectory $serviceDir -Wait -PassThru -NoNewWindow
    $secondExit = $secondInstance.ExitCode
    $secondPass = ($secondExit -ne 0)
    Record-Check "Check 9 - Second service instance exits immediately" $secondPass "ExitCode=$secondExit"

    # Check 10: Confirm CLI prints 'Service is not running' with exit code 2 once service is stopped
    [System.IO.File]::WriteAllText($emergencyFile, "UNLOCK")
    Start-Sleep -Milliseconds 500
    if ($serviceProc -and -not $serviceProc.HasExited) {
        Stop-Process -Id $serviceProc.Id -Force
    }
    Start-Sleep -Seconds 1

    $stoppedOut = (& $cliExe ping 2>&1) | Out-String
    $stoppedExit = $LASTEXITCODE
    $stoppedPass = ($stoppedExit -eq 2 -and $stoppedOut -match "Service is not running")
    Record-Check "Check 10 - CLI prints 'Service is not running' with exit code 2" $stoppedPass "ExitCode=$stoppedExit, Output='$($stoppedOut.Trim())'"
}
finally {
    # Emergency cleanup: ensure sessions unlocked and service stopped
    try {
        [System.IO.File]::WriteAllText($emergencyFile, "UNLOCK")
    } catch { }

    if ($serviceProc -and -not $serviceProc.HasExited) {
        try {
            Stop-Process -Id $serviceProc.Id -Force
        } catch { }
    }

    # Ensure any remaining Blocker.Service processes are terminated
    Get-Process -Name "Blocker.Service" -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force } catch { }
    }

    Start-Sleep -Milliseconds 500
    if ([System.IO.File]::Exists($emergencyFile)) {
        try { [System.IO.File]::Delete($emergencyFile) } catch { }
    }

    # Write results file
    [System.IO.File]::WriteAllLines($resultFile, $results)
    Write-Host "Results written to $resultFile"
}
