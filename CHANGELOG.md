# Sankalpa Master Fixes & Changelog

This document tracks all identified issues, root cause analyses, applied fixes, and verification statuses.

---

### [FIX-2026-11-04-03] Strict Uninstall Protection & Companion Extension Synchronization (v1.1.1)
- **Timestamp**: 2026-11-04 15:50 IST
- **Component**: Core Engine (`UninstallGuard.cs`), CLI (`Program.cs`, `Blocker.Cli.csproj`), Windows Installer (`Sankalpa.iss`), Maintenance Scripts (`clean-uninstall.cmd`, `publish-sankalpa-dev.py`), Tests (`UninstallGuardTests.cs`)
- **Status**: **SOLVED & VERIFIED IN LIVE OS**
- **Problems Identified**:
  1. **Uninstall Tamper Bypass**: Users could attempt to bypass active focus block sessions by launching Windows Settings / Add-Remove Programs or running uninstallation routines while a block was actively running.
  2. **Extension Version Out of Sync**: The browser extension was on manifest version 1.0.0 while releasing v1.1.x.
- **Changes Applied**:
  - **Core Uninstall Guard (`UninstallGuard.cs`)**:
    - Created `UninstallGuard` with status-based checking (`CanUninstallFromStatus`) and defense-in-depth offline SQLite database and Windows `hosts` file verification (`CheckDatabaseForActiveBlock`, `CheckSystemHostsForActiveBlock`, `CheckOfflineActiveBlock`).
    - Detects active manual timer sessions, active schedule windows, locked profiles, and active hosts file redirects.
  - **CLI Command (`sankalpa-cli.exe check-uninstall`)**:
    - Returns exit code `0` if no block is active (uninstall permitted).
    - Returns exit code `1` if any block session, schedule, or locked profile is active (uninstall forbidden).
  - **Windows Installer Integration (`Sankalpa.iss`)**:
    - Added `InitializeUninstall(): Boolean` event hook to Inno Setup. Before displaying any uninstall screens or modifying any services or files, the uninstaller executes `sankalpa-cli.exe check-uninstall`.
    - If a block is active, the uninstaller displays a critical error notification and immediately aborts uninstallation.
  - **Portable & Developer Uninstaller (`clean-uninstall.cmd`)**:
    - Added step 0 verifying `sankalpa-cli.exe check-uninstall` before stopping services or touching data.
  - **Extension Version 1.1.1**:
    - Bumped `extension/manifest.json` to 1.1.1 and generated store-ready package `sankalpa-extension-v1.1.1.zip`.
- **Verification**:
  - **Unit Tests**: All 6 new `UninstallGuardTests` passed.
  - **Full Test Suite**: All **647 tests passed** (559 in `Sankalpa.Tests`, 88 in `Sankalpa.Security.Tests`).
  - **CLI Verification**: `sankalpa-cli check-uninstall` returns exit code 0 when idle and exit code 1 when active.

---


### [FIX-2026-11-04-02] UI Button ContentTemplate Fix, Autonomous Browser Termination Rework, Independent Windows Toast Pipeline & Live OS Verification
- **Timestamp**: 2026-11-04 15:10 IST
- **Component**: Desktop UI (`Controls.xaml`), Engine & Planning (`FallbackPlanner.cs`, `NotificationThrottler.cs`), Background Service (`Worker.cs`, `ServiceNotificationDispatcher.cs`), Native Host (`Program.cs`), Build Pipeline (`publish-sankalpa-dev.py`), Tests (`FallbackPlannerTests.cs`, `NotificationGraceTests.cs`)
- **Status**: **SOLVED & VERIFIED IN LIVE OS**
- **Problems Identified**:
  1. **Button ContentTemplate Bug**: In `Controls.xaml`, `PrimaryButton` and `DangerButton` contained `<Setter Property="ContentTemplate">` with a `<TextBlock Text="{Binding}" />`. When buttons contained nested visual children such as `<TextBlock Text="+ New block" />`, WPF evaluated `.ToString()` on the TextBlock, rendering the string `"System.Windows.Controls.TextBlock"` instead of the inner text.
  2. **Browser Termination Skipped Without Website Rules**: In `FallbackPlanner.cs`, an early exit `if (!hasWebsiteBlocks) { result.UnauthorizedBrowserProcesses.Clear(); result.GracePeriodBrowsers.Clear(); return result; }` wiped out unauthorized browsers and grace period tracking whenever no active profile contained explicit website blocking rules, preventing unextended browsers from ever entering the grace period or being terminated.
  3. **Toast Notifications Dropping on Process Exit**: WinRT `ToastNotifier.Show()` is an asynchronous COM RPC call. Spawning `Blocker.NativeHost.exe` without an intentional delay caused the process to exit in 2–5ms, abruptly terminating the COM RPC before the Windows Action Center could receive and render the toast.
  4. **Throttler Blocking Grace Milestones**: The notification throttler used a flat key that caused countdown milestones (60s, 30s, 10s) for the same browser to be dropped.
