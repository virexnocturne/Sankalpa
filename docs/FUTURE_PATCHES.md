# Sankalpa — Future Patches & Bug Fixes

This document tracks planned bug fixes, regression repairs, and behavioral patches for **Sankalpa Blocker**, clearly separated between active planned patches and verified resolutions.

---

## 📋 Active Backlog & Planned Patches

### 4. Collapsible & Expandable Keyboard Shortcuts Panel (Space-Saving UI)

- **Status**: `Planned`
- **Priority**: `Medium`
- **Category**: UI/UX & Settings Layout Ergonomics / Space Optimization
- **Affected Components**:
  - `src/Blocker.App/MainWindow.xaml` (Settings Tab, CATEGORY 4: KEYBOARD SHORTCUTS, lines 1170–1310)
  - `src/Blocker.App/AppearanceDialog.xaml` (Shortcuts Section, lines 140–210)
  - `src/Blocker.Core/UiSettingsStore.cs` (`IsKeyboardShortcutsExpanded`)

- **Description / Symptoms**:
  - As of v1.3.1, the Settings panel in `MainWindow.xaml` and `AppearanceDialog.xaml` lists 10 global keyboard shortcuts (`Ctrl + B`, `Ctrl + S`, `Ctrl + M`, `Alt + N`, `Ctrl + Shift + M`, `Ctrl + Alt + M`, `Ctrl + Shift + U`, `Ctrl + N`, `Ctrl + Shift + N`, `ESC`).
  - Every single shortcut item is rendered fully expanded with its title, multi-line explanatory description, and key combination badge in a tall vertical stack.
  - This block consumes over 550 pixels of vertical screen real estate, forcing users to scroll extensively through Settings to reach other configurations.
  - On standard 1080p laptop displays, the shortcuts block dominates the window height and clutters the visual hierarchy.

- **Root Cause Analysis**:
  1. In `MainWindow.xaml` (lines 1170–1310), the shortcuts section is implemented inside a static, non-collapsible `<Border>` containing a fixed `<StackPanel>` of 10 grids separated by divider lines.
  2. There is no toggle, `<Expander>`, or collapsible accordion container to hide or compact the list when the user already knows the shortcuts or wishes to save space.

- **Proposed Fix**:
  1. **Collapsible Container / Expander**: Wrap the Keyboard Shortcuts card in an interactive expander header (e.g., `<Expander>` or custom header button `[ Keyboard Shortcuts (10) ▾ / ▴ ]`) allowing users to collapse the entire card down to a single compact line when not needed.
  2. **Compact Two-Column View Mode**: Alternatively, provide a compact default state showing just the 2-column shortcut name and key combination badge (e.g. `New Block Profile [Ctrl + B]`), with an "Expand Descriptions" toggle or hover/click expansion for detailed descriptions.
  3. **State Persistence**: Store the expanded/collapsed preference in `UiSettingsStore` (`IsKeyboardShortcutsExpanded`) so user preference persists across application restarts.

---

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

### 6. `Win + Shift + S` Screenshot / Snipping Tool Hotkey Inoperable in Kiosk & Meditation Mode

- **Status**: `Planned`
- **Priority**: `High`
- **Category**: Input Interception, Kiosk Lockdown & System Hotkey Passthrough
- **Affected Components**:
  - `src/Blocker.Core/LowLevelKeyboardHook.cs` (`HookCallback`, lines 231–290)
  - `src/Blocker.App/ZenShieldWindow.xaml.cs` (Z-Order, `Topmost`, Foreground Watchdog)
  - `src/Blocker.Service/Worker.cs` (Process Whitelist in Lockdown)

- **Description / Symptoms**:
  - While running in Meditation Mode or full-screen System Lockdown / Kiosk mode, pressing the standard Windows screen snipping shortcut `Win + Shift + S` either fails completely or does not bring up the snipping overlay.
  - Users are unable to capture screenshots of notes, study materials, or allowed applications during focused sessions.
  - Expected behavior: Even during strict kiosk or meditation sessions, users should be permitted to take snips / screenshots with `Win + Shift + S` to paste images directly into their notes drawer or save references without breaking out of lockdown.

