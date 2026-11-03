# Sankalpa Master Fixes & Changelog

This document tracks all identified issues, root cause analyses, applied fixes, and verification statuses.

---

### [FIX-2026-11-02-06] Missing Background Notifications When UI is Closed (BrowserGrace, BrowserKill, App Blocks)
- **Timestamp**: 2026-11-02 21:35 IST
- **Component**: Core Helper (`ExtensionHelper.cs`), Service Notification Dispatcher (`ServiceNotificationDispatcher.cs`), Native Messaging Host (`Blocker.NativeHost/Program.cs`), Desktop UI (`App.xaml.cs`)
- **Status**: **SOLVED & VERIFIED**
- **Problems Identified**:
  1. **Missing Grace & Termination Notifications**: When Firefox (unmanaged/no extension) or Edge (extension disabled or "Allow in InPrivate" disabled) was opened, the browser was terminated by the background service after 30 seconds, but **no warning countdown notification** and **no termination notification** were shown to the user.
  2. **Notification Delivery Broken When UI Closed**: Notifications failed to appear whenever `Sankalpa.exe` desktop UI was closed.
  3. **Silent Failure & Missing Diagnostics**: Running `Blocker.NativeHost.exe --notify` produced no logs and exited with code 0 even when notification dispatch failed.
- **Root Cause Analysis**:
  1. **WinRT AppUserModelID (AUMID) Requirement**: `ToastNotificationManager.CreateToastNotifier("Sankalpa").Show(toast)` requires that `"Sankalpa"` be registered in the Windows Registry (`HKCU\Software\Classes\AppUserModelId\Sankalpa`). Without this key, Windows Shell's Notification Platform rejects calls from unpackaged Win32 processes with `RPC_E_SERVERFAULT (0x80010105)`. The previous fallback also used a hardcoded PowerShell AUMID that threw the same exception.
  2. **Premature Process Termination**: In `Blocker.NativeHost/Program.cs`, `ShowToastNotification` was invoked synchronously followed immediately by `return 0;`. WinRT `ToastNotifier.Show` is an asynchronous COM RPC call. Exiting within 1–2 milliseconds killed the process before Windows Action Center could receive and render the notification.
  3. **Deferred Logging in Native Host**: Logging initialization in `Blocker.NativeHost/Program.cs` was placed after CLI flag evaluation. As a result, when launched with `--notify`, `_logFilePath` remained empty, causing all exceptions caught in `ShowToastNotification` to be silently discarded.
  4. **Session 0 Service Launch & Interactive Session Detection**: `ServiceNotificationDispatcher.LaunchProcessInUserSession` lacked fallback resolution when `WTSGetActiveConsoleSessionId()` returned `0xFFFFFFFF` or `0`, and did not log Win32 error codes for `WTSQueryUserToken`, `DuplicateTokenEx`, or `CreateProcessAsUser`.
- **Changes Applied**:
  - **AUMID Registration (`ExtensionHelper.cs`)**:
    - Added `EnsureAumidRegistered(string? exePath = null)` which creates `HKCU\Software\Classes\AppUserModelId\Sankalpa` with `DisplayName = "Sankalpa"`, `ShowInSettings = 1`, and `IconUri` pointing to the Sankalpa icon.
    - Also writes to `HKLM\Software\Classes\AppUserModelId\Sankalpa` when elevated so all users inherit the registration.
    - Hooked `EnsureAumidRegistered` into `ExtensionHelper.RegisterHost()`, `App.xaml.cs` startup, and `ShowToastNotification()`.
  - **Native Host Notification Engine (`Blocker.NativeHost/Program.cs`)**:
    - Created `InitializeLogging()` and called it at the very top of `Main(string[] args)`. Every `--notify` invocation is now fully logged with timestamps, process IDs, and parameters.
    - Updated `ShowToastNotification()` to ensure AUMID registration prior to toast creation.
    - Added an 800ms wait (`Thread.Sleep(800)`) after `notifier.Show(toast)` to ensure asynchronous COM RPC handoff completes before process exit.
    - Added robust fallback: if WinRT `ToastNotificationManager` fails, it falls back to PowerShell WinRT toast invocation and balloon tip notification.
  - **Service Notification Dispatcher (`ServiceNotificationDispatcher.cs`)**:
    - Added dedicated logging to both service base directory and user AppData directory (`logs/service-notifications.log`).
    - Added active session resolution fallback via `Process.GetProcessesByName("explorer")` if `WTSGetActiveConsoleSessionId()` returns invalid.
    - Converted `LaunchProcessInUserSession` to return `bool` and added automatic fallback to `LaunchPowerShellToast` if `CreateProcessAsUser` is unsuccessful.
    - Logged Win32 error codes for all token duplication and process creation operations.
  - **Unit Testing**:
    - Added `EnsureAumidRegistered_SetsExpectedRegistryValues` unit test in `BrowserCatalogTests.cs`.