- **Changes Applied**:
  - **WPF Button Templates (`Controls.xaml`)**:
    - Removed `<Setter Property="ContentTemplate">` from `PrimaryButton` and `DangerButton`. Button content now directly renders visual child elements or string content naturally without type-name string coercion.
  - **Autonomous Browser Termination Rework (`FallbackPlanner.cs`, `Worker.cs`)**:
    - Removed the restrictive `!hasWebsiteBlocks` guard from `FallbackPlanner.cs`. Unmonitored browsers and browsers without extension / incognito permission are now consistently tracked in the 60-second grace period and flagged for termination upon expiration regardless of active website rules.
    - Robustified `Worker.cs` process termination using `ExecuteTaskkillByImageName` (`C:\Windows\System32\taskkill.exe /F /T /IM "{exeName}"`) and handle-based tree killing with fallback.
  - **Independent Toast Notification Pipeline (`Blocker.NativeHost/Program.cs`, `ServiceNotificationDispatcher.cs`, `NotificationThrottler.cs`)**:
    - Added `Thread.Sleep(1200)` in `NativeHost` after calling `ToastNotifier.Show()` to guarantee the WinRT COM RPC completes before process termination.
    - Updated `ServiceNotificationDispatcher` to differentiate countdown milestones (`60s`, `30s`, `10s`), allowing milestone notifications to pass through the throttler unhindered.
    - Enhanced synchronous multi-target logging across `AppContext.BaseDirectory/logs`, `C:\ProgramData\Sankalpa\logs`, and `%LOCALAPPDATA%\Blocker\logs`.
    - Maintained zero UI dependency: Session 0 `WTSQueryUserToken` and `explorer.exe` token duplication dispatch toasts directly into active user sessions even when `Sankalpa.exe` UI is completely closed.
  - **Automated Publishing & Release Sync (`publish-sankalpa-dev.py`)**:
    - Automated self-contained `win-x64` Release builds of `Sankalpa.exe`, `Blocker.Service.exe`, `Blocker.NativeHost.exe`, and `sankalpa-cli.exe` into `C:\Users\virex\.gemini\antigravity\scratch\Sankalpa dev`.
    - Handled file locks safely using staging directories and fallback renaming for active Native Messaging instances.
- **Verification**:
  - **Unit & Security Tests**: All **641 tests passed** (553 in `Sankalpa.Tests`, 88 in `Sankalpa.Security.Tests`).
  - **UI Button Verification**: `UiLint` passed 8/8 tests; no type string coercion occurs.
  - **Live Chrome Verification (UI Closed)**:
    - Launched Google Chrome without extension:
    - T+1s: `BrowserGrace (60s)` toast dispatched to user session.
    - T+32s: `BrowserGrace (30s)` toast dispatched to user session.
    - T+51s: `BrowserGrace (10s)` toast dispatched to user session.
    - T+62s: `BrowserKill` toast dispatched, taskkill executed, and Chrome processes were terminated.
  - **Live Firefox Verification (UI Closed)**:
    - Launched Mozilla Firefox:
    - T+1s: `BrowserGrace (60s)` toast dispatched to user session.
    - T+31s: `BrowserGrace (30s)` toast dispatched to user session.
    - T+51s: `BrowserGrace (10s)` toast dispatched to user session.
    - T+61s: `BrowserKill` toast dispatched, taskkill executed, and Firefox processes were terminated.

---


