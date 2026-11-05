## Sankalpa v1.2.0 — Automatic Schedule Locking, Delayed Unlock & UI Polishing

This release brings significant improvements to schedule enforcement, anti-tamper locking, and desktop UI usability.

### ✨ What's Changed

- **Automatic Schedule Lockdown (`ProfileStateLogic`, `CommandHandler`)**:
  - Profiles with active schedule rules are now strictly locked for their configured time windows (`Schedule 🔒`), even if no special lock mode was manually configured.
  - The toggle switch is disabled while within an active schedule window (`CanToggle = false`), preventing users from simply turning off blocks during designated focus hours.
  - Backend enforcement (`VerifyUnlock`) refuses disabling or editing active scheduled blocks (`Refused: Profile is locked until [endTime]`).
  - Outside configured schedule hours, the block unlocks automatically and can be edited or toggled as normal.

- **Delayed Unlock Protection**:
  - Added new `DelayedUnlock` mode with configurable cooldown timers (15m, 30m, 1h, 2h).
  - Users must wait out an active, attentive countdown timer in front of the screen to disable or modify a locked block.

- **Desktop UI Cleanups (`MainWindow.xaml`, `BlockEditorDialog.xaml`)**:
  - Removed misleading red "Stop" button on profile cards and eliminated the confusing popup dialog.
  - Removed the redundant `Weekdays 8:00 AM-5:00 PM` preset button in the Schedule tab, giving full horizontal width to the description text and completely resolving text clipping.

- **Companion Browser Extension Version 1.2.0**:
  - Bumped `extension/manifest.json` to 1.2.0 and bundled new package `sankalpa-extension-v1.2.0.zip`.

---

### 📦 Downloads & Installation

1. **Recommended (Windows Installer)**:
   - Download **`Sankalpa-Setup-1.2.0.exe`** below and run it.
   - Automatically installs the desktop app, background enforcer service, native messaging bridge, and bundles the companion extension into `C:\Program Files\Sankalpa`.

2. **Portable Standalone Executable**:
   - Download **`Sankalpa.exe`** for a standalone portable executable (self-contained win-x64 with .NET 8 runtime).

3. **Browser Companion Extension**:
   - Download **`sankalpa-extension-v1.2.0.zip`** for manual extraction and loading unpacked in Chrome (`chrome://extensions`), Edge (`edge://extensions`), Brave, or Firefox (`about:debugging`).
   - Alternatively, open `Sankalpa.exe`, click the extension pill in the status bar, and click **"Open folder"** to load the bundled extension directly.

---

### 🔒 Checksums (SHA-256)

- **`Sankalpa-Setup-1.2.0.exe`**: `FB400EBA47A1424E21E14DAA13D50EBABAFD93627452298159567AC4DCE867BF`
- **`Sankalpa.exe`**: `955EE0739B845A99BB2A186F3C9D71FFBDB61D7919F595A034BF5BECC9AD6348`
- **`sankalpa-extension-v1.2.0.zip`**: `E0E27ABF25C9E6D74C63A67180CFD83DFCB68A4340A54620C82D97518862EA3E`