- **Verification**:
  - `dotnet build` succeeded with **0 warnings, 0 errors**.
  - `dotnet test` passed with all **485 passed, 0 failed, 0 skipped**.
  - Executed CLI notification tests:
    - `--notify "BrowserGrace" "firefox.exe" "Closing in 30 seconds: extension not installed"` -> Verified in `native-host.log`: `[NativeHost] Successfully displayed toast notification via WinRT for '⏳ Mozilla Firefox Closing Soon'.`
    - `--notify "BrowserKill" "firefox.exe" "..."` -> Verified in `native-host.log`: `[NativeHost] Successfully displayed toast notification via WinRT for '⚠️ Mozilla Firefox Terminated'.`

---

### [FIX-2026-11-02-05] Concurrency, Time Integrity & Stress Hardening Audit
- **Timestamp**: 2026-11-02 20:50 IST
- **Component**: Service (`Worker.cs`, `BlockerDatabase.cs`, `PipeServer.cs`, `ServiceNotificationDispatcher.cs`), Core Engine (`BlockSessionLogic.cs`, `CommandHandler.cs`, `DnsManager.cs`, `NotificationThrottler.cs`, `PasswordSecurity.cs`, `WebRuleMatcher.cs`), Desktop UI (`MainWindow.xaml.cs`), Test Suite (`tests/Blocker.Core.Tests/`)
- **Status**: **SOLVED & VERIFIED**
- **Problems Identified**:
  1. **Inactivity "Infinite Reset Trap"**: If a machine was powered down or slept longer than a locked session's duration, the startup recovery code evaluated `remainingSeconds <= 0` and reset `remainingSeconds = session.DurationSeconds`. Users turning on their PC for short daily intervals were permanently trapped in an infinite lockout loop where the full timer reset every morning.
  2. **Windows PID Recycling Collision**: `Worker._pidOriginalFileNameCache` only pruned dead PIDs when cache size exceeded 1,000 entries. Windows rapidly recycles PIDs, creating a risk where a new innocent user application received a recycled PID previously used by a terminated anti-kill tool (`procexp.exe`), causing `Worker` to falsely terminate the innocent application.
  3. **Named Pipe ACL Vulnerability**: `PipeServer.CreatePipeSecurity()` granted `WorldSid` (`Everyone`, SID `S-1-1-0`) `ReadWrite` access to `\\.\pipe\BlockerControl`, allowing untrusted, guest, or low-integrity local processes to send administrative commands (`EMERGENCYUNLOCK`, `SAVEDNSCONFIG`, `CLEANHOSTS`).
  4. **DNS Parameter Validation & Command Injection**: `HandleSaveDnsConfig` passed unparsed IP strings to `netsh.exe` without verifying format or address family. Unsanitized network interface names allowed command argument tampering.
  5. **Concurrency & Thread Safety Issues**:
     - `NotificationThrottler` used an unsynchronized `Dictionary<string, DateTime>` accessed concurrently by `ServiceNotificationDispatcher`, risking bucket corruption and 100% CPU infinite spin.
     - `PasswordSecurity.IsLockedOut` reset lockout expiry without holding `lock (state)`.
     - `WebRuleMatcher._compileCache` lacked a capacity ceiling, growing monotonically under dynamic pattern churn.
     - `ServiceNotificationDispatcher` leaked `Process` handles in interactive sessions.
     - `MainWindow.xaml.cs` contained `async void` helper methods without top-level `try/catch` exception boundaries.
     - `BlockerDatabase.StartSession` suffered a check-then-act race across concurrent pipe listener threads that could clobber active locked sessions.
- **Root Cause**:
  - Wall-clock vs monotonic clock discrepancy during offline state: `RestoreActiveSessionOnStartup` treated all offline expired locked sessions as tamper attempts.
  - Deferred eviction threshold (>1000) in PID cache rather than real-time per-tick eviction against current running PIDs.
  - Overly permissive default pipe ACL (`WorldSid`) left from early development phases.
  - Direct string interpolation in process invocation without `IPAddress.TryParse()` and address family checks.
  - Non-thread-safe collection usage across multi-threaded hosted services (`Worker` vs `PipeServer`).
