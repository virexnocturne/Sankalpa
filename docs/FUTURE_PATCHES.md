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

### 7. Pre-Session Confirmation Warning for Kiosk & Meditation Mode with "Do Not Show Again" (Accidental Lockout Prevention)

- **Status**: `Planned`
- **Priority**: `High`
- **Category**: UX Safety, Accidental Trigger Prevention & Session Lifecycle
- **Affected Components**:
  - `src/Blocker.App/MeditationViewModel.cs` (`StartMeditation`)
  - `src/Blocker.App/MainWindow.xaml.cs` (Global shortcut `Ctrl + Shift + M`)
  - `src/Blocker.App/MeditationStartWarningDialog.xaml` (New confirmation dialog)
  - `src/Blocker.Core/UiSettingsStore.cs` (`SuppressMeditationStartWarning`)
  - `src/Blocker.App/AppearanceDialog.xaml` / `MainWindow.xaml` (Settings toggle to re-enable warning)

- **Description / Symptoms**:
  - Currently, clicking the "Start Meditation" button on the dashboard or accidentally pressing the global hotkey `Ctrl + Shift + M` instantly initiates full-screen kiosk lockdown without any confirmation or pre-flight prompt.
  - If a user triggers the hotkey inadvertently (or clicks the button without realizing their lock mode is configured to Complete Lock or Password Lock with a long duration, e.g. 15–20 minutes), they find themselves immediately locked out of their operating system with no opportunity to abort.
  - Real-world impact: Users have accidentally locked their entire computer for 16+ minutes while in the middle of active workflows, meetings, or unsaved work.
  - Expected behavior: Provide a clear, respectful confirmation warning before engaging lockdown mode that explains the duration and lockdown rigor, equipped with a `"Do not show this warning again"` checkbox so experienced users can bypass it permanently without nuisance.

- **Root Cause Analysis**:
  1. In `MeditationViewModel.cs`, `StartMeditation()` immediately sets `IsActive = true`, starts the soundscape, instantiates `ZenShieldWindow`, covers all monitors, and locks the OS down synchronously on a single click or hotkey event.
  2. There is no pre-flight validation check against user preferences in `UiSettingsStore` to confirm intent before applying strict low-level keyboard hooks and process blocking.

- **Proposed Fix**:
  1. **Pre-Session Confirmation Dialog (`MeditationStartWarningDialog.xaml`)**:
     - Display a styled modal before entering lockdown when `!UiSettingsStore.SuppressMeditationStartWarning`.
     - **Session Summary**: Clearly display the target duration (e.g. `16 minutes`), Lock Mode (`Locked / Strict`, `Password`, `None`), and Allowed Applications count (`0 allowed apps — Full Lockout`).
     - **Explicit Warning Message**: *"You are about to enter full-screen lockdown. Desktop access, task switching, and non-whitelisted applications will be blocked until the session concludes."*
     - **Frictionless Opt-Out**: Include a prominent `[ ] Do not show this warning again` checkbox.
     - **Actions**: `[ Start Focus Session ]` (primary accent button) and `[ Cancel ]` (dismiss without starting).
  2. **Hotkey & ViewModel Guard**:
     - When `StartMeditation()` or `Ctrl + Shift + M` is invoked, check `UiSettingsStore.Instance.SuppressMeditationStartWarning`.
     - If `false`, display the dialog. If the user clicks `Cancel`, abort session start immediately. If they click `Start` with the checkbox checked, save `SuppressMeditationStartWarning = true` in `UiSettingsStore.cs`.
  3. **Settings Re-Enable Option**:
     - Add a toggle in Settings (`MainWindow.xaml` and `AppearanceDialog.xaml`):
       *"Show confirmation warning before entering Meditation & Lockdown"* allowing users who previously suppressed the warning to restore it at any time.

---

### 8. Notes Drawer Shortcut Double-Execution & Countdown Clock Layout Snapping (`Ctrl + Shift + N` / `Ctrl + N`)

