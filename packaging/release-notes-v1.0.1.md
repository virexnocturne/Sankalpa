## Sankalpa v1.0.1 — Maintenance, Autonomous Enforcement & Reliable Alerts Release

This release delivers critical fixes to the Windows desktop interface, completely reworks background browser termination so it enforces autonomously without needing active website block rules, makes desktop toast notifications fire reliably without needing the UI running, and bundles the companion browser extension directly into the installer.

### ✨ What's Changed

- **UI Button Template Fix**:
  - Fixed a WPF data-binding bug where `+ New block` and other action buttons rendered `System.Windows.Controls.TextBlock` instead of their text content.
- **Autonomous Browser Termination**:
  - Removed restrictive guards that previously disabled grace period tracking if no website blocking rules were active.
  - Any unauthorized browser or browser without the active Sankalpa extension now consistently receives countdown warnings and terminates after 60 seconds.
- **Independent Windows Toast Notification System**:
  - Background notifications now dispatch reliably from `Blocker.Service` (Session 0) into active user sessions even when `Sankalpa.exe` is completely closed.
  - Added WinRT COM RPC completion delay (`Thread.Sleep(1200)`) in `Blocker.NativeHost` to ensure notifications are never dropped before Windows Action Center receives them.
  - Unthrottled countdown milestones (`60s`, `30s`, `10s`) so warning alerts are always delivered.
- **Companion Extension Bundling**:
  - The companion browser extension is now packaged directly inside `Sankalpa-Setup-1.0.1.exe` and copied alongside `Sankalpa.exe`, enabling 1-click "Open folder" access directly from the desktop application dialog.
- **General UI & Quality Fixes**:
  - DNS protection custom duration bindings restored.
  - Pressing Escape during inline profile rename now correctly cancels instead of committing.
  - Password and typing challenge enforcement added to dashboard delete actions to prevent bypassing locked profiles.
  - Atomic rename dispatch to eliminate duplicate profile creation during edits.
  - Clickable extension status indicator in the status bar.
  - Empty state card for empty dashboards.

---

### 📦 Downloads & Installation

1. **Recommended (Windows Installer)**:
   - Download **`Sankalpa-Setup-1.0.1.exe`** below and run it.
   - Requires Administrator elevation once to set up the background enforcer service.
   - Automatically installs the desktop app, service, native messaging bridge, and extension folder into `C:\Program Files\Sankalpa`.

2. **Portable Standalone Executable**:
   - Download **`Sankalpa.exe`** for a standalone portable executable (requires .NET 8 runtime included / self-contained).

3. **Browser Companion Extension**:
   - Download **`sankalpa-extension-v1.0.1.zip`** for manual extraction and loading unpacked in Chrome (`chrome://extensions`), Edge (`edge://extensions`), or Firefox (`about:debugging`).
   - Alternatively, open `Sankalpa.exe`, click the extension pill in the status bar, and click **"Open folder"** to load the bundled extension directly.

---

### 🔒 Checksums (SHA-256)

- **`Sankalpa-Setup-1.0.1.exe`**: `C115268CD55785E7024DA2A063586759FB85CAD87A3A3F569F2A400BADB2C3A0`
- **`Sankalpa.exe`**: `CA20CA078DD29B1EB5CDF152B2C9906CFCC58EC76C88ADB36C51F8DBF4F453D2`
- **`sankalpa-extension-v1.0.1.zip`**: `8DC3D40CE32BAA0FB84995AFBD749C311E5958130B443FFD16BC2688798EC531`
