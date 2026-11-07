## Critical bug fixes (Sankalpa v1.3.2)

Sankalpa v1.3.2 is a critical security, stability, and architecture overhaul of **Meditation Mode**, eliminating destructive process killing, duplicate zombie schedules, and system lockouts.

---

### 🛡️ Critical Bug Fixes & Architectural Improvements

1. **Singleton Schedule Persistence & Zombie Row Purge**
   - **Root Cause:** Defaulting schedule ID to `0` caused repeated SQLite `INSERT` statements, spawning conflicting duplicate schedule rows in `MeditationSchedules` that could trigger multiple or unexpected lockout timers.
   - **Fix:** Enforced singleton upsert strictly on `Id = 1` (`INSERT ... ON CONFLICT(Id) DO UPDATE`). Added automatic database migration in `BlockerDatabase` that copies the newest schedule into `Id = 1` and cleans up all rows `WHERE Id > 1`.

2. **Schedule Disabling Persistence Barrier**
   - **Root Cause:** `MeditationViewModel.OpenEditDialog()` had an `if (IsScheduleEnabled)` guard, preventing the disabled state from being sent across IPC to the service when untoggled.
   - **Fix:** Removed the guard; toggling a schedule to disabled now unconditionally persists `Enabled = false` to the database.

3. **Non-Destructive Process Suspension (Zero Work Loss)**
   - **Root Cause:** Meditation Mode previously called `targetProc.Kill(entireProcessTree: true)` and ran `taskkill /F /IM`, abruptly terminating unallowed applications and destroying unsaved user documents, code, and browser sessions.
   - **Fix:** Created `ProcessSuspender` utilizing native Windows NT kernel APIs (`NtSuspendProcess` and `NtResumeProcess`). Applications outside the allowed whitelist are safely paused at the thread level and automatically resumed with 100% of their state and unsaved work intact when meditation ends.

4. **Audio Engine & System Process Immunity**
   - **Root Cause:** `audiodg.exe` and background audio handlers were not protected, resulting in audio glitches, soundscape stutter, or media player crashes.
   - **Fix:** Added `audiodg` to `ProtectedProcesses.WellKnown` alongside `taskmgr`, `explorer`, `dwm`, `csrss`, and `svchost`.

5. **Emergency Escape Chord (`Ctrl + Shift + Alt + Escape`)**
   - **Root Cause:** `LowLevelKeyboardHook` suppressed `Esc` whenever `Ctrl` or `Alt` was pressed, eating the escape sequence before WPF or the emergency handler could inspect it.
   - **Fix:** Implemented a continuous 3.0-second hold detector for `Ctrl + Shift + Alt + Escape` at the very top of `HookCallback` before any key suppression runs. Holding the chord writes `EMERGENCY_UNLOCK` markers and immediately closes the lockdown screen.

6. **Administrative & DACL Lockout Prevention**
   - **Root Cause:** Meditation Mode fed into `isLockedActive` and `anyHardLocked`, which locked down the service and process DACLs and terminated Task Manager, Process Explorer, and Resource Monitor.
   - **Fix:** Completely decoupled Meditation Mode from kernel DACL lockdowns and anti-kill tool termination. Administrators can always access Task Manager and inspect or manage background services.

7. **Session 0 Isolation & Two-Phase Handshake**
   - **Root Cause:** Windows services run in Session 0 and cannot reliably enumerate user-session windows (`MainWindowHandle == 0`), making background window filtering impossible from the service alone.
   - **Fix:** Implemented a two-phase handshake:
     - The service initiates scheduled routines with `Status = "Pending"`.
     - The desktop client launches `ZenShieldWindow`, enumerates visible desktop windows, and sends IPC command `ConfirmTimerReady`.
     - The service promotes the session to `"Active"` and suspends only unallowed visible desktop applications.
     - If the desktop client fails to confirm within 10 seconds, the session cancels cleanly without touching any processes.

8. **Windows Shell & Taskbar Concealment**
   - **Root Cause:** Calling `SetTaskbarVisibility(false)` and `HWND_BOTTOM` demoted the Zen screen and broke the Windows taskbar across service or app restarts.
   - **Fix:** Removed taskbar suppression and window demotion. Fullscreen borderless windows naturally overlay the taskbar cleanly. Whitelisted apps are floated with `HWND_TOPMOST` so they float above the Zen backdrop without desktop flicker.

9. **Notes Drawer & Quick Notes Keyboard Shortcuts**
   - **Root Cause:** Hotkeys were bound to `MainWindow`, which was not focused while `ZenShieldWindow` was active.
   - **Fix:** Bound `Ctrl + Shift + N` (notes drawer), `Alt + N` (quick notes), and `Esc` (close drawer) directly in `ZenShieldWindow.xaml.cs`.

10. **Natural Session Expiration & Resumption Lifecycle**
    - **Root Cause:** Reading expired sessions in the database could mark them completed without un-suspending paused processes.
    - **Fix:** Session lifecycle routed through `Worker.cs` with unconditional `ProcessSuspender.Default.ResumeAll()` calls on session completion, manual stop, emergency unlock, and service shutdown.

11. **Client Startup State Hydration**
    - Added `ConnectToServiceAsync()` to query `GetMeditationSchedules` on desktop launch, keeping the UI toggle, scheduled time, days, duration, and soundscape synchronized with the database.

---

### 🧪 Verification & Automated Testing
- **Automated Test Suite:** **836 / 836 Tests Passed** across `Sankalpa.Tests` and `Sankalpa.Security.Tests` (0 failed, 0 skipped).
- **Integration Tests:** 8 real-world integration tests in `MeditationOverhaulTests.cs` verifying singleton upserts, database migration, real NT kernel child process suspension and resumption, protected process immunity, and IPC two-phase handshakes.
- **Release Database Migration:** Tested and verified against production database `dist/Sankalpa-Release/data/blocker.db`, safely consolidating legacy duplicate schedules into a single row.
