@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges to delete SankalpaService...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo Stopping and deleting SankalpaService...
sc stop SankalpaService >nul 2>&1
sc delete SankalpaService
echo.
echo ==============================================
echo SankalpaService has been completely removed!
echo ==============================================
pause