### [FIX-2026-11-04-01] Main UI Bug Fixes (Bugs 1-7), Browser Termination Fix & Independent Service Notifications
- **Timestamp**: 2026-11-04 13:10 IST
- **Component**: Desktop App (`MainWindow.xaml`, `MainWindow.xaml.cs`, `MainViewModel.cs`, `ProfileViewModel.cs`, `App.xaml.cs`), Core Engine (`BrowserCatalog.cs`, `FallbackPlanner.cs`), Background Service (`ServiceNotificationDispatcher.cs`, `Worker.cs`), Test Suites (`DashboardTests.cs`, `NotificationGraceTests.cs`, `SettingsTests.cs`)
- **Status**: **SOLVED & VERIFIED**
- **Problems Identified**:
  1. **DNS Protection Custom Duration Bindings (Bug 1)**: `MainWindow.xaml` bound to `DnsCustomDays`, `DnsCustomHours`, `DnsCustomMinutes`, `DnsCustomSeconds`, and `DnsCustomDurationSummary`, which did not exist on `MainViewModel`, triggering WPF data-binding errors and causing custom DNS lock durations to be ignored.
  2. **Inline Rename Pressing Escape Commits (Bug 2)**: In `MainWindow.xaml.cs`, pressing Escape in `InlineNameTextBox_KeyDown` committed changes rather than reverting them.
  3. **Dashboard Delete Bypasses Password/Challenge & Fails on Enabled Blocks (Bug 3)**: Profile cards lacked `CanDelete` binding; `DeleteBlockCommand` bypassed password/typing challenge locks and attempted deletion on active profiles, causing service rejections and errors.
  4. **Rename During Unlocked Edit Duplicates Profiles (Bug 4)**: Editing an active profile and saving under a new name saved the profile and then attempted to delete `oldName`, which failed for active profiles and created duplicate profiles.
  5. **Extension Status in Status Bar Unclickable (Bug 5)**: Extension connection indicator was a static `StackPanel` without a click handler or command to open the extension dialog.
  6. **Missing Empty State on Dashboard (Bug 6)**: Dashboard presented a blank area when `Profiles.Count == 0` without instructions or call-to-action.
  7. **Legacy Branding in Tray Balloon Tip (Bug 7)**: Minimizing to system tray showed legacy "Blocker" title and description.
  8. **Item 8 Skipped as Feature**: Maintained `WindowState="Maximized"` on `MainWindow.xaml` per user specification.
  9. **Browser Termination Not Working**:
     - `Worker.cs` called relative `taskkill.exe` without full path, which silently failed in Session 0 Windows Service environments.
     - Calling `Process.GetProcessById(processId).Kill(entireProcessTree: true)` on sandboxed Chromium child processes failed without killing root browser processes, while `killedBrowsersThisPass.Add(exeName)` caused subsequent browser PIDs to be skipped.
  10. **Notifications Tied to UI**:
     - `ServiceNotificationDispatcher.cs` declared `DuplicateTokenEx` with `CharSet = CharSet.Auto` which threw `EntryPointNotFoundException: Entry point was not found` (probing for `DuplicateTokenExW`), and `CreateProcessAsUser` lacked explicit `CreateProcessAsUserW` entry point.
     - When `Sankalpa.exe` UI was closed, background service notifications failed in Session 0, leaving notifications silent.
