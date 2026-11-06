# Sankalpa — Future Patches & Bug Fixes

This document tracks planned bug fixes, regression repairs, and behavioral patches for **Sankalpa Blocker**, clearly separated between active planned patches and verified resolutions.

---

## 📋 Active Backlog & Planned Patches

### 5. Authentic Acoustic Ambient Soundscapes in Meditation Mode (Replacing Sine Wave Synthetics)

- **Status**: `Planned`
- **Priority**: `Medium–High`
- **Category**: Audio Engine & Meditation Immersion
- **Affected Components**:
  - `src/Blocker.App/Assets/Audio/` (`rain.wav`, `library.wav`, `forest.wav`, `bowls.wav`)
  - `src/Blocker.App/AmbientSoundEngine.cs`
  - `packaging/Sankalpa.iss` & build scripts (`dist/Sankalpa-Release/Assets/Audio`)

- **Description / Symptoms**:
  - In v1.3.0, the offline ambient sound engine was introduced with four default soundscapes: Rain, Library, Forest, and Singing Bowls.
  - Because offline standalone builds required immediate placeholders, these soundscapes were generated using procedural mathematical sine wave oscillators and filtered white noise ("sin ones").
  - Consequently, the soundscapes sound artificial, buzzy, and robotic rather than soothing or calming. The Tibetan singing bowl sounds like a raw sine frequency tone, rain sounds like harsh static, and forest sounds lack authentic natural acoustics.
  - For a meditation and focus environment, authentic acoustic immersion is essential; synthetic sine loops cause auditory fatigue and detract from mindfulness.

- **Root Cause Analysis**:
  1. The bundled `.wav` files in `src/Blocker.App/Assets/Audio/` are identical 2,116,844-byte (12-second 44.1kHz mono) algorithmic sine-wave/noise test tones generated during initial scaffolding.
  2. The engine in `AmbientSoundEngine.cs` loops whichever audio file is on disk, so playback fidelity is strictly bottlenecked by the synthetic nature of these placeholder WAV files.

- **Proposed Fix**:
  1. **Acquire & Bundle Authentic High-Definition Audio**: Replace the procedural sine-wave audio files with real-world acoustic field recordings:
     - `rain.wav`: Real gentle rain falling on leaves and a window with soft natural thunder rumbles.
     - `library.wav`: Real calm library ambiance with distant paper turns, subtle wooden chair rustles, and soft room tone.
     - `forest.wav`: Real temperate forest with distant wind through pine trees, natural birdsong, and rustling leaves.
     - `bowls.wav`: Real hand-hammered Tibetan bronze singing bowl strikes with authentic harmonic overtones and long decaying resonance.
  2. **Seamless Zero-Crossing Loop Points**: Edit and master each recording to ensure smooth, seamless looping without audible clicks, pops, or volume dips at the boundary.
  3. **Optimized Compression**: Convert audio to high-bitrate stereo MP3/Ogg or high-quality compressed WAV/WMA to balance pristine acoustic fidelity with small installer packaging footprint (< 5–10 MB total).
  4. **Engine & Verification Support**: Ensure `AmbientSoundEngine.cs` smoothly loads and fades the upgraded assets, with automated playback and header verification tests in `NotesAndSoundscapeUiTests.cs`.

---

## ✅ Resolved & Verified Patches (Shipped)

### 1. Scheduled Block in Extension Does Not Show "Locked" Badge During Active Schedule

- **Status**: `Resolved & Verified (Shipped in v1.2.0)`
- **Priority**: `High`
- **Category**: Extension UI & Protocol Synchronization / Status Accuracy
- **Affected Components**:
  - `src/Blocker.Core/CommandHandler.cs` (`HandleGetActiveWebRules`)
  - `extension/popup.js` (`renderBlockRow` / `renderActiveList`)
  - `src/Blocker.Core/Protocol.cs` (`ActiveProfileDto`, `ProfileSummaryDto`)

- **Description / Symptoms**:
  - When a block profile is triggered by a schedule and actively enforcing rules, the browser companion extension popup correctly displays `ACTIVE` and the remaining scheduled countdown timer (e.g., `10:20 remaining`).
  - However, the `🔒 Locked` badge was completely missing from the block row in the extension popup.
- **Root Cause & Fix Applied**:
  - Updated `HandleGetActiveWebRules` in `CommandHandler.cs` to leverage `ProfileStateLogic.IsProfileLockedNow(profile, session, localNow)`, accurately conveying `Locked = true` to the browser companion extension so it dynamically renders the `🔒 Locked` badge.

---

### 2. Service DACL Lockdown Can Fail Silently Without Being Retried

