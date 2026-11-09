## Sankalpa v1.3.5 — Process Suspension Hardening, Cold-Launch Fast Path, Dynamic Switching & Path Pinning

Sankalpa v1.3.5 brings substantial performance, security, and stability upgrades to Zen meditation lockdown mode. This release completely eliminates cold-launch lag, grants directory-pinned immunity to critical Windows OS input and activation helpers, introduces dynamic cancellation and progress feedback for whitelisted app switching, cleans up session-spawned background terminals on session exit, and establishes strict executable path pinning to prevent CLI name collisions and system binary spoofing.

---

### 🚀 Key Improvements & Architectural Highlights

1. **Cold Launch Fast Path & Snapshot Elimination (Bugs 12 & 14)**
   - **Zero-Candidate Fast Exit:** `FindMainWindowForProcess` now short-circuits immediately when candidate process IDs are empty, completely deleting the costly desktop-wide `EnumWindows` and `GetProcessById` fallback that previously stalled UI cold launches by 5–7 seconds.
   - **Throttled Fallback Scans:** `IsAllowedAppValid()` background scans are rate-limited to at most once every 2 seconds when no app switch is active.

2. **OS System Helper Immunity via Directory Pinning (Bugs 12 & 14)**
   - **System Helper Guard:** Exempted `RuntimeBroker.exe`, `ctfmon.exe`, `sihost.exe`, `fontdrvhost.exe`, `ApplicationFrameHost.exe` (`System32`) and `TextInputHost.exe` (`SystemApps` subtree) from service-level suspension.
   - **Path Canonicalization:** All helper checks verify canonical directory locations (`Path.GetFullPath`), strictly preventing user-space binaries or `%TEMP%` spoofed executables from claiming immunity while restoring instantaneous typing, IME, and Windows 11 packaged app activation (e.g. Notepad).

3. **Dynamic App Switching, Cancellation & Mini-HUD Feedback (Bugs 13 & 14)**
   - **Cooperative Cancellation:** Replaced coarse boolean launch locks with `CancellationTokenSource`. Clicking a new whitelisted app instantly supersedes and cancels in-flight discovery while safely deduplicating repeat clicks.
   - **Decoupled 4s Watchdog Grace Period:** Window discovery timeout expanded to 30 seconds for cold launches (such as VLC building plugin cache), while the foreground suppression exemption is strictly bounded to 4 seconds to maintain ironclad lockdown.
   - **Window Geometry Preservation:** Replaced fixed dimension thresholds with Win32 `IsIconic` and `GetWindowPlacement`, restoring minimized windows while preserving maximized states.
   - **Mini-HUD Status:** Added non-intrusive status messages in the Mini-HUD ("Opening X...", "X didn't open — tap to retry") with automatic 6-second auto-dismissal.

4. **Session-Spawned Child Cleanup (Bug 13)**
   - **Handle & Creation Time Tracking:** `ProcessSuspender` retains `SafeProcessHandle` and records process creation timestamps via `GetProcessTimes` upon suspension.
   - **Targeted Session Termination:** On session exit, `FinishSession(sessionStartUtc)` terminates only child processes that were spawned during the active session (`creationTimeUtc >= sessionStartUtc`) and descended from an allowed app (`fromAllowedApp == true`), while resuming pre-existing processes cleanly. Prevents queued Claude Code terminals and CLI shells from popping up simultaneously when meditation ends.
   - **16-Hop Lineage Resolution:** Robust cycle-safe ancestor traversal (`IsDescendedFromAllowedApp`) tracks child lineages through shells and toolchains.

5. **Path-Pinning Allowed & Protected Binaries**
   - **Allowed App Install Root Resolution:** Resolves installation roots through `AppPathResolver` (collapsing Squirrel `app-x.y.z` directories to their install root) and communicates roots across IPC via `ConfirmTimerReadyRequest`.
   - **CLI vs. GUI Collision Fix:** `Worker.IsAppAllowed` enforces that allowed processes reside within their pinned install directory. This prevents CLI tools (e.g., `~/.local/bin/claude.exe`) from inheriting the GUI desktop app's allowance.
   - **System Binary Spoofing Guard:** Hardened `ProtectedProcesses.IsProtected` and `ProcessMatcher.IsBlocked` so binaries claiming to be `svchost.exe`, `csrss.exe`, `explorer.exe`, or `snippingtool.exe` outside their authentic system directories are denied protection and blocked.

---

### 🧪 Verification & Automated Testing
- **Automated Test Suite:** **899 / 899 Tests Passed** (89 in `Sankalpa.Security.Tests`, 810 in `Sankalpa.Tests`).
- **New Regression Test Suites:** Added `SystemHelperGuardTests.cs` and `ProtectedProcessesAndPathPinningTests.cs` covering directory traversal rejection, path-pinned allowance, and ancestor lineage checks.