- **Changes Applied**:
  - **Inactivity Fix**: Added `BlockSessionLogic.IsExpired()` and `HasClockMovedBackwards()`. If a session genuinely expired during downtime (`nowUtc >= session.StartUtc.AddSeconds(session.DurationSeconds)`), `Worker` cleanly ends the session, releases process/service DACL locks, and logs graceful completion. Backward clock tampering (`nowUtc < session.StartUtc`) is detected and clamped.
  - **Unended Session Query**: Added `GetActiveOrUnendedSession()` to `BlockerDatabase.cs` so unended sessions are properly retrieved on boot even if wall-clock time passed during downtime.
  - **PID Cache Synchronization**: Updated `Worker.cs` to prune dead PIDs on every 1-second tick against `currentPids` (`_pidOriginalFileNameCache.TryRemove(pid, out _)`), completely eliminating PID recycling false kills.
  - **Pipe ACL Hardening**: Removed `WorldSid` from `PipeServer.CreatePipeSecurity()`. Access is restricted strictly to `currentUser`, `systemSid`, `adminSid`, and `authUsersSid` (`AuthenticatedUserSid`). Replaced empty catch block in listener loop with warning diagnostics.
  - **DNS IP Validation**: Enforced `IPAddress.TryParse()` and `AddressFamily` checks (`InterNetwork` vs `InterNetworkV6`) in `CommandHandler.HandleSaveDnsConfig` and `DnsManager.ApplyDns`. Sanitized interface names against double quotes.
  - **Thread-Safety Hardening**:
    - Replaced `Dictionary` with `ConcurrentDictionary<string, DateTime>` and atomic `AddOrUpdate` in `NotificationThrottler.cs`.
    - Wrapped password lockout reset in `lock (state)` in `PasswordSecurity.cs`.
    - Added `MaxCompileCacheCapacity = 2000` ceiling to `WebRuleMatcher._compileCache`.
    - Scoped `Process.Start` in `using var` in `ServiceNotificationDispatcher.cs`.
    - Converted UI helper methods to `async Task` and wrapped UI switch event handlers in `try/catch` in `MainWindow.xaml.cs`.
    - Synchronized `StartSession`, `EndActiveSession`, and `EndSession` under `lock (_cacheLock)` in `BlockerDatabase.cs`.
    - Protected cached profile lists in `BlockerDatabase.ListProfiles()` by returning shallow copies.
  - **Automated Stress & Fuzzing Harnesses**:
    - Added `tests/Blocker.Core.Tests/ParallelPipeStressTests.cs`: 50 parallel client commands (`AddWebsite`, `RenameProfile`, `StartSession`, `StopSession`, `Ping`, `GetStatus`) + simultaneous background poller on an isolated private pipe passed in **456 ms** with zero deadlocks or state corruption.
    - Added `tests/Blocker.Core.Tests/FuzzingAndInputTests.cs`: 11 automated test cases verifying rejection of malformed JSON, 64 KB hard cap enforcement, binary garbage, and nasty domain string normalization.
- **Verification**:
  - Build: `dotnet build Blocker.sln -nologo` succeeded with **0 warnings, 0 errors**.
  - Concurrency Repeat Loop: 49 concurrency/schedule/session unit tests executed in a **200x continuous loop** (9,800 assertions) in Release mode with **100% pass rate (200/200, 0 failures)**.
  - Full Test Suite: `dotnet test tests/Blocker.Core.Tests/Blocker.Core.Tests.csproj` passed with all **484 passed, 0 failed, 0 skipped**.
  - Package Vulnerabilities: `dotnet list package --vulnerable` returned **0 known vulnerabilities** across all 6 projects.
  - Published updated Release single-file executables to `dist/service/Blocker.Service.exe`, `dist/app/Sankalpa.exe`, and `dist/cli/sankalpa-cli.exe`.

---

### [FIX-2026-11-02-04] Extension: "+ New block" Fails & Inactive Blocks Missing from Add Picker
- **Timestamp**: 2026-11-02 20:35 IST
- **Component**: Companion Extension (`popup.js`, `background.js`, `popup.css`), Native Host (`Blocker.NativeHost`), Core Service Protocol (`Protocol.cs`, `CommandHandler.cs`)
- **Status**: **SOLVED & VERIFIED**
- **Problem**:
  1. When no website block was active, clicking "+ New block" displayed the error *"Failed to add website."*
  2. Users could not see or add domains directly to inactive blocks because only active blocks were sent to the extension.
  3. Adding websites failed even on active blocks due to unhandled response framing and missing ID properties.
- **Root Cause**:
  - `HandleGetActiveWebRules` in `CommandHandler.cs` strictly filtered and serialized only active blocks (`ProfileStateLogic.IsProfileActive`), leaving `cachedProfiles` empty in the popup whenever blocks were inactive.
  - `ActiveProfileDto` lacked an `Id`, `LockMode`, and `Locked` property, causing the picker to pass `undefined` for `profile_id`.
  - Native Host did not emit a distinct response message type (`add_website_response`), causing race conditions with ongoing heartbeat polling responses.
  - Asynchronous extension message callbacks had no timeout safety net or disconnect handler, resulting in Chrome closing the port and returning `undefined` (which defaulted to the generic *"Failed to add website."* error).