- **Status**: `Resolved & Verified (Shipped in v1.3.1)`
- **Priority**: `Medium–High`
- **Category**: Security Hardening & Background Service Tamper Protection
- **Affected Components**:
  - `src/Blocker.Service/Worker.cs` (`CheckActiveSessionsAndSync`)
  - `src/Blocker.Core/SecurityHardener.cs` (`IsLockedDown`, `IsServiceLockedDown`)

- **Description / Symptoms**:
  - If process lockdown succeeded but Windows service DACL lockdown failed due to transient SCM lock contention, the service logged success and skipped retrying service lockdown on subsequent polling cycles.
- **Root Cause & Fix Applied**:
  - Decoupled process and service lockdown state checks in `Worker.cs` so any transient failure in `sc.exe sdset` is naturally retried on subsequent cycles until `IsServiceLockedDown` is successfully established.

---

### 3. Scheduled Block Does Not Automatically Turn On at Scheduled Time if Block Was Toggled "Off"

- **Status**: `Resolved & Verified (Shipped in v1.3.1)`
- **Priority**: `High`
- **Category**: Scheduling Engine & Profile Lifecycle Automation
- **Affected Components**:
  - `src/Blocker.Core/ProfileStateLogic.cs` (`IsProfileActive`, `IsProfileActiveWithSessions`)
  - `src/Blocker.Service/Worker.cs`

- **Description / Symptoms**:
  - If a user toggled a block profile "OFF" prior to a scheduled window, the scheduled block failed to turn on when the clock entered the scheduled period because `!profile.Enabled` was evaluated before `ScheduleLogic.IsActive`.
- **Root Cause & Fix Applied**:
  - Adjusted schedule precedence in `ProfileStateLogic.cs` so active schedule time windows take priority over manual off-toggles, ensuring scheduled focus blocks automatically engage as configured.

---

### 4. Collapsible & Expandable Keyboard Shortcuts Panel (Space-Saving UI)

- **Status**: `Resolved & Verified (Shipped in v1.3.2)`
- **Priority**: `Medium`
- **Category**: UI/UX & Settings Layout Ergonomics / Space Optimization
- **Affected Components**:
  - `src/Blocker.App/MainWindow.xaml` (Settings Tab, CATEGORY 4: KEYBOARD SHORTCUTS)
  - `src/Blocker.App/AppearanceDialog.xaml` (Shortcuts Section)
  - `src/Blocker.App/MainViewModel.cs` (`IsShortcutsExpanded`, `ToggleShortcutsExpandedCommand`)
  - `src/Blocker.Core/UiSettingsStore.cs` (`IsKeyboardShortcutsExpanded`)
- **Fix Applied**:
  - Wrapped the 10-shortcut reference card in `MainWindow.xaml` and `AppearanceDialog.xaml` inside a collapsible accordion container with toggle button `[ Expand shortcuts (10) ▾ / Collapse shortcuts ▴ ]`.
  - Added user preference persistence via `UiSettingsStore.IsKeyboardShortcutsExpanded` (defaults to collapsed), compressing the 550px list down to a compact 50px card until expanded by the user.

---

### 6. `Win + Shift + S` Screenshot / Snipping Tool Hotkey in Kiosk & Meditation Mode

- **Status**: `Resolved & Verified (Shipped in v1.3.2)`
- **Priority**: `High`
- **Category**: Input Interception, Kiosk Lockdown & System Hotkey Passthrough
- **Affected Components**:
  - `src/Blocker.Core/LowLevelKeyboardHook.cs` (`HookCallback`, `OnSnippingRequested`)
  - `src/Blocker.App/ZenShieldWindow.xaml.cs` (`YieldTopmostForSnipping`, `RestoreTopmostAfterSnipping`, `WatchdogTimer_Tick`)
  - `src/Blocker.Service/Worker.cs` (`ProtectedProcesses`, `Worker.IsAppAllowed`)
- **Fix Applied**:
  - Wired `OnSnippingRequested` in `LowLevelKeyboardHook.cs` with dual invocation fallbacks (`ms-screenclip:` and `snippingtool.exe /clip`).
  - Added immediate topmost yielding (`YieldTopmostForSnipping` sets `Topmost = false` and `HWND_NOTOPMOST`) across primary and secondary displays so `ScreenClippingHost.exe` renders on top of the focus shield and receives mouse dragging input cleanly.
  - Added watchdog detection that automatically restores `Topmost = true` once snipping closes or completes.

---

### 7. Pre-Session Confirmation Warning for Kiosk & Meditation Mode with "Do Not Show Again"