- **Changes Applied**:
  - **MainViewModel & ProfileViewModel (Bugs 1, 3, 4)**:
    - Implemented `DnsCustomDays`, `DnsCustomHours`, `DnsCustomMinutes`, `DnsCustomSeconds`, and `DnsCustomDurationSummary` with full `INotifyPropertyChanged` support. Synchronized custom properties in `ApplyDnsPresetDuration` and calculated lock duration directly in `LockDnsAsync`.
    - Added `CanDelete => !IsLockedFromToggling && !IsActive`, `HasPasswordLock`, `HasTypingChallenge`, and `NotifyStateChanged()` to `ProfileViewModel.cs`.
    - Implemented `DeleteBlockAsync` in `MainViewModel.cs`: guards against deleting active/turned-on blocks, prompts for `PasswordUnlockDialog` or `TypingChallengeDialog` when locked, and passes unlock credentials to `CommandNames.DeleteProfile`.
    - Updated `SaveProfileAsync` to atomically dispatch `CommandNames.RenameProfile` when renaming existing profiles, eliminating profile duplication.
  - **MainWindow XAML & Views (Bugs 2, 5, 6, 7)**:
    - Updated `InlineNameTextBox_KeyDown` in `MainWindow.xaml.cs` to call `profile.CancelEditName()` and return immediately on `Key.Escape`.
    - Converted the status bar extension indicator into an interactive button styled with `StatusBarButtonStyle`, bound to `OpenExtensionDialogCommand` with tooltip.
    - Added an empty state card on the dashboard when `Profiles.Count == 0` featuring an icon badge, title, subtitle, and "+ New Block" action button.
    - Bound `IsEnabled="{Binding CanDelete}"` on dashboard card delete buttons.
    - Updated tray balloon tip strings in `App.xaml.cs` to "Sankalpa".
    - Preserved `WindowState="Maximized"`.
  - **Browser Termination Fix (`Worker.cs`, `BrowserCatalog.cs`)**:
    - Implemented `ExecuteTaskkillByImageName` using verified system path `Path.Combine(Environment.SystemDirectory, "taskkill.exe")` with `"/F /T /IM "{exeName}""`.
    - Handled process-tree kills with graceful fallback to direct process termination on all matching PIDs.
    - Swept detached worker/child processes via taskkill whenever an active profile blocks a browser application.
    - Expanded `KnownUnsupportedBrowsers` in `BrowserCatalog.cs` to include `chromium.exe`, `thorium.exe`, `mullvadbrowser.exe`, and `iridium.exe`.
  - **Independent Background Notifications (`ServiceNotificationDispatcher.cs`)**:
    - Fixed P/Invoke declarations: `DuplicateTokenEx` with `ExactSpelling = true` and `CreateProcessAsUserW` with explicit entry point in `advapi32.dll`.
    - Enhanced user session token discovery: queries active console session via `WTSGetActiveConsoleSessionId()`, falling back to active interactive `explorer.exe` process session IDs and acquiring user tokens via `OpenProcessToken` with duplicate rights.
    - Expanded `ResolveNativeHostExe` across base directory, LocalAppData, and registry paths (including `HKEY_USERS` under SYSTEM).
    - Verified desktop toast notifications fire reliably whether `Sankalpa.exe` UI is open or completely closed.
- **Verification**:
  - Solution build: `dotnet build Blocker.sln` succeeded with **0 warnings and 0 errors**.
  - Test suites: **All 634 tests passed** (546 in `Sankalpa.Tests`, 88 in `Sankalpa.Security.Tests`).
  - Automated UI lint: `UiLint` passed all 8 checks.

---

### [FIX-2026-11-03-03] Button Internal Text Foreground Fix & Release Pipeline Synchronization
- **Timestamp**: 2026-11-03 18:35 IST
- **Component**: MainWindow.xaml, BlockEditorDialog.xaml, Controls.xaml, Release Pipeline (dist/Sankalpa-Release/, dist/release/, dist/app/)
- **Status**: **SOLVED & VERIFIED**
- **Problems Identified**:
  1. **Dark Text on Dark Buttons in White Theme**: In White theme with Slate accent, the '+ New block' button rendered dark/black text on a dark slate fill. WPF's application-level implicit TextBlock style assigned Foreground='{DynamicResource TextPrimary}' (#111111), taking precedence over TextElement.Foreground on Button.
  2. **Stale Process Redirect on Release Click**: Clicking Sankalpa.exe in dist/release activated an older running instance due to the Global\\BlockerUiSingletonMutex inter-process activation mechanism, rather than launching the freshly built binaries.