- **Root Cause Analysis**:
  1. **Low-Level Hook Key Suppression Timing**:
     - In `LowLevelKeyboardHook.cs`, the Windows key (`VK_LWIN` / `VK_RWIN`) is immediately intercepted and blocked (`block = true; return (IntPtr)1;`) to prevent the Start Menu from opening.
     - Because `VK_LWIN` is blocked at the raw kernel hook level, `explorer.exe` and the Windows Shell never register that the Windows key is down, so the OS-level hotkey handler for `Win + Shift + S` is never triggered.
     - While there is a special case branch (`if (vk == VK_S && winDown && shiftDown)` attempting to invoke `Process.Start("ms-screenclip:")`), this synthetic invocation suffers from critical flaws:
       - `GetAsyncKeyState(VK_LWIN)` may return 0 if the hook thread evaluates the state after the key release or during fast key sequences.
       - Calling `Process.Start("ms-screenclip:")` while returning `(IntPtr)1` suppresses the key event from reaching Windows, which often fails to trigger the shell host if UWP protocol activation is throttled or suspended.
  2. **Z-Order & Topmost Shield Window Occlusion**:
     - In full lockdown (non-allowed-app mode), `ZenShieldWindow` is configured with `Topmost = true` across all monitors (`WindowStyle="None"`, `WindowState="Maximized"`).
     - When Windows invokes `ScreenClippingHost.exe`, the semi-transparent snipping canvas renders beneath `ZenShieldWindow` because `ZenShieldWindow` reasserts topmost z-order via its 500ms watchdog loop (`BringToTop()` / `SetWindowPos(HWND_TOPMOST)`).
  3. **Focus Snatching by Watchdog**:
     - The watchdog timer in `ZenShieldWindow` continuously monitors the foreground HWND. When `ScreenClippingHost` gains momentary focus, if it is not immediately recognized or if the window handle transitions through an intermediate OS handle, the watchdog forcefully pulls focus back to `ZenShieldWindow`, prematurely terminating the snipping session.

- **Proposed Fix**:
  1. **Clean Win+Shift+S Key Passthrough in Hook**:
     - Modify `LowLevelKeyboardHook.cs` so that when `VK_LWIN` or `VK_RWIN` is pressed while `Shift` is held (or vice versa), the hook buffers or allows the key combination to pass cleanly through to the system instead of swallowing it, permitting the native Windows hotkey registration to fire naturally.
  2. **Z-Order Coordination During Snipping**:
     - In `ZenShieldWindow.xaml.cs`, register an active snipping detector or listen for `ScreenClippingHost` activation.
     - Temporarily yield `Topmost` (or drop `ZenShieldWindow` beneath `HWND_TOP` while `ScreenClippingHost` is the foreground process) until the snip completes (window closes or clipboard receives bitmap).
  3. **Whitelisting Snipping Host in Watchdog**:
     - Ensure both `ScreenClippingHost` and modern Windows 11 `SnippingTool` process names and window classes are exempted from foreground interception in `ZenShieldWindow.xaml.cs` and `Worker.cs`.

---

### 10. Scheduled Block Deactivates Blocking Outside Scheduled Window Even When Toggled "ON"

- **Status**: `Planned`
- **Priority**: `High`
- **Category**: Scheduling Lifecycle, Profile State Machine & User Mental Model Alignment
- **Affected Components**:
  - `src/Blocker.Core/ProfileStateLogic.cs` (`IsProfileActive`, `IsProfileActiveWithSessions`, `IsProfileLockedNow`)
  - `src/Blocker.App/ProfileViewModel.cs` (`CanToggle`, `CanEdit`, `IsActive`)
  - `src/Blocker.App/MainViewModel.cs` (`ToggleProfile`)