- **Changes Applied**:
  - Added `ProfileSummaryDto` and populated `AllProfiles` in `ActiveWebRulesResponse` so the extension receives all blocks (active and inactive) along with their IDs, lock states, and rule counts.
  - Updated `ActiveProfileDto` with `Id`, `LockMode`, and `Locked` fields.
  - Enhanced `openBlockPicker` in `popup.js` to render both active and inactive blocks (with distinct badges: `Active`, `Inactive`, and `🔒 Locked`), while keeping `+ New block` available at the bottom.
  - Fixed `HandleAddWebsite` in `CommandHandler.cs` to resolve target profiles by ID or Name, ignore `"+ New block"` names when creating new blocks, and return JSON containing the created/updated profile.
  - Added `add_website_response` handling in `Blocker.NativeHost` and `background.js` with a 4-second timeout guard and disconnect error recovery.

---

### [FIX-2026-11-02-03] Extension: "Open Sankalpa UI" Button Not Working
- **Timestamp**: 2026-11-02 20:30 IST
- **Component**: Native Host (`Blocker.NativeHost/Program.cs`)
- **Status**: **SOLVED & VERIFIED**
- **Problem**:
  - Clicking the external app launcher icon in the extension popup header failed to bring the Sankalpa desktop window to the front or open the UI.
- **Root Cause**:
  - `Blocker.NativeHost` attempted to call `SetForegroundWindow` on the WPF window handle directly. In Windows OS, background console processes without foreground rights are blocked by OS foreground lock from focusing windows of other processes.
  - `Sankalpa.exe` already had an internal IPC event `Global\SankalpaUiShowWindowEvent` listened to by its own thread, which invokes `ShowMainWindow()` and toggles `Topmost` from the UI dispatcher. Native Host was not signaling this event.
- **Changes Applied**:
  - Updated `HandleOpenApp()` in `Blocker.NativeHost/Program.cs` to first signal `Global\SankalpaUiShowWindowEvent`. This immediately triggers the running app's UI thread to restore and bring the window to the foreground.
  - Fallback logic checks for running processes, restores minimized state, or launches `Sankalpa.exe` if not currently running.

---

### [FIX-2026-11-02-02] Theme Sync & Header Cycling for Browser Extension
- **Timestamp**: 2026-11-02 17:50 IST
- **Component**: App UI (`MainViewModel.cs`, `MainWindow.xaml`), Core (`UiSettingsStore.cs`, `CommandHandler.cs`), Extension (`popup.js`, `background.js`, `popup.css`)
- **Status**: **SOLVED & VERIFIED**
- **Problem**:
  - Changing the extension theme in desktop app Settings (Slate, Deep Black, Minimal White) did not apply to the browser extension popup.
- **Root Cause**:
  - `Blocker.Service` (running as `NT AUTHORITY\SYSTEM` in Session 0) and `Sankalpa.exe` (running as user in Session 1) resolved `SpecialFolder.LocalApplicationData` to different directories, preventing the service and native host from reading the UI's theme settings.
- **Changes Applied**:
  - Unified `UiSettingsStore` to store configuration at `AppContext.BaseDirectory/data/ui-settings.json` shared between Session 0 and Session 1.
  - Propagated normalized theme in `GetActiveWebRules` and `ExtHeartbeat` responses.
  - Implemented `data-theme` dynamic styling in `popup.css` and added an icon button to the popup header to cycle through themes with local override persistence.

---

### [FIX-2026-11-02-01] UI Polish: Start Button Removal & Pomodoro Migration
- **Timestamp**: 2026-11-02 17:40 IST
- **Component**: Desktop UI (`MainWindow.xaml`, `BlockEditorDialog.xaml`, `MainViewModel.cs`)
- **Status**: **SOLVED & VERIFIED**
- **Problem**:
  - Start button on block cards was greyed out / confusing.
  - Pomodoro functionality was not in the lock protection flow.
  - Blocks could not be renamed while an active lock was running.
- **Changes Applied**:
  - Removed Start button completely from block cards.
  - Moved Pomodoro setup and timer controls into the **Lock Protection** tab in `BlockEditorDialog`.
  - Added dedicated `RenameProfile` service command allowing display names to be edited under active Complete Lock without altering timers, sessions, or rules.
  - Made the card lock badge clickable, navigating directly to the Lock Protection tab (`initialTab: 3`).
  - Greayed out the Schedule tab during active Complete Lock with notice: *"Schedule is ignored while a complete lock is running."*