- **Status**: `Planned`
- **Priority**: `High`
- **Category**: Input Handling, UI Animation & Focus Stage Layout Ergonomics
- **Affected Components**:
  - `src/Blocker.App/MainWindow.xaml` (`<KeyBinding>` line 27 & Focus Stage DataTrigger lines 1400–1408)
  - `src/Blocker.App/MainWindow.xaml.cs` (`PreviewKeyDown` lines 134–156)
  - `src/Blocker.App/NotesDrawerControl.xaml.cs` (`ApplyDrawerState`)
  - `src/Blocker.App/MeditationViewModel.cs` (`ToggleNotesDrawerCommand`, `NewNoteCommand`)

- **Description / Symptoms**:
  - In normal dashboard mode (with no active lock session or schedule running):
    1. **Double-Toggle Jitter on `Ctrl + Shift + N`**: Pressing the global hotkey `Ctrl + Shift + N` to toggle open the notes drawer fails to open or keep the drawer open. Instead, the middle countdown timer clock abruptly jerks to the left and immediately snaps back to center within milliseconds, leaving the drawer in its original closed state.
    2. **Unnecessary Clock Shift on `Ctrl + N`**: Pressing `Ctrl + N` to create a new note opens the composer, but simultaneously causes the large centered countdown clock to shift left by 170px (setting `Margin="24,24,340,24"`), even though the drawer is already smoothly sliding in.
    3. **Multi-Step `Escape` Dismissal**: When inside the note composer, pressing `Escape` once cancels the composer back to the notes card list (with the clock remaining shifted left), and pressing `Escape` a second time finally closes the drawer, snapping the countdown clock back to center.
  - Expected behavior:
    - Pressing `Ctrl + Shift + N` must reliably toggle the drawer open or closed without stuttering or double-executing.
    - Centered elements (the large countdown timer) should either remain statically centered or animate smoothly using a non-jarring layout transform without erratic multi-axis snapping.

- **Root Cause Analysis**:
  1. **Duplicate Hotkey Registration (Double Execution)**:
     - In `MainWindow.xaml` (line 27), a window-level key binding is registered:
       ```xml
       <KeyBinding Modifiers="Control+Shift" Key="N" Command="{Binding Meditation.ToggleNotesDrawerCommand}" />
       ```
     - Simultaneously, in `MainWindow.xaml.cs` (lines 147–155), a `PreviewKeyDown` handler intercepts the exact same key combination:
       ```csharp
       if (e.Key == Key.N && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
       {
           _viewModel.Meditation.ToggleNotesDrawerCommand?.Execute(null);
           e.Handled = true;
           return;
       }
       ```
     - Even though `e.Handled = true` is set, WPF's internal command routing still triggers the `KeyBinding` or triggers a second toggle event when re-routed through child controls. As a result, `ToggleNotesDrawerCommand` runs twice:
       `IsNotesDrawerOpen = false -> true -> false` in rapid succession.
     - The property change triggers the layout margin shift to `24,24,340,24` and then immediately back to `24`, visually manifesting as the clock shooting to the left and snapping right back to center.
  2. **Hard Layout Shift via Discrete Margin Setter**:
     - In `MainWindow.xaml` (lines 1403–1407), the centered focus stage uses a discrete `Margin` change inside a `<DataTrigger>`:
       ```xml
       <DataTrigger Binding="{Binding Meditation.IsNotesDrawerOpen}" Value="True">
           <Setter Property="Margin" Value="24,24,340,24" />
       </DataTrigger>
       ```
     - Because this is an instantaneous layout margin modification rather than a smoothly animated canvas translation or dedicated grid column layout, any state toggle causes the entire clock container to jump abruptly.
  3. **Multi-Stage `Escape` Key Stack**:
     - The `PreviewKeyDown` handler for `Key.Escape` processes dismissals hierarchically: First closes the composer (`IsComposerOpen = false`), then on the next `Escape` press closes the drawer (`IsNotesDrawerOpen = false`). While this layered dismissal is structurally sound, the violent clock movement between steps creates a jarring experience for the user.

