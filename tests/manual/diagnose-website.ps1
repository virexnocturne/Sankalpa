# ==============================================================================
# tests/manual/diagnose-website.ps1
# Administrator Diagnostic Script for YouTube & Website Blocking Investigation
# MUST BE RUN FROM AN ELEVATED (ADMINISTRATOR) POWERSHELL CONSOLE.
# ==============================================================================

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path "$scriptDir\..\..").Path
$cliProj = "$repoRoot\src\Blocker.Cli"
$serviceProj = "$repoRoot\src\Blocker.Service"
$resultFile = "$scriptDir\diagnose-result.txt"

# 1. Administrator Elevation Check
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This diagnostic script requires Administrator rights to inspect the Windows hosts file. Run PowerShell as Administrator." -ForegroundColor Red
    exit 1
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

Write-Host "=== Starting Website Blocking Diagnostics for YouTube ===" -ForegroundColor Cyan
$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
$backupPath = "$hostsPath.diagnose-test.bak"
$startedServiceProcess = $null
$diagOutput = New-Object System.Collections.Generic.List[string]

function Log-Section {
    param(
        [string]$Title,
        [string]$Content
    )
    $diagOutput.Add("=== $Title ===")
    $diagOutput.Add($Content)
    $diagOutput.Add("")
}

