## Sankalpa v1.3.1 — Massive Bug Patch & Rigor Enforcement

Sankalpa v1.3.1 is a comprehensive stability, security, and quality-of-life patch addressing **18 identified issues** across the Meditation Lockdown Engine, Session Rigor & Cryptographic Protection, Notes Vault Persistence, Block Profiles, and Application UI.

---

### 🛡️ 1. Lockdown Engine & Window Watchdog
- **Bug 1 (Blocked Apps Wishlist Conflict)**: Prevented applications contained in active or scheduled Block Profiles from being wishlisted in Meditation mode. The app picker now labels blocked applications with `(Blocked by Block Profile)` and disables their selection.
- **Bug 2 & 3 (Allowed App Minimize & Title Bar Drag Bypass)**: Fixed vulnerability where minimizing or dragging an allowed app's title bar exposed the underlying desktop and taskbar. `ZenShieldWindow` is now pinned at `HWND_BOTTOM` behind allowed apps as an impenetrable Zen backdrop, reinforced by a 500ms active window watchdog and a dedicated **"Return to Zen Focus"** button on the pinned Mini-HUD.
- **Bug 4 (Task View & Gesture Bypass Blocked)**: Neutralized the three-finger swipe-up gesture (`Task View` / `MultitaskingViewFrame`) and `Win + Tab` / `Alt + Tab` via enhanced low-level keyboard hooks and shell window detection, while explicitly preserving `Win + Shift + S` for the Windows Snipping Tool.
- **Bug 15 (Scheduled Blocks Independence)**: Ensured that scheduled website and application block profiles run independently of meditation sessions and remain active within their scheduled time windows even after a meditation timer ends.

---

### 🔒 2. Meditation Rigor & Session Persistence
- **Bug 5 (Typing Challenge Prompt Enforced)**: Fixed lock mode string mismatch so the Typing Challenge verification sentence consistently prompts the user and strictly requires typing the matching phrase before exiting.
- **Bug 6 (Cryptographic Password Protection)**: Enforced password setup and confirmation inside the configuration dialog, storing a PBKDF2/SHA-256 salted hash and requiring exact password verification before allowing session exit.
- **Bug 7 (Daily Schedule Automatic Session Trigger)**: Implemented background session detection in the desktop app to automatically launch `ZenShieldWindow` when a scheduled meditation time arrives.
- **Bug 8 (Soundscape Pill Instant Update)**: Fixed UI data binding so selecting Mute or another soundscape in the edit dialog immediately updates the top-left header pill.
- **Bug 9 (Timer Centering on Session Completion)**: Added automatic reset of the notes drawer state (`IsNotesDrawerOpen = false`) upon session stop, ensuring the countdown timer re-centers without remaining offset to the left.
- **Bug 10 (Timer Duration Persistence)**: Saved user-configured meditation timer durations (including 1-minute sessions), soundscape selections, and lock modes to `UiSettingsStore` so settings persist across app restarts.

---

### 📝 3. Persistent Notes Vault & Markdown Polish
- **Bug 11 (NOTES Drawer Tab Focus Dotted Outline)**: Eliminated the dotted keyboard focus outline around the right-edge `NOTES` drawer handle by setting `FocusVisualStyle="{x:Null}"`.
- **Bug 12 (Persistent Notes Vault Across Restarts)**: Implemented an atomic local JSON note vault at `%LOCALAPPDATA%\Sankalpa\notes\active_notes.json` that debounces writes on creation, editing, and deletion, automatically reloading notes upon app launch.
- **Bug 13 (Caret Drift & Letter Deletion on `#` Tags)**: Fixed text binding re-entrancy in `NotesDrawerControl` by eliminating aggressive property-change triggers, preventing cursor jumping or deleted letters when typing headings or annotations.
- **Bug 14 (Quick Notes Save Bar Clipping & Duplicate Checkmark)**: Compacted the footer status text to `"Ctrl+Enter save • Right-click presets"` and removed the redundant checkmark preceding `"Auto-saved"`.
- **Feature (In-Edit-Mode Screenshot Previews)**: Added an image thumbnail strip above the note editor that parses embedded markdown image URIs and renders live clickable thumbnails while in edit mode.

---

### ⚙️ 4. Block Editor, UI Polish & Shortcuts
- **Bug 16 ("Pick apps..." Selection Appending)**: Modified the app picker in `BlockEditorDialog` to append newly selected applications to existing entries rather than overwriting the entire list.
- **Bug 17 (Default Block Profile Cleanup)**: Removed `notepad.exe` from the default seeded block profile.
- **Bug 18 (Scrollbar Preservation)**: Preserved all native and themed scrollbars per user design preference.
- **Feature (Auto-Dismiss Quick Notes Hint)**: Tracked overlay opens to automatically hide the `"Alt + N to toggle"` hint badge after the shortcut has been used 3 times.
- **Feature (Expanded Global Keyboard Shortcuts)**:
  - `Ctrl + Shift + M`: Start / Stop Meditation Session
  - `Ctrl + Alt + M`: Mute / Unmute Ambient Audio
  - `Ctrl + Shift + U`: Open Meditation Configuration Dialog
  - `Ctrl + N`: New Quick Note
  - `Ctrl + Shift + N`: Toggle Notes Drawer
  - `Esc`: Dismiss open dialogs and overlays

---

### 🧪 Verification & Reliability Metrics
- **Automated Test Suite**: **803 / 803 Tests Passed** (`Sankalpa.Tests` + `Sankalpa.Security.Tests`).
- **Headless UI Render Tests**: **44 Screenshots Verified** with 0 errors.
- **Deployment**: Live binaries deployed to `Sankalpa dev` and `Sankalpa-Release`, companion extension packaged as `sankalpa-extension-v1.3.1.zip`, and native messaging host registered across 12 browsers.
