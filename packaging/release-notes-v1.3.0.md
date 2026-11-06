## Sankalpa v1.3.0 — Meditation Mode, System Lockdown & Obsidian Notes Collection

Sankalpa v1.3.0 introduces a major new dimension to digital focus: **Meditation & System Lockdown Mode**. Free yourself from your computer for a dedicated duration, immerse yourself in offline ambient soundscapes, capture contemplation thoughts with an Obsidian-style Notes Collection and slide-out drawers, and protect your offline deep work with unbreakable tamper-resistant locks.

---

### ✨ What's New in v1.3.0

- 🧘 **Meditation & System Lockdown Dashboard**:
  - Dedicated **"Meditation & Lockdown"** top navigation tab with the lotus icon.
  - Large monospace adaptive countdown timer (`IBM Plex Mono`) rendering `mm:ss` for minute sessions and expanding dynamically to `hh:mm:ss` for hour sessions.
  - Action Core: minimal `[ Start Meditation ]` button with in-place `Edit...` configuration flyout.
  - Quick duration presets (`5m`, `10m`, `20m`, `30m`, `45m`, `60m` or custom stepper).
  - App whitelisting: toggle between *Complete Lockout (0 Apps)* or pick allowed applications (e.g. Spotify, Notion, PDF reader).
  - Lock protection rigor: *Complete Lock (Strict Unbreakable)*, *Typing Challenge*, or *Password*.
  - Recurring scheduled lockouts with a 60-second heads-up warning and single 5-minute snooze safeguard.

- 🛡️ **Fullscreen Zen Shield Kiosk & Multi-Monitor Backdrops**:
  - Primary display runs a borderless, topmost Zen kiosk with a calming 4s inhale / 4s exhale breathing ring animation.
  - All secondary displays are automatically blanketed by pitch-black backdrop windows (`ZenBackdropWindow`).
  - Thread-safe Win32 `LowLevelKeyboardHook` intercepts and suppresses Windows Key (`VK_LWIN`/`VK_RWIN`), `Alt+Tab`, `Alt+Esc`, and `Ctrl+Esc`.
  - Process and Windows Service DACLs locked down during Complete Lock; Task Manager and anti-kill tools are actively terminated.
  - Boot recovery: detects active incomplete sessions upon system startup and restores the lock with accurate remaining time.

- 📝 **Obsidian-Style Notes Collection & Slide-Out Drawer**:
  - Replaced the single plain text box with a card-based Notes Collection.
  - **Top-Anchored Cursor**: Input fields strictly aligned to top-left (`VerticalContentAlignment="Top"`).
  - **Right-Edge `"NOTES"` Drawer Handle**: Vertical rectangular tab pinned flush to the right border of the screen (`N O T E S`). Toggles the 340px notes collection drawer with a fluid cubic animation.
  - **Inline Accordion Preview**: Clicking a card smoothly expands it downward in the same window without hiding other notes.
  - **Interactive Checkboxes**: Markdown `- [ ]` and `- [x]` tasks render as real interactive checkboxes that toggle and auto-save in real-time with strikethrough styling.
  - **In-Place Note Composer**: Write `#` / `##` headings, bullet points, and tasks with quick-insert markdown actions.
  - **3-Way Flexible Markdown Export**: Export as a single Session Bundle, individual vault `.md` files for Obsidian/Logseq, or a Master Chronological Journal.

- 🌧️ **Offline Ambient Sound Engine**:
  - 100% offline audio engine with zero external network streaming.
  - 4 seamless, equal-power loopable 16-bit PCM soundscapes (44.1 kHz): `Rain`, `Library` (warm brown noise), `Forest` (wind & birds), and `Singing Bowls` (1.2 Hz alpha wave entrainment).
  - Moved from center stage to a clean, expandable pill at the **top-left corner** (`🌧 Rain ▾`), keeping the center stage distraction-free.

- 💊 **Floating Zen Mini-HUD Pill & Quick Notes Overlay**:
  - When working inside a whitelisted application, a compact, draggable Mini-HUD pill stays pinned at top-center:
    `🧘 18:45 remaining | 🌧 Rain [⏸] | 📝 Notes | [ Return to Zen Screen ]`
  - Clicking `[ 📝 Notes ]` (or pressing `Alt + N`) summons a floating topmost notes overlay (`ZenQuickNotesOverlay.xaml`) directly over your active app.

- ⌨️ **App-Wide Keyboard Shortcuts**:
  - `Ctrl + B`: Create New Block Profile
  - `Ctrl + S`: Open / Toggle Settings
  - `Ctrl + M`: Switch to Meditation & Lockdown View
  - `Alt + N`: Toggle Quick Notes Overlay
  - Cheat sheet card added to Settings.

---

### 📦 Downloads & Installation

1. **Recommended (Windows Installer)**:
   - Download **`Sankalpa-Setup-1.3.0.exe`** below and run it.
   - Automatically installs the desktop app, background enforcer service, native messaging bridge, and bundles the companion extension into `C:\Program Files\Sankalpa`.

2. **Portable Standalone Executable**:
   - Download **`Sankalpa.exe`** for a standalone portable executable (self-contained win-x64 with .NET 8 runtime).

3. **Browser Companion Extension (v1.3.0)**:
   - Download **`sankalpa-extension-v1.3.0.zip`** for manual extraction and loading unpacked in Chrome (`chrome://extensions`), Edge (`edge://extensions`), Brave, or Firefox (`about:debugging`).

---

### 🔒 Checksums (SHA-256)

- **`Sankalpa-Setup-1.3.0.exe`**: `912E3E86F916F4B555CBA1A3A552D4B90933B9EF2D0D4EDA8B79817E519110AE`
- **`Sankalpa.exe`**: `E2706007EF3AC8DBE1EFD757D93D0B2550C67706A191B81785DF7764891AA722`
- **`sankalpa-extension-v1.3.0.zip`**: `07E64CB84B5976E8E0F2A3EC61473DCEE6EA7AA575F6C64DFF46382BD664FCE4`