- **Status**: `Resolved & Verified (Shipped in v1.3.2)`
- **Priority**: `High`
- **Category**: UX Safety, Accidental Trigger Prevention & Session Lifecycle
- **Affected Components**:
  - `src/Blocker.App/MeditationViewModel.cs` (`StartMeditation`)
  - `src/Blocker.App/MeditationStartWarningDialog.xaml` (New confirmation dialog)
  - `src/Blocker.Core/UiSettingsStore.cs` (`SuppressMeditationStartWarning`)
  - `src/Blocker.App/AppearanceDialog.xaml` (Settings toggle)
- **Fix Applied**:
  - Created `MeditationStartWarningDialog` displaying duration, lock mode, and allowed apps summary with prominent warning text and `[ ] Do not show this warning again` checkbox.
  - Added pre-flight check in `MeditationViewModel.StartMeditation()` checking `UiSettingsStore.Instance.SuppressMeditationStartWarning`.
  - Added "Show warning before starting Meditation & Lockdown" toggle in Settings to restore the prompt at any time.

---

### 8. Notes Drawer Shortcut Double-Execution & Countdown Clock Layout Snapping

- **Status**: `Resolved & Verified (Shipped in v1.3.2)`
- **Priority**: `High`
- **Category**: Input Handling, UI Animation & Focus Stage Layout Ergonomics
- **Affected Components**:
  - `src/Blocker.App/MainWindow.xaml` (`<KeyBinding>` deduplication & `TranslateTransform` animation)
  - `src/Blocker.App/MainWindow.xaml.cs` (`PreviewKeyDown` shortcut routing)
  - `src/Blocker.App/NotesDrawerControl.xaml.cs` (`FocusComposer`)
- **Fix Applied**:
  - Removed duplicate `<KeyBinding>` for `Ctrl + Shift + N` from XAML; shortcut is routed strictly once in `PreviewKeyDown`, eliminating instant open/close jitter.
  - Replaced the abrupt discrete `Margin="24,24,340,24"` DataTrigger with a smooth 220ms `TranslateTransform.X` animation to `-170`, synchronizing smoothly with the drawer slide.
  - Added `FocusComposer()` to autofocus the note composer directly upon `Ctrl + N` and streamlined `Escape` key handling.

---

### 9. Kiosk Mode Bypass via Exposed Taskbar & Inert Watchdog in Allowed App Whitelist Mode

- **Status**: `Resolved & Verified (Shipped in v1.3.2)`
- **Priority**: `Critical`
- **Category**: Kiosk Lockdown, Process Whitelisting & Bypass Prevention
- **Affected Components**:
  - `src/Blocker.App/ZenShieldWindow.xaml.cs` (`WatchdogTimer_Tick`, `EnforceFocus`)
  - `src/Blocker.Service/Worker.cs` (`Meditation Mode Whitelist enforcement`)
- **Fix Applied**:
  - Added continuous taskbar suppression (`SetTaskbarVisibility(false)` with `SW_HIDE`) on every 500ms watchdog tick in `ZenShieldWindow.xaml.cs`.
  - Added active window suppression with `ShowWindow(fgHwnd, SW_FORCEMINIMIZE)` and `ShowWindow(fgHwnd, SW_HIDE)` on unauthorized foreground processes, re-asserting `_lastAllowedAppHwnd` or bringing `ZenShieldWindow` to `HWND_TOPMOST` if breaches persist.
  - Removed Session 0 `MainWindowHandle` gate in `Worker.cs` by detecting active console sessions via `WTSGetActiveConsoleSessionId()` and terminating unallowed user processes across session boundaries.

---

### 10. Scheduled Block Deactivates Blocking Outside Scheduled Window Even When Toggled "ON"

- **Status**: `Resolved & Verified (Shipped in v1.3.2)`
- **Priority**: `High`
- **Category**: Scheduling Lifecycle, Profile State Machine & User Mental Model Alignment
- **Affected Components**:
  - `src/Blocker.Core/ProfileStateLogic.cs` (`IsProfileActive`, `IsProfileLockedNow`)
  - `src/Blocker.App/ProfileViewModel.cs` (`LockColText`, `StateChipText`)
  - `tests/Sankalpa.Tests/ProfileStateLogicTests.cs`
  - `tests/Sankalpa.Tests/CommandHandlerTests.cs`
- **Fix Applied**:
  - Updated `ProfileStateLogic.IsProfileActive` so that when `profile.Enabled == true`, the profile remains active as `AlwaysOn` outside its scheduled hours rather than shutting off blocking.
  - Ensured `IsProfileLockedNow` only returns `true` during active schedule hours, leaving the profile unlocked (`Unlocked 🔓`) and freely editable/toggleable outside the schedule.
  - Updated `ProfileViewModel.LockColText` and status chips to reflect `Unlocked 🔓` and `"Active • Next window: ..."` when active outside the window.
  - Guaranteed automatic locking upon schedule window arrival next day regardless of previous user interactions.
