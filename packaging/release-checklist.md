# Sankalpa v1.0.0 — Release & Publishing Checklist

This checklist guides you through the complete lifecycle of preparing, testing, and distributing a production release of Sankalpa.

---

## Phase 1: Pre-Release Quality Verification

- [ ] **Verify Core Solution Build**:
  ```powershell
  dotnet build Blocker.sln -c Release
  ```
  *(Verify all 5 projects build with 0 errors and 0 warnings).*
- [ ] **Run UI Lint Audit**:
  Confirm zero hardcoded hex colors, zero sub-12px fonts, and verified Lucide icons across all views.
- [ ] **Check Process Cleanliness**:
  Ensure no rogue debug background services are running before building:
  ```powershell
  taskkill /F /IM Sankalpa.exe /IM Blocker.Service.exe /IM Blocker.NativeHost.exe
  ```

---

## Phase 2: Compile the Windows Installer

- [ ] **Install Inno Setup 6** (if not already installed):
  ```powershell
  winget install JRSoftware.InnoSetup --source winget
  ```
- [ ] **Build the Production Setup Executable**:
  ```powershell
  powershell -ExecutionPolicy Bypass -File packaging\windows-installer\build-installer.ps1 -Version "1.0.0"
  ```
- [ ] **Verify Artifact Generation**:
  Check that `packaging\dist\Sankalpa-Setup-1.0.0.exe` is generated. Note down its SHA-256 hash.

---

## Phase 3: Local Installation Verification

- [ ] **Test Clean Installation**:
  Run `packaging\dist\Sankalpa-Setup-1.0.0.exe` on your machine or a clean Windows VM.
  - Verify UAC elevation prompt appears.
  - Verify default destination is `C:\Program Files\Sankalpa`.
  - Check that Start Menu and Desktop shortcuts are created.
- [ ] **Verify Windows Service Registration**:
  ```powershell
  sc query SankalpaService
  ```
  *(State must show `RUNNING`, Start Type `AUTO_START`).*
- [ ] **Verify Native Messaging Bridge**:
  Check registry key:
  ```powershell
  Get-ItemProperty "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.blocker.host"
  ```
- [ ] **Verify Notification Delivery**:
  Launch `Sankalpa.exe`, start a test block session, close the UI, and verify toast notifications appear from the background service.
- [ ] **Test Uninstallation**:
  Go to Windows Settings → Apps → Installed apps → **Sankalpa** → Uninstall.
  - Verify `SankalpaService` is stopped and deleted.
  - Verify prompt to keep or delete `%LocalAppData%\Sankalpa` data functions as expected.

---

## Phase 4: Companion Extension Store Submissions

- [ ] **Package the Extension ZIP**:
  ```powershell
  powershell -ExecutionPolicy Bypass -File packaging\browser-extension\package-extension.ps1
  ```
  *(Generates `packaging\dist\sankalpa-extension-v1.0.0.zip`).*
- [ ] **Host Privacy Policy**:
  Copy `packaging\browser-extension\PRIVACY_POLICY.md` to your public GitHub repo or website.
- [ ] **Submit to Chrome Web Store**:
  - Upload `sankalpa-extension-v1.0.0.zip` at [Chrome Web Store Console](https://chrome.google.com/webstore/devconsole).
  - Use copy from `packaging\browser-extension\STORE_LISTINGS.md`.
- [ ] **Submit to Microsoft Edge Add-ons**:
  - Submit package or import from Chrome Web Store via [Microsoft Partner Center](https://partner.microsoft.com/dashboard/microsoftedge).
- [ ] **Submit to Mozilla Firefox Add-ons (AMO)**:
  - Submit package at [Mozilla Developer Hub](https://addons.mozilla.org/developers/).

---

## Phase 5: GitHub Release & Public Distribution

- [ ] **Create GitHub Release**:
  - Go to your repository on GitHub → **Releases** → **Draft a new release**.
  - Tag: `v1.0.0`.
  - Title: `Sankalpa v1.0.0 — Distraction-Free Focus & Productivity Engine`.
  - Attach binaries from `packaging\dist\`:
    - `Sankalpa-Setup-1.0.0.exe`
    - `sankalpa-extension-v1.0.0.zip`
  - Paste release notes and SHA-256 checksums.
- [ ] **Submit to Windows Package Manager (winget)**:
  Use `wingetcreate` to publish to the official Windows Community repository:
  ```powershell
  winget install Microsoft.WingetCreate
  wingetcreate new https://github.com/virex/Sankalpa/releases/download/v1.0.0/Sankalpa-Setup-1.0.0.exe
  ```
  Follow prompts and submit the automated pull request to `microsoft/winget-pkgs`.