- **Description / Symptoms**:
  - When a block profile has a configured schedule (e.g., Weekdays 9:00 AM – 5:00 PM), once the scheduled window concludes (e.g., at 5:01 PM), the profile completely stops enforcing its website and application blocks, even though the profile's master toggle switch remains visibly turned "ON" in the dashboard.
  - Users expect that when a schedule window ends, the block should remain active and enforce blocking rules as long as the toggle is "ON", but become **unlocked and editable** so users can adjust rules or manually switch it "OFF" if they choose.
  - When the scheduled window arrives again the following day, regardless of whether the user turned the block off or kept it on, the scheduled lock must automatically engage, enforce rules, and enter a strict locked/uneditable state.

- **Root Cause Analysis**:
  1. In `src/Blocker.Core/ProfileStateLogic.cs` (`IsProfileActive`):
     ```csharp
     if (profile.Schedule != null && profile.Schedule.Count > 0 && ScheduleLogic.IsActive(profile.Schedule, localNow))
     {
         activeReason = "Schedule";
         return true;
     }

     if (!profile.Enabled)
     {
         activeReason = "None";
         return false;
     }

     if (profile.Schedule == null || profile.Schedule.Count == 0)
     {
         activeReason = "AlwaysOn";
         return true;
     }

     activeReason = "None";
     return false;
     ```
  2. Notice that if `profile.Schedule != null && profile.Schedule.Count > 0`, but `ScheduleLogic.IsActive` is `false` (the current time is outside the scheduled window):
     - Line 29 evaluates `if (profile.Schedule == null || profile.Schedule.Count == 0)`, which is `false`.
     - Execution falls straight to line 35: `activeReason = "None"; return false;`.
  3. Consequently, any profile with a defined schedule is forced completely inactive outside its scheduled hours, completely ignoring `profile.Enabled == true`.
  4. In `IsProfileLockedNow`:
     ```csharp
     if (profile.Schedule != null && profile.Schedule.Count > 0 && ScheduleLogic.IsActive(profile.Schedule, localNow))
     {
         return true;
     }
     ```
     During the schedule, the profile is strictly locked. Outside the schedule, `ScheduleLogic.IsActive` is false, meaning the profile is already unlocked—but because `IsProfileActive` returned false, no blocking rules are applied!

- **Proposed Fix**:
  1. **Decouple Schedule Enforcement from Manual State**:
     - In `ProfileStateLogic.cs`, if `profile.Enabled == true`, the profile should remain active (`activeReason = "AlwaysOn"` or `"Manual"`) even when outside of its scheduled window:
       ```csharp
       // 1. If currently inside active schedule window -> strictly active
       if (profile.Schedule != null && profile.Schedule.Count > 0 && ScheduleLogic.IsActive(profile.Schedule, localNow))
       {
           activeReason = "Schedule";
           return true;
       }

       // 2. If manually disabled outside schedule -> inactive
       if (!profile.Enabled)
       {
           activeReason = "None";
           return false;
       }

       // 3. If enabled (whether schedule is configured or not) -> active outside schedule window
       activeReason = "AlwaysOn";
       return true;
       ```
  2. **Differentiate Lock State from Active State**:
     - When outside the schedule window:
       - Because `ScheduleLogic.IsActive == false`, `IsProfileLockedNow` evaluates to `false` (assuming `LockMode == None` or challenge-based).
       - The block remains **Active** (rules enforced), **Unlocked** (toggle switch enabled), and **Editable** (user can click Edit or toggle it off).
  3. **Guaranteed Next-Day Automatic Schedule Activation**:
     - When the clock enters the scheduled window tomorrow, `ScheduleLogic.IsActive` becomes `true`.
     - Line 1 evaluates first: `activeReason = "Schedule"; return true;` regardless of whether `profile.Enabled` was toggled off, and `IsProfileLockedNow` returns `true`, locking the block down automatically.

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
