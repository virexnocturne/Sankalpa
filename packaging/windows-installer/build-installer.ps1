<#
.SYNOPSIS
    Builds the complete production release of Sankalpa and compiles the Inno Setup installer.

.PARAMETER SkipDotnetBuild
    If specified, skips running 'dotnet publish' and uses the pre-existing binaries in dist/Sankalpa-Release.

.PARAMETER Version
    The release version string (defaults to '1.0.0').
#>

param(
    [switch]$SkipDotnetBuild = $false,
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$StagingDir = Join-Path $RepoRoot "dist\Sankalpa-Release"
$DistDir = Join-Path $RepoRoot "packaging\dist"
$IssPath = Join-Path $ScriptDir "Sankalpa.iss"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Sankalpa Windows Installer Build Pipeline (v$Version)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

# 1. Locate Inno Setup Compiler (iscc.exe)
$isccCandidates = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
    "${env:ProgramFiles}\Inno Setup 6\iscc.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\iscc.exe",
    "${env:ProgramFiles}\Inno Setup 7\iscc.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\iscc.exe"
)

$isccPath = $null
foreach ($cand in $isccCandidates) {
    if ($cand -and (Test-Path $cand)) {
        $isccPath = $cand
        break
    }
}

if (-not $isccPath) {
    Write-Warning "Inno Setup Compiler (iscc.exe) was not found on this system."
    Write-Host "To install Inno Setup 6, run the following command in PowerShell:" -ForegroundColor Yellow
    Write-Host "  winget install JRSoftware.InnoSetup --source winget --accept-source-agreements --accept-package-agreements" -ForegroundColor Green
    Write-Host ""
    Write-Error "Inno Setup is required to compile the installer. Please install it and rerun this script."
    exit 1
}

Write-Host "Found Inno Setup Compiler: $isccPath" -ForegroundColor Green

# 2. Build .NET Release Binaries (if not skipped)
if (-not $SkipDotnetBuild) {
    Write-Host ""
    Write-Host "Building self-contained win-x64 Release binaries..." -ForegroundColor Cyan
    
    # Ensure staging directory exists
    if (-not (Test-Path $StagingDir)) {
        New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
    }

    $projects = @(
        @{ Name = "Blocker.App"; OutExe = "Sankalpa.exe" },
        @{ Name = "Blocker.Service"; OutExe = "Blocker.Service.exe" },
        @{ Name = "Blocker.NativeHost"; OutExe = "Blocker.NativeHost.exe" },
        @{ Name = "Blocker.Cli"; OutExe = "sankalpa-cli.exe" }
    )

    foreach ($proj in $projects) {
        $projPath = Join-Path $RepoRoot "src\$($proj.Name)\$($proj.Name).csproj"
        Write-Host "Publishing $($proj.Name)..." -ForegroundColor Gray
        & dotnet publish $projPath -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o $StagingDir
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Build failed for project $($proj.Name)."
            exit 1
        }
    }

    # Copy assets and licenses
    $assetsSrc = Join-Path $RepoRoot "src\Blocker.App\Assets"
    $assetsDest = Join-Path $StagingDir "Assets"
    if (Test-Path $assetsSrc) {
        Copy-Item -Path $assetsSrc -Destination $StagingDir -Recurse -Force
    }

    $licensesSrc = Join-Path $RepoRoot "licenses"
    $licensesDest = Join-Path $StagingDir "licenses"
    if (Test-Path $licensesSrc) {
        Copy-Item -Path $licensesSrc -Destination $StagingDir -Recurse -Force
    }

    Write-Host "Release binaries compiled and staged into $StagingDir" -ForegroundColor Green
} else {
    Write-Host "Skipping dotnet publish as requested. Using existing binaries in $StagingDir." -ForegroundColor Yellow
}

# 3. Create packaging output directory
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

# 4. Compile Inno Setup Installer
Write-Host ""
Write-Host "Compiling Inno Setup package from $IssPath..." -ForegroundColor Cyan
& "$isccPath" "/DMyAppVersion=$Version" "$IssPath"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed."
    exit 1
}

# 5. Locate and report generated installer
$outputInstaller = Join-Path $DistDir "Sankalpa-Setup-$Version.exe"
if (Test-Path $outputInstaller) {
    $item = Get-Item $outputInstaller
    $sizeMb = [math]::Round($item.Length / 1MB, 2)
    $hash = (Get-FileHash -Path $outputInstaller -Algorithm SHA256).Hash

    Write-Host ""
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  Installer generated successfully!" -ForegroundColor Green
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  Path:   $outputInstaller"
    Write-Host "  Size:   $sizeMb MB"
    Write-Host "  SHA256: $hash"
    Write-Host ""
    Write-Host "You can now distribute this single setup file to users!" -ForegroundColor Cyan
} else {
    Write-Warning "Compilation completed, but $outputInstaller could not be located in $DistDir."
}
