@echo off
setlocal EnableDelayedExpansion

echo ========================================================
echo   Sankalpa Clean Uninstaller & Environment Reset
echo ========================================================
echo.

:: 0. Check Uninstall Protection (strictly forbids uninstallation if any block is active)
echo [*] Checking uninstall protection...
set "CLI_EXE=%~dp0sankalpa-cli.exe"
if not exist "!CLI_EXE!" set "CLI_EXE=%~dp0dist\Sankalpa-Release\sankalpa-cli.exe"
if not exist "!CLI_EXE!" set "CLI_EXE=C:\Program Files\Sankalpa\sankalpa-cli.exe"

if exist "!CLI_EXE!" (
    "!CLI_EXE!" check-uninstall
    if !errorlevel! neq 0 (
        echo.
        echo ========================================================
        echo   [ACCESS DENIED] UNINSTALL PROTECTION IS ACTIVE
        echo ========================================================
        echo   An active block session or schedule is currently running!
        echo   Sankalpa cannot be uninstalled while a block is active.
        echo   Strict uninstall protection is enforced.
        echo.
        echo   Please wait until all active blocks have finished.
        echo ========================================================
        echo.
        pause
        exit /b 1
    )
)

:: 1. Check for administrative privileges
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [!] Requesting administrative privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo [1/6] Terminating running Sankalpa processes...
taskkill /F /IM Sankalpa.exe >nul 2>&1
taskkill /F /IM Blocker.Service.exe >nul 2>&1
taskkill /F /IM Blocker.NativeHost.exe >nul 2>&1

echo [2/6] Stopping and deleting Windows Service (SankalpaService)...
sc query SankalpaService >nul 2>&1
if %errorlevel% equ 0 (
    sc stop SankalpaService >nul 2>&1
    sc delete SankalpaService >nul 2>&1
    echo     Service stopped and deleted.
) else (
    echo     Service was not installed.
)

echo [3/6] Unregistering Native Messaging host from all browsers...
set "HOST_EXE=%~dp0Blocker.NativeHost.exe"
if not exist "!HOST_EXE!" set "HOST_EXE=%~dp0dist\Sankalpa-Release\Blocker.NativeHost.exe"
if not exist "!HOST_EXE!" set "HOST_EXE=%LOCALAPPDATA%\Blocker\Blocker.NativeHost.exe"

if exist "!HOST_EXE!" (
    "!HOST_EXE!" --unregister >nul 2>&1
    echo     Browser native messaging hosts unregistered.
) else (
    echo     NativeHost executable not found; manually removing registry keys...
    reg delete "HKCU\Software\Google\Chrome\NativeMessagingHosts\com.sankalpa.native_host" /f >nul 2>&1
    reg delete "HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.sankalpa.native_host" /f >nul 2>&1
    reg delete "HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.sankalpa.native_host" /f >nul 2>&1
    reg delete "HKCU\Software\Mozilla\NativeMessagingHosts\com.sankalpa.native_host" /f >nul 2>&1
)

echo [4/6] Cleaning up Windows Shell and Toast registrations...
reg delete "HKCU\Software\Classes\AppUserModelId\Sankalpa" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "Sankalpa" /f >nul 2>&1

echo [5/6] Restoring Windows Hosts file if modified...
set "HOSTS_FILE=%SystemRoot%\System32\drivers\etc\hosts"
powershell -Command "if (Test-Path '%HOSTS_FILE%') { $lines = Get-Content '%HOSTS_FILE%'; $out = @(); $skipping = $false; foreach ($l in $lines) { if ($l -match '# === SANKALPA BLOCKER') { $skipping = -not $skipping; continue } if (-not $skipping) { $out += $l } } $out | Set-Content '%HOSTS_FILE%' -Force }" >nul 2>&1

echo [6/6] User Data & Profile Settings...
set /p REMOVE_DATA="Do you want to delete all saved profiles, databases, and logs? (Y/N): "
if /i "!REMOVE_DATA!"=="Y" (
    echo Removing data directories...
    if exist "%LOCALAPPDATA%\Sankalpa" rd /s /q "%LOCALAPPDATA%\Sankalpa"
    if exist "%LOCALAPPDATA%\Blocker" rd /s /q "%LOCALAPPDATA%\Blocker"
    if exist "%PROGRAMDATA%\Sankalpa" rd /s /q "%PROGRAMDATA%\Sankalpa"
    echo Data wiped completely.
) else (
    echo Profiles and block history preserved.
)

echo.
echo ========================================================
echo   Sankalpa has been cleanly uninstalled from this PC!
echo ========================================================
echo.
pause
