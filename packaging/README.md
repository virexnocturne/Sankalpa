# Sankalpa Packaging & Distribution System

This directory houses the independent release, installer, and store publishing pipeline for **Sankalpa Focus Blocker**.

None of the scripts or configurations in this directory modify the core application source code. All outputs are staged cleanly into `packaging/dist/`.

---

## Directory Structure

```
packaging/
├── README.md                          # This documentation
├── release-checklist.md               # End-to-end publishing checklist
├── dist/                              # Output folder for generated installers & packages
│   └── sankalpa-extension-v1.0.0.zip  # Store-ready browser companion archive
├── windows-installer/
│   ├── Sankalpa.iss                   # Inno Setup 6 installer script
│   └── build-installer.ps1            # Automated single-file build & installer generator
└── browser-extension/
    ├── package-extension.ps1          # Extension packaging script (filters out dev/test files)
    ├── STORE_LISTINGS.md              # Ready-to-copy store descriptions & permission justifications
    └── PRIVACY_POLICY.md              # 100% offline local privacy policy for store listings
```

---

## Quick Start: Generating Release Artifacts

### 1. Build the Windows Installer (`.exe`)
Requirements: [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
powershell -ExecutionPolicy Bypass -File packaging\windows-installer\build-installer.ps1 -Version "1.0.0"
```
*Output:* `packaging/dist/Sankalpa-Setup-1.0.0.exe`

### 2. Package the Companion Extension (`.zip`)
No external dependencies required (uses built-in PowerShell zip archiving).

```powershell
powershell -ExecutionPolicy Bypass -File packaging\browser-extension\package-extension.ps1
```
*Output:* `packaging/dist/sankalpa-extension-v1.0.0.zip`

---

## Architecture Highlights
- **Single Elevated Setup**: The generated installer requests elevation once and orchestrates the Windows Service creation, background watchdog, and browser native messaging registry keys automatically.
- **Data Safety**: Installing upgrades will never overwrite user blocks or databases stored in `%LocalAppData%\Sankalpa`.
- **Store-Ready**: The extension packager automatically strips test files, keys, and dev artifacts, leaving only the validated Manifest V3 assets required by the Chrome Web Store, Edge Add-ons, and Mozilla AMO.