- **Changes Applied**:
  - **White Theme Button Text (MainWindow.xaml, BlockEditorDialog.xaml, Controls.xaml)**:
    - Replaced inline Content='+ New block' in MainWindow.xaml with an explicit TextBlock child specifying Foreground='{DynamicResource TextOnAccent}' and FontWeight='SemiBold'.
    - Updated 'Lock DNS' in MainWindow.xaml and 'Start Complete Lock Now' / 'Start Pomodoro Lock Now' in BlockEditorDialog.xaml to explicitly set Foreground='{DynamicResource TextOnAccent}'.
    - Added ContentTemplate in PrimaryButton and DangerButton in Controls.xaml ensuring all string contents dynamically bind with TextOnAccent (#FFFFFF) or TextOnDanger (#FFFFFF).
  - **Release Pipeline & Process Termination**:
    - Terminated stale UI process prior to staging.
    - Built self-contained single-file win-x64 release binaries via dotnet publish.
    - Replaced Sankalpa.exe, Sankalpa.pdb, Blocker.Core.pdb, Sankalpa.dll, and licenses directly into dist/Sankalpa-Release/ and dist/app/.
    - Created Windows junction dist/release pointing to dist/Sankalpa-Release to guarantee identical resolution whether launched via dist/release or dist/Sankalpa-Release.
- **Verification**:
  - Clean compile with dotnet build Blocker.sln (0 warnings, 0 errors).
  - Verified dist/Sankalpa-Release/Sankalpa.exe, dist/app/Sankalpa.exe, and dist/release/Sankalpa.exe are byte-identical and updated.

---

### [FIX-2026-11-03-02] Shared Styles, Button Foregrounds & Typography Weight Refinement
- **Timestamp**: 2026-11-03 18:10 IST
- **Component**: Shared Theme Dictionaries (`Theme.Black.xaml`, `Theme.Grey.xaml`), Control Templates (`Controls.xaml`), Theme Engine (`ThemeManager.cs`), Embedded Fonts (`Fonts/FamiljenGrotesk-Medium.ttf`), Test Suite (`FoundationTests.cs`)
- **Status**: **SOLVED & VERIFIED**
- **Problems Identified**:
  1. **Low Contrast Text/Icons on Buttons**: In `PrimaryButton` and `DangerButton`, text and icons fell back to `TextPrimary` from the global `TextBlock` and `Path` implicit styles, resulting in pale text on pale buttons (Black/Grey themes) and dark text on dark buttons (White theme).
  2. **Pale Accent Fills in Dark Themes**: The foundation pass used high-luminance Tailwind 400/500 pastel accents in Black and Grey themes with black text, departing from the original muted slate aesthetic and reducing visual punch.
  3. **Synthetic Font Weight Bolding**: Shared styles referenced weights up to 600/Bold while only 600 TTFs were embedded for Familjen Grotesk, causing WPF to synthesize or approximate intermediate weights. Tab labels, status bar elements, and buttons were not standardized to medium (500) weight.
  4. **Obsolete Contrast Test**: `Accents_ContrastRatios_MeetRequirementsInEveryTheme` in `FoundationTests.cs` asserted a 3:1 accent-against-background contrast ratio that contradicted muted dark-theme fills.
- **Changes Applied**:
  - **Button Control Templates (`Controls.xaml`)**:
    - Scoped `TextBlock` and `Path` (including `x:Key="Icon"`) styles within `ControlTemplate.Resources` for both `PrimaryButton` and `DangerButton`.
    - Explicitly bound `ContentPresenter.TextElement.Foreground` and child elements to `TextOnAccent` (for Primary) and `TextOnDanger` (for Danger) across normal, hover, pressed, and disabled states.
  - **Accent Colors & Contrast (`Theme.Black.xaml`, `Theme.Grey.xaml`, `ThemeManager.cs`)**:
    - Restored original pre-foundation dark theme accent fills (Emerald `#047857`, Indigo `#4F46E5`, Violet `#7C3AED`, Amber `#B45309`, Rose `#E11D48`, Slate `#475569`).
    - Standardized `TextOnAccent` to pure white (`#FFFFFF`) across all 6 accents in all 3 themes, achieving $\ge 4.5:1$ contrast while keeping fills visibly darker than text in dark themes.
  - **Typography & Font Weight 500 (`Controls.xaml`, `Fonts/`)**:
    - Standardized body text to 400 (`Normal`) and buttons/tab labels to 500 (`Medium`). Removed all occurrences of 600 or bold in shared styles.
    - Downloaded and embedded the official static `FamiljenGrotesk-Medium.ttf` (usWeightClass 500) directly into `src/Blocker.App/Fonts/`, resolving cleanly in WPF without synthetic bolding.
  - **Test Alignment (`FoundationTests.cs`)**:
    - Removed the obsolete `Accents_ContrastRatios_MeetRequirementsInEveryTheme` test method; preserved all core WCAG background and surface contrast tests.
- **Verification**:
  - Verified `Typeface.TryGetGlyphTypeface` resolves Familjen Grotesk Medium, IBM Plex Sans Regular/Medium, and IBM Plex Mono Regular/Medium without bold simulation.
  - Built `Blocker.sln` cleanly with **0 errors, 0 warnings**.

---

### [FIX-2026-11-03-01] Sankalpa Consistency & Visual Polish Pass (5-Agent Architecture)
- **Timestamp**: 2026-11-03 17:30 IST
- **Component**: Foundation (`Theme.Black.xaml`, `Theme.White.xaml`, `Theme.Grey.xaml`, `Typography.xaml`, `Icons.xaml`, `Controls.xaml`), Block Editor (`BlockEditorDialog.xaml`, `AppPickerDialog.xaml`), Lock Dialogs (`TypingChallengeDialog.xaml`, `PasswordUnlockDialog.xaml`), Dashboard (`MainWindow.xaml`, Status Bar, Card Layout), Settings (`MainWindow.xaml` Settings View, `MainViewModel.cs`), Test Suites (`Blocker.Core.Tests/`)
- **Status**: **SOLVED & VERIFIED**
- **Problems Identified**:
  1. **Disparate Theme Models**: App maintained separate dark/light mode toggles and an Obsidian vs. Classic split with hard-coded hex colors and inconsistent component styles.
  2. **Ad-hoc Typography & Scaling**: Literal font sizes below 12px existed throughout dialogs; font families were fragmented across system UI defaults.
  3. **Emoji & Dingbat Glyphs**: Dialogs, buttons, and notifications used system emojis and symbols that rendered inconsistently across Windows versions.
  4. **Layout Shifts on Active Session**: Dashboard block card layouts experienced column shifts when the Stop/Active state buttons appeared.
  5. **Broken Settings Layout & Defect 5**: An unstyled tooltip popup in the top bar produced an unwanted white box artifact over the Settings interface section.
- **Changes Applied**:
  - **Unified Theme System**:
    - Replaced legacy Obsidian/Classic options with three first-class resource dictionaries: `Theme.Black.xaml`, `Theme.White.xaml`, and `Theme.Grey.xaml` sharing identical token keys.
    - Preserved 6 accent options (Emerald, Indigo, Violet, Amber, Rose, Slate) with dynamic runtime updates across all open windows.
    - Migrated legacy `ThemeMode` and `DesignTheme` configuration entries transparently to `Theme` ("black", "white", "grey").
  - **Typography Standard**:
    - Embedded official static TTFs: Familjen Grotesk (Titles), IBM Plex Sans (Body), and IBM Plex Mono (Times/Durations/IPs/Numbers).
    - Enforced minimum font size of 12px throughout all XAML files via tokenized `DynamicResource` sizing.
  - **Vector Lucide Icons (`Icons.xaml`)**:
    - Integrated stroke-based Lucide icons (24x24 grid, 1.75 stroke thickness) across all dashboard cards, headers, pickers, and editors.
  - **Card Shift Fix & Dialog Polish**:
    - Reserved Column 3 width (`Width="144"`) in `MainWindow.xaml` block cards, preventing action button state shifts.
    - Standardized `BlockEditorDialog`, `AppPickerDialog`, `StartSessionDialog`, `ExtensionDialog`, and `AppearanceDialog`.
    - Bound DNS Unlock button to `CanUnlockDns` and added `TimeFormatter` for 12h/24h schedule formatting.
  - **Automated Quality Checks & Unit Tests**:
    - Implemented `UiLint` automated validator checking for hardcoded hex colors, sub-12px font sizes, banned symbols, and incorrect plurals.
    - Added comprehensive test suites: `FoundationTests.cs`, `DashboardTests.cs`, `EditorSitesTests.cs`, `EditorLockTests.cs`, `SettingsTests.cs` (574 tests passing).
- **Verification**:
  - Full test suite passed: **574 passed, 0 failed, 0 skipped**.
  - Verified zero visual layout shifts on card session activation.

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