- **Proposed Fix**:
  1. **Consolidate Shortcut Routing (Deduplicate)**:
     - Remove the duplicate `<KeyBinding>` for `Ctrl+Shift+N` from `MainWindow.xaml` and manage it exclusively in `PreviewKeyDown` in `MainWindow.xaml.cs` (or vice-versa), ensuring the command executes strictly once per keystroke.
  2. **Smooth Layout Transition for Focus Clock**:
     - Replace the abrupt `Margin="24,24,340,24"` discrete DataTrigger with a smoothed transition (e.g. animate `TranslateTransform.X` or place the focus clock inside a 2-column Grid with an animated right column width), preventing instant snapping when the drawer opens or closes.
  3. **Direct Focus Transfer**:
     - When `Ctrl + N` is pressed, open the drawer and immediately place keyboard focus into the title box (`MarkdownBox`) without requiring additional mouse clicks, and streamline `Escape` key cancellation.

---

### 9. Kiosk Mode Bypass via Exposed Taskbar & Inert Watchdog in Allowed App Whitelist Mode

- **Status**: `Planned`
- **Priority**: `Critical`
- **Category**: Kiosk Lockdown, Process Whitelisting & Bypass Prevention
- **Affected Components**:
  - `src/Blocker.App/ZenShieldWindow.xaml.cs` (`ShowMiniHud`, `WatchdogTimer_Tick`, `EnforceFocus`, `SetTaskbarVisibility`)
  - `src/Blocker.Service/Worker.cs` (`Meditation Mode Whitelist enforcement`, lines 958–1010)
  - `src/Blocker.Core/LowLevelKeyboardHook.cs`

- **Description / Symptoms**:
  - When running in Meditation Mode or System Lockdown with whitelisted apps (e.g. allowing `notepad.exe` or VS Code), users can completely bypass the kiosk lockdown and switch to unauthorized applications.
  - The Windows Taskbar (`Shell_TrayWnd`) becomes visible and clickable at the bottom of the screen.
  - Users can click on the Taskbar or Start Menu to launch or switch to any other application (such as Google Chrome, Discord, or games).
  - Even though a 500ms watchdog timer (`_watchdogTimer`) runs continuously inside `ZenShieldWindow.xaml.cs` to monitor the foreground window, it fails to enforce lockdown: the unauthorized application remains in the foreground and fully interactive, rendering the kiosk whitelist non-functional.

