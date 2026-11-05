## Sankalpa v1.2.1 — Delayed Unlock Presets & Challenge Toggle Workflow

This release resolves two critical UX issues in block locking and unlocking:

### ✨ What's Changed

- **Delayed Unlock Presets Restored in Block Editor (`BlockEditorDialog.xaml.cs`)**:
  - Selecting "Delayed Unlock" in the Lock Protection section now immediately displays the preset wait duration buttons (15m, 30m, 45m, 1h).
  - Selected preset styling updates seamlessly upon selection.

- **Interactive Toggle-to-Unlock for Challenge-Protected Blocks (`ProfileViewModel.cs`, `MainViewModel.cs`, `MainWindow.xaml.cs`)**:
  - Clearly differentiates **Hard Locks** (Complete Lock focus sessions and active scheduled time windows) from **Challenge Locks** (Delayed Unlock cooldown timer, Emergency Password, and Typing Challenge).
  - Un-greys the toggle switch for challenge-protected blocks (`CanToggle = true`).
  - Clicking the toggle switch to turn off a challenge-protected block launches the appropriate challenge modal (`DelayedUnlockPromptDialog` -> `CoolDownTimerWindow`, `PasswordUnlockDialog`, or `TypingChallengeDialog`).
  - If the user cancels, closes, or fails the challenge, the block stays strictly ON. If completed, the block turns OFF with validated credentials.
  - While active and locked, blocks remain in **additive-only mode** in the editor to prevent unauthorized modifications or exception backdoors.

---

### 📦 Downloads & Installation

1. **Recommended (Windows Installer)**:
   - Download **`Sankalpa-Setup-1.2.1.exe`** below and run it.
   - Automatically installs the desktop app, background enforcer service, native messaging bridge, and bundles the companion extension into `C:\Program Files\Sankalpa`.

2. **Portable Standalone Executable**:
   - Download **`Sankalpa.exe`** for a standalone portable executable (self-contained win-x64 with .NET 8 runtime).

3. **Browser Companion Extension**:
   - Download **`sankalpa-extension-v1.2.0.zip`** for manual extraction and loading unpacked in Chrome (`chrome://extensions`), Edge (`edge://extensions`), Brave, or Firefox (`about:debugging`).
   - Alternatively, open `Sankalpa.exe`, click the extension pill in the status bar, and click **"Open folder"** to load the bundled extension directly.

---

### 🔒 Checksums (SHA-256)

- **`Sankalpa-Setup-1.2.1.exe`**: `400F5B780B521CB9B91DFDF573A547A5D6304B0DDF9300E684B020360E11AD9C`
- **`Sankalpa.exe`**: `C7FB702B56799703E52E9A7B17690D4FF7F138F1E7AC16111E4A94CAF64572C0`
- **`sankalpa-extension-v1.2.0.zip`**: `E0E27ABF25C9E6D74C63A67180CFD83DFCB68A4340A54620C82D97518862EA3E`
