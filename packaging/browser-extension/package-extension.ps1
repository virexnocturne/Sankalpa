<#
.SYNOPSIS
    Packages the Sankalpa browser companion extension into a clean, store-compliant ZIP.
    Excludes private keys, automated test suites, and internal scratch files.

.PARAMETER Version
    Optional override version string. If omitted, extracted directly from manifest.json.
#>

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$ExtSourceDir = Join-Path $RepoRoot "extension"
$DistDir = Join-Path $RepoRoot "packaging\dist"
$TempStageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sankalpa_ext_stage_" + [System.Guid]::NewGuid().ToString("N"))

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Sankalpa Companion Extension Packaging Pipeline" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

# 1. Validate manifest.json exists
$manifestPath = Join-Path $ExtSourceDir "manifest.json"
if (-not (Test-Path $manifestPath)) {
    Write-Error "manifest.json not found in $ExtSourceDir"
    exit 1
}

# 2. Extract metadata from manifest
$manifestJson = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$extName = $manifestJson.name
$manifestVersion = $manifestJson.version
if (-not $Version) {
    $Version = $manifestVersion
}

Write-Host "Extension Name: $extName" -ForegroundColor Gray
Write-Host "Version:        $Version" -ForegroundColor Gray

# 3. Create staging directory
New-Item -ItemType Directory -Path $TempStageDir -Force | Out-Null

try {
    # 4. Copy production assets (strictly excluding test files, private keys, local txt notes)
    $excludedPatterns = @(
        "*.test.mjs",
        "*.test.js",
        "EXTENSION_ID.txt",
        ".keys*",
        "*.old*",
        "*.tmp",
        "*.bak"
    )

    $filesToCopy = Get-ChildItem -Path $ExtSourceDir -File | Where-Object {
        $fileName = $_.Name
        $isExcluded = $false
        foreach ($pat in $excludedPatterns) {
            if ($fileName -like $pat) {
                $isExcluded = $true
                break
            }
        }
        -not $isExcluded
    }

    foreach ($file in $filesToCopy) {
        Copy-Item -Path $file.FullName -Destination $TempStageDir -Force
    }

    # Verify essential files
    $requiredFiles = @(
        "manifest.json",
        "background.js",
        "matcher.js",
        "popup.html",
        "popup.js",
        "popup.css",
        "blocked.html",
        "blocked.js",
        "blocked.css",
        "icon16.png",
        "icon32.png",
        "icon48.png",
        "icon128.png"
    )

    $missingFiles = @()
    foreach ($rf in $requiredFiles) {
        if (-not (Test-Path (Join-Path $TempStageDir $rf))) {
            $missingFiles += $rf
        }
    }

    if ($missingFiles.Count -gt 0) {
        Write-Error "Packaging aborted: Required extension files missing from bundle: $($missingFiles -join ', ')"
        exit 1
    }

    # Ensure packaging output directory exists
    if (-not (Test-Path $DistDir)) {
        New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
    }

    # 5. Compress into store-ready ZIP archive
    $zipPath = Join-Path $DistDir "sankalpa-extension-v$Version.zip"
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($TempStageDir, $zipPath)

    $zipItem = Get-Item $zipPath
    $sizeKb = [math]::Round($zipItem.Length / 1KB, 2)
    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash

    Write-Host ""
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  Extension ZIP created successfully!" -ForegroundColor Green
    Write-Host "========================================================" -ForegroundColor Green
    Write-Host "  Archive: $zipPath"
    Write-Host "  Size:    $sizeKb KB"
    Write-Host "  SHA256:  $hash"
    Write-Host ""
    Write-Host "Ready for upload to:" -ForegroundColor Cyan
    Write-Host "  1. Chrome Web Store (https://chrome.google.com/webstore/devconsole)"
    Write-Host "  2. Microsoft Partner Center / Edge Add-ons (https://partner.microsoft.com/dashboard/microsoftedge)"
    Write-Host "  3. Mozilla Add-ons Developer Hub (https://addons.mozilla.org/developers/)"
    Write-Host ""

} finally {
    # Clean up temporary stage directory
    if (Test-Path $TempStageDir) {
        Remove-Item -Path $TempStageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
