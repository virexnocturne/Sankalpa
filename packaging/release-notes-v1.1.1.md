## Sankalpa v1.1.1 — Strict Uninstall Protection & Companion Extension Synchronization

This release introduces **Strict Uninstall Protection** to ensure that active focus blocks cannot be bypassed or removed via uninstallation, and synchronizes the companion browser extension version.

### ✨ What's Changed

- **Strict Uninstall Protection (`UninstallGuard`)**:
  - Implemented `UninstallGuard` in `Blocker.Core` to check live service status via Named Pipe, with defense-in-depth offline fallbacks inspecting SQLite database tables (`BlockSessions`, `Profiles`, `DnsSettings`) and system `hosts` redirects.
  - Added `sankalpa-cli.exe check-uninstall` command which returns exit code `0` (uninstallation allowed) or exit code `1` (access denied due to active block).
  - Integrated `InitializeUninstall()` into the Windows installer (`Sankalpa-Setup-1.1.1.exe`). If an active block session, schedule, or locked profile is active, Windows Settings / Add-Remove Programs (`unins000.exe`) strictly refuses to uninstall and aborts immediately without touching files, services, or registry keys.
  - Enforced uninstall protection in `clean-uninstall.cmd` for portable and developer installations.
- **Companion Extension Version 1.1.1**:
  - Synchronized `extension/manifest.json` to version 1.1.1.
  - Generated `sankalpa-extension-v1.1.1.zip` for Chrome, Edge, Brave, and Firefox.
- **Flaky Test Resilience**:
  - Improved retry timing in hosts file manager integration tests to prevent concurrency timeouts under heavy system load.

---

### 📦 Downloads & Installation

1. **Recommended (Windows Installer)**:
   - Download **`Sankalpa-Setup-1.1.1.exe`** below and run it.
   - Automatically installs the desktop app, background enforcer service, native messaging bridge, and bundles the extension folder into `C:\Program Files\Sankalpa`.

2. **Portable Standalone Executable**:
   - Download **`Sankalpa.exe`** for a standalone portable executable (requires .NET 8 runtime included / self-contained).

3. **Browser Companion Extension**:
   - Download **`sankalpa-extension-v1.1.1.zip`** for manual extraction and loading unpacked in Chrome (`chrome://extensions`), Edge (`edge://extensions`), or Firefox (`about:debugging`).
   - Alternatively, open `Sankalpa.exe`, click the extension pill in the status bar, and click **"Open folder"** to load the bundled extension directly.

---

### 🔒 Checksums (SHA-256)

- **`Sankalpa-Setup-1.1.1.exe`**: `6057B8162971CB3096CED839BA29092DEE854B4DA4AC4A33A39A6E968E23D8B2`
- **`Sankalpa.exe`**: `4FC113C8250253E286EEFF773C27F8ABD69D156D3408C54C2009CABD1ED0CFF9`
- **`sankalpa-extension-v1.1.1.zip`**: `7F6C5CA78CB466A04F8231B3550B1862AABFE4459CE6CA43859D2A8FDF30A6C2`