- **Deep Technical Diagnosis & Root Causes**:
  1. **Taskbar Uncovered & Re-asserted by Windows Shell in Allowed App Mode**:
     - In strict full-lockout mode, `ZenShieldWindow` is `Topmost = true` across all monitors, physically covering the Windows Taskbar.
     - However, when an allowed app is launched, `ShowMiniHud()` executes:
       ```csharp
       Topmost = false;
       SetWindowPos(mainHandle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
       ```
     - Dropping `ZenShieldWindow` to `HWND_BOTTOM` places it beneath the Windows Taskbar (`Shell_TrayWnd`).
     - Although `SetTaskbarVisibility(false)` was called once at session startup, Windows Explorer automatically restores and repaints the taskbar whenever a standard window (like `notepad.exe`) receives focus. Because `SetTaskbarVisibility(false)` is never maintained continuously in the watchdog loop, the entire Windows Taskbar remains permanently visible and interactive.
  2. **Inert Foreground Watchdog (`SetForegroundWindow` Failure via Windows UIPI)**:
     - When a user clicks the taskbar or launches an unauthorized application (e.g., Chrome), `WatchdogTimer_Tick` detects that the foreground PID is neither `Sankalpa.exe` nor in `_allowedAppProcessNames`.
     - It calls `EnforceFocus()`:
       ```csharp
       private void EnforceFocus()
       {
           if (_isAllowedAppMode && _lastAllowedAppHwnd != IntPtr.Zero && IsWindow(_lastAllowedAppHwnd))
           {
               ShowWindow(_lastAllowedAppHwnd, SW_RESTORE);
               SetForegroundWindow(_lastAllowedAppHwnd);
           }
           else
           {
               RestoreZenScreen();
           }
       }
       ```
     - **Silent Win32 API Failure**: Under Windows User Interface Privilege Isolation (UIPI) and foreground lock policies (`LockSetForegroundWindow`), a background application cannot forcibly steal focus from an active foreground application unless it owns the input queue or was the last active process. Windows rejects `SetForegroundWindow(_lastAllowedAppHwnd)` silently (returning `FALSE`) and merely flashes the allowed app's taskbar icon.
     - **Zero Suppression of the Unauthorized Window**: `EnforceFocus()` never calls `ShowWindow(fgHwnd, SW_FORCEMINIMIZE)` or `PostMessage(fgHwnd, WM_CLOSE, ...)` on the unauthorized foreground window (`fgHwnd`). It leaves the unauthorized window active, maximized, and receiving mouse/keyboard input.
     - **Fallback Deadlock**: Because `IsWindow(_lastAllowedAppHwnd)` remains `true` (Notepad is still running in the background), the `else` branch (`RestoreZenScreen()`) is never triggered. The watchdog loops infinitely every 500ms making failed `SetForegroundWindow` calls while the user freely uses the unblocked app.
  3. **Session 0 GUI Isolation Blindness in Background Service (`Worker.cs`)**:
     - The background Windows Service (`SankalpaService` / `Worker.cs`) also attempts whitelist enforcement at line 970:
       ```csharp
       IntPtr hwnd = IntPtr.Zero;
       try { hwnd = targetProc.MainWindowHandle; } catch { }
       if (hwnd != IntPtr.Zero)
       {
           ShowWindow(hwnd, SW_FORCEMINIMIZE);
           targetProc.Kill(entireProcessTree: true);
       }
       ```
     - Because Windows Services run isolated in **Session 0**, `targetProc.MainWindowHandle` is always `IntPtr.Zero` when querying processes running on the interactive desktop in **Session 1** (or modern packaged UWP apps).
     - Because `hwnd == IntPtr.Zero`, the service skips termination completely, leaving the user desktop unguarded by the service.

- **Proposed Fix**:
  1. **Continuous Taskbar Suppression During Allowed App Mode**:
     - Call `SetTaskbarVisibility(false)` inside `WatchdogTimer_Tick` on every tick, or register an `ABM_SETSTATE` shell message setting the taskbar to auto-hide / hidden state so `Shell_TrayWnd` cannot resurface.
  2. **Active Suppression of Disallowed Foreground Windows**:
     - In `WatchdogTimer_Tick`, when `fgHwnd` belongs to an unauthorized process, immediately suppress the rogue window:
       ```csharp
       ShowWindow(fgHwnd, SW_FORCEMINIMIZE);
       ShowWindow(fgHwnd, SW_HIDE);
       ```
     - Then re-assert `ZenShieldWindow` as `Topmost = true` or use `AttachThreadInput` to legitimately transfer focus back to `_lastAllowedAppHwnd`.
  3. **Immediate Zen Backdrop Re-assertion on Focus Breach**:
     - If an unauthorized process gains focus, immediately bring `ZenShieldWindow` to `HWND_TOPMOST` to cover all monitors until the user explicitly selects the allowed app from the Mini-HUD or hotkey.
  4. **Direct Process Name Whitelisting in `Worker.cs` without `MainWindowHandle` Guard**:
     - In `Worker.cs`, remove the `if (hwnd != IntPtr.Zero)` gate for whitelist enforcement, and verify interactive session PIDs via `Process.SessionId == WTSGetActiveConsoleSessionId()` so the service reliably terminates non-whitelisted processes across Session 0 boundaries.

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