try {
    # 2. Back up hosts file
    Copy-Item -Path $hostsPath -Destination $backupPath -Force
    $originalHosts = Read-HostsFile $hostsPath
    Write-Host "Hosts file backed up to $backupPath."

    # 3. Check if service is running; start if needed
    $serviceRunning = Get-Process -Name "Blocker.Service" -ErrorAction SilentlyContinue
    if (-not $serviceRunning) {
        Write-Host "Blocker.Service not running. Starting background instance..."
        $startedServiceProcess = Start-Process dotnet -ArgumentList "run --project `"$serviceProj`"" -PassThru
        Start-Sleep -Seconds 3
    }

    # 4. Create temporary test profile blocking youtube.com
    $tempProfileName = "TempYoutubeDiag"
    Write-Host "Creating temporary profile '$tempProfileName' blocking youtube.com..."
    & dotnet run --project "$cliProj" -- profile add --name $tempProfileName --site youtube.com 2>&1 | Out-Null

    # 5. Start a 60-second locked session
    Write-Host "Starting 60-second locked session..."
    & dotnet run --project "$cliProj" -- start --profile $tempProfileName --seconds 60 --locked true 2>&1 | Out-Null
    Start-Sleep -Seconds 3

    # 6. Collect: BLOCKER section of hosts file
    $hostsContent = Read-HostsFile $hostsPath
    $beginMarker = "# BLOCKER-BEGIN"
    $endMarker = "# BLOCKER-END"
    $bIdx = $hostsContent.IndexOf($beginMarker)
    $eIdx = $hostsContent.IndexOf($endMarker)
    $blockerSection = "NOT FOUND"
    if ($bIdx -ge 0 -and $eIdx -gt $bIdx) {
        $blockerSection = $hostsContent.Substring($bIdx, ($eIdx + $endMarker.Length) - $bIdx)
    }
    Log-Section "1. BLOCKER SECTION IN HOSTS FILE (INITIAL)" $blockerSection

    # 7. Collect: [System.Net.Dns]::GetHostAddresses results
    $dnsResults = @()
    foreach ($d in @("youtube.com", "www.youtube.com", "m.youtube.com")) {
        try {
            $addrs = [System.Net.Dns]::GetHostAddresses($d) | Select-Object -ExpandProperty IPAddressToString
            $dnsResults += "$d => $($addrs -join ', ')"
        }
        catch {
            $dnsResults += "$d => ERROR: $_"
        }
    }
    Log-Section "2. SYSTEM.NET.DNS GETHOSTADDRESSES" ($dnsResults -join "`r`n")

    # 8. Collect: ping -n 1 youtube.com
    $pingOutput = (& ping -n 1 youtube.com 2>&1) -join "`r`n"
    Log-Section "3. PING -N 1 YOUTUBE.COM" $pingOutput

    # 9. Collect: Get-DnsClientCache matching youtube
    $cacheEntries = @()
    try {
        $cache = Get-DnsClientCache -ErrorAction SilentlyContinue | Where-Object { $_.Entry -like "*youtube*" }
        if ($cache) {
            foreach ($c in $cache) {
                $cacheEntries += "$($c.Entry) (Type: $($c.Type), Status: $($c.Status), Data: $($c.Data))"
            }
        }
        else {
            $cacheEntries += "No cache entries matching '*youtube*'."
        }
    }
    catch {
        $cacheEntries += "Get-DnsClientCache failed: $_"
    }
    Log-Section "4. DNS CLIENT CACHE MATCHING YOUTUBE" ($cacheEntries -join "`r`n")

    # 10. Collect: System proxy settings
    $proxyWinHttp = (& netsh winhttp show proxy 2>&1) -join "`r`n"
    $proxyReg = ""
    try {
        $reg = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -ErrorAction SilentlyContinue
        $proxyReg = "ProxyEnable: $($reg.ProxyEnable)`r`nProxyServer: $($reg.ProxyServer)`r`nAutoConfigURL: $($reg.AutoConfigURL)"
    }
    catch {
        $proxyReg = "Could not read Internet Settings registry: $_"
    }
    Log-Section "5. SYSTEM PROXY SETTINGS" "$proxyWinHttp`r`n`r`n[Internet Settings Registry]`r`n$proxyReg"

    # 11. Collect: Windows Defender detections
    $defenderDetections = "None"
    try {
        $threats = Get-MpThreatDetection -ErrorAction Stop | Select-Object -First 5
        if ($threats) {
            $defenderDetections = ($threats | Format-List -Property InitialDetectionTime, ThreatName, Resources | Out-String).Trim()
        }
        else {
            $defenderDetections = "No recent threat detections found."
        }
    }
    catch {
        $defenderDetections = "Could not query Windows Defender (Get-MpThreatDetection): $_"
    }
    Log-Section "6. WINDOWS DEFENDER DETECTIONS" $defenderDetections

    # 12. Collect: blocker status
    $statusText = (& dotnet run --project "$cliProj" -- status 2>&1) -join "`r`n"
    Log-Section "7. BLOCKER STATUS OUTPUT" $statusText

    # 13. Collect: running browsers
    $browsers = Get-Process -Name chrome, msedge, firefox, brave, opera -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName -Unique
    $runningBrowsers = if ($browsers) { ($browsers -join ", ") } else { "None" }
    Log-Section "8. RUNNING BROWSER PROCESSES" $runningBrowsers

    # 14. Wait 45 seconds to test hosts persistence and detect reverts
    Write-Host "Waiting 45 seconds to detect if hosts file gets reverted by external software..."
    Start-Sleep -Seconds 45

    $hostsAfter45s = Read-HostsFile $hostsPath
    $bIdx2 = $hostsAfter45s.IndexOf($beginMarker)
    $eIdx2 = $hostsAfter45s.IndexOf($endMarker)
    $blockerSectionAfter = "NOT FOUND (REVERTED)"
    if ($bIdx2 -ge 0 -and $eIdx2 -gt $bIdx2) {
        $blockerSectionAfter = $hostsAfter45s.Substring($bIdx2, ($eIdx2 + $endMarker.Length) - $bIdx2)
    }
    Log-Section "9. BLOCKER SECTION IN HOSTS FILE (AFTER 45 SECONDS)" $blockerSectionAfter

    # Write output to result file
    [System.IO.File]::WriteAllText($resultFile, ($diagOutput -join "`r`n"))
    Write-Host "Diagnostic results written to $resultFile." -ForegroundColor Green
}
finally {
    Write-Host "Cleaning up diagnostic state..."
    try {
        # Drop EMERGENCY_UNLOCK
        $eFile1 = "$repoRoot\src\Blocker.Service\bin\Debug\net8.0\EMERGENCY_UNLOCK"
        $eFile2 = "$repoRoot\src\Blocker.Service\EMERGENCY_UNLOCK"
        New-Item -ItemType File -Path $eFile1 -Force -ErrorAction SilentlyContinue | Out-Null
        New-Item -ItemType File -Path $eFile2 -Force -ErrorAction SilentlyContinue | Out-Null

        & dotnet run --project "$cliProj" -- stop 2>&1 | Out-Null
        & dotnet run --project "$cliProj" -- clean-hosts 2>&1 | Out-Null
        & dotnet run --project "$cliProj" -- profile delete --name "TempYoutubeDiag" 2>&1 | Out-Null

        Remove-Item -Path $eFile1 -Force -ErrorAction SilentlyContinue | Out-Null
        Remove-Item -Path $eFile2 -Force -ErrorAction SilentlyContinue | Out-Null

        if (Test-Path $backupPath) {
            Write-HostsFile $hostsPath $originalHosts
            Remove-Item -Path $backupPath -Force -ErrorAction SilentlyContinue | Out-Null
        }

        if ($startedServiceProcess -and -not $startedServiceProcess.HasExited) {
            Stop-Process -Id $startedServiceProcess.Id -Force -ErrorAction SilentlyContinue
        }

        & ipconfig /flushdns | Out-Null
    }
    catch {
        Write-Warning "Cleanup exception: $_"
    }
}
