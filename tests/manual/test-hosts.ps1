# ==============================================================================
# tests/manual/test-hosts.ps1
# Self-contained verification script for Phase 3 Windows hosts file website blocking.
# MUST BE RUN FROM AN ELEVATED (ADMINISTRATOR) POWERSHELL CONSOLE.
# ==============================================================================

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path "$scriptDir\..\..").Path
$serviceProj = "$repoRoot\src\Blocker.Service"
$resultFile = "$scriptDir\hosts-test-result.txt"

# 1. Administrator Elevation Check
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script requires Administrator rights to edit C:\Windows\System32\drivers\etc\hosts. Run PowerShell as Administrator." -ForegroundColor Red
    exit 1
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

function Read-HostsFile {
    param([string]$Path)
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            return [System.IO.File]::ReadAllText($Path)
        }
        catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 300
        }
    }
}

function Write-HostsFile {
    param(
        [string]$Path,
        [string]$Content
    )
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            [System.IO.File]::WriteAllText($Path, $Content)
            return
        }
        catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 300
        }
    }
}

Write-Host "=== Starting Phase 3 Hosts File Website Blocking Test ===" -ForegroundColor Cyan
$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
$backupPath = "$hostsPath.manual-test.bak"
$serviceProcess = $null

try {
    # 2. Back up existing hosts file
    Copy-Item -Path $hostsPath -Destination $backupPath -Force
    $originalHosts = Read-HostsFile $hostsPath
    Log-Check "Hosts Backup" $true "Hosts file backed up to $backupPath."

    # 3. Create test profile blocking example.com
    Write-Host "Creating test profile 'HostsTestProfile'..."
    $createProfileOutput = & dotnet run --project "$serviceProj" -- profile add --name HostsTestProfile --block notepad.exe --site example.com 2>&1
    Log-Check "Profile Creation" ($LASTEXITCODE -eq 0) "Profile created with domain 'example.com'."

    # 4. Start background service worker
    Write-Host "Starting Blocker.Service background process..."
    $serviceProcess = Start-Process dotnet -ArgumentList "run --project `"$serviceProj`"" -PassThru
    Start-Sleep -Seconds 3

    # 5. Start a 1-minute locked session
    Write-Host "Starting 1-minute locked session for HostsTestProfile..."
    $startSessionOutput = & dotnet run --project "$serviceProj" -- start --profile HostsTestProfile --seconds 60 --locked true 2>&1
    Log-Check "Session Start" ($LASTEXITCODE -eq 0) "1-minute locked session started."

    # Allow worker loop to apply the hosts file block (runs every 1s)
    Start-Sleep -Seconds 3

    # 6. Check DNS resolution for example.com
    & ipconfig /flushdns | Out-Null
    $resolvedIps = [System.Net.Dns]::GetHostAddresses("example.com") | Select-Object -ExpandProperty IPAddressToString
    $hasLoopback = $resolvedIps -contains "127.0.0.1"
    Log-Check "DNS Resolution" $hasLoopback "example.com resolved to: ($($resolvedIps -join ', ')). Loopback 127.0.0.1 present: $hasLoopback"

    # 7. Check that unrelated lines in hosts file are untouched
    $currentHosts = Read-HostsFile $hostsPath
    $beginMarker = "# BLOCKER-BEGIN"
    $endMarker = "# BLOCKER-END"
    $beginIdx = $currentHosts.IndexOf($beginMarker)
    $endIdx = $currentHosts.IndexOf($endMarker)

    $hasMarkers = ($beginIdx -ge 0) -and ($endIdx -gt $beginIdx)
    $unrelatedUntouched = $false
    if ($hasMarkers) {
        $cleanHosts = $currentHosts.Substring(0, $beginIdx) + $currentHosts.Substring($endIdx + $endMarker.Length).TrimStart("`r", "`n")
        $unrelatedUntouched = ($cleanHosts.Trim() -eq $originalHosts.Trim())
    }
    Log-Check "Unrelated Lines Preserved" $unrelatedUntouched "Hosts file lines outside of # BLOCKER markers remained intact."

    # 8. Tamper with block and confirm re-application within ~10 seconds
    Write-Host "Tampering with hosts file to test auto-recovery..."
    $tamperedHosts = $currentHosts.Replace("127.0.0.1 example.com", "# TAMPERED LINE")
    Write-HostsFile $hostsPath $tamperedHosts

    Write-Host "Waiting for service to detect tampering and re-apply block (checks every 5s)..."
    $tamperRecovered = $false
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Seconds 1
        $recheckHosts = Read-HostsFile $hostsPath
        if ($recheckHosts.Contains("127.0.0.1 example.com")) {
            $tamperRecovered = $true
            break
        }
    }
    Log-Check "Tamper Auto-Recovery" $tamperRecovered "Service detected hosts tampering and re-applied 127.0.0.1 example.com."

    # 9. Wait for session to end naturally (total duration was 60s, ~40s remaining)
    Write-Host "Waiting for 60s session to end naturally..."
    $sessionEnded = $false
    for ($i = 0; $i -lt 55; $i++) {
        Start-Sleep -Seconds 1
        $statusOutput = & dotnet run --project "$serviceProj" -- status 2>&1
        if ($statusOutput -match "No active session") {
            $sessionEnded = $true
            break
        }
    }
    Start-Sleep -Seconds 2

    # 10. Confirm block is removed and hosts file matches original backup
    $finalHosts = Read-HostsFile $hostsPath
    $blockRemoved = -not $finalHosts.Contains($beginMarker)
    $matchesOriginal = ($finalHosts.Trim() -eq $originalHosts.Trim())

    Log-Check "Block Removal on Session Expiration" $blockRemoved "Blocker markers were removed upon session end."
    Log-Check "Hosts Restored to Original" $matchesOriginal "Final hosts file content matches original backup."

}
finally {
    # 11. End any active session and ensure clean hosts state
    try {
        # Drop EMERGENCY_UNLOCK in both base directory and project output to ensure unlock works
        $emergencyFile1 = "$repoRoot\src\Blocker.Service\bin\Debug\net8.0\EMERGENCY_UNLOCK"
        $emergencyFile2 = "$repoRoot\src\Blocker.Service\EMERGENCY_UNLOCK"
        New-Item -ItemType File -Path $emergencyFile1 -Force -ErrorAction SilentlyContinue | Out-Null
        New-Item -ItemType File -Path $emergencyFile2 -Force -ErrorAction SilentlyContinue | Out-Null

        # Stop active session and clean hosts block
        & dotnet run --project "$serviceProj" -- stop 2>&1 | Out-Null
        & dotnet run --project "$serviceProj" -- hosts-clean 2>&1 | Out-Null

        Remove-Item -Path $emergencyFile1 -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $emergencyFile2 -Force -ErrorAction SilentlyContinue
    }
    catch { }

    # Stop background service worker
    if ($serviceProcess -and -not $serviceProcess.HasExited) {
        Stop-Process -Id $serviceProcess.Id -Force -ErrorAction SilentlyContinue
    }

    # Always restore hosts file from the backup made at the start so a failed run never leaves it modified
    if (Test-Path $backupPath) {
        for ($attempt = 1; $attempt -le 10; $attempt++) {
            try {
                $bakContent = [System.IO.File]::ReadAllText($backupPath)
                [System.IO.File]::WriteAllText($hostsPath, $bakContent)
                Remove-Item -Path $backupPath -Force -ErrorAction SilentlyContinue
                & ipconfig /flushdns | Out-Null
                break
            }
            catch {
                if ($attempt -eq 10) {
                    Write-Warning "Could not restore hosts from backup: $_"
                }
                Start-Sleep -Milliseconds 300
            }
        }
    }

    # Write summary to hosts-test-result.txt
    try {
        Set-Content -Path $resultFile -Value ($results -join "`r`n")
        Write-Host "`nTest results written to: $resultFile" -ForegroundColor Cyan
    }
    catch { }
}
