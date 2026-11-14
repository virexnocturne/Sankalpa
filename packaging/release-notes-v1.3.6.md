## Sankalpa v1.3.6 — Zen Mode UI Performance, Comprehensive CLI Meditation Controls, Audio UX & Hardened Emergency Unlock

Sankalpa v1.3.6 delivers comprehensive performance optimizations to Zen meditation lockdown mode, expands the CLI with full meditation and security command-line controls, introduces custom playlist imports with catalog synchronization, and refines profile lock mode editing and emergency unlock ergonomics.

---

### 🚀 Key Improvements & Architectural Highlights

1. **Zen Mode UI Lag & Modal Password Unlock Optimization (`fix/zen-ui-lag`)**
   - **Discrete 5-Event WinEvent Filtering:** Narrowed broad window hooks strictly to required event types (`0x0003`, `0x0016`, `0x0017`, `0x8001`, `0x8003`) with lock-free trailing-edge coalescing, completely eliminating UI-thread callback floods.
   - **Idempotent Mini-HUD & Non-Blocking Minimize:** Restricted allowed-app focus/z-order manipulation strictly to hidden-to-shown transitions; allowed-app minimization uses `SetWindowPos(..., SWP_ASYNCWINDOWPOS)` + `ShowWindowAsync(..., SW_MINIMIZE)` to prevent window hanging.
   - **Dedicated MTA Background Throttler Worker:** Offloaded process enumerations, COM audio session checks (`IAudioSessionManager2`), 25s working-set memory trims, and crash-journal disk I/O onto a dedicated background worker.
   - **Deduplicated Async Single-Verify Password Unlock:** PBKDF2 (100k iterations) verification runs asynchronously off the UI thread exactly once per attempt with fail-closed safety and modal tracker suppression.

2. **Comprehensive CLI Controls (`sankalpa-cli`)**
   - **Meditation & Lockdown Commands:** Full CLI interface for starting, stopping, querying, and scheduling meditation sessions (`sankalpa-cli meditation start --apps ... --sound ...`, `sankalpa-cli meditation status`, `sankalpa-cli meditation note`, `sankalpa-cli meditation schedule`).
   - **Delayed Unlock & DNS Challenge Integration:** Request or cancel delayed unlock tokens and query DNS typing verification challenges via terminal (`sankalpa-cli delayed-unlock`, `sankalpa-cli dns challenge`).
   - **Audit Events & Web Rules:** Query real-time blocking audit trails (`sankalpa-cli events`) and inspect active extension web rules (`sankalpa-cli webrules`).

3. **Audio UX Improvements, Playlist Import & Dynamic Catalog Sync**
   - **M3U / M3U8 Playlist Support:** Secure local playlist parser hardened against UNC/SMB relays, remote URLs, and directory traversal.
   - **Dynamic Soundscape Synchronization:** Cross-window event bus (`SoundscapeCatalog.CatalogChanged`) synchronizes imported tracks and custom audio collections without app restarts.
   - **Mini-HUD Topmost Stability:** Refined `EnsureTopmost` guarantees Mini-HUD visibility over borderless and fullscreen allowed apps without stealing keyboard input.

4. **Emergency Unlock & Profile Ergonomics**
   - **Emergency Schedule Bypass:** Emergency unlock immediately deactivates active recurring schedules and clears pending meditation countdowns, preventing re-lock loops.
   - **Unlocked Profile Editing:** Profiles that are turned off or unlocked can adjust lock modes back to None without redundant password re-prompts.

---

### 🧪 Automated Verification & Testing
- **Automated Test Suite:** **1,099 / 1,099 Tests Passed** (123 in `Sankalpa.Security.Tests`, 976 in `Sankalpa.Tests`).
- **Regression Suites:** Includes discrete WinEvent filter tests, Mini-HUD idempotency tests, async PBKDF2 unlock tests, and comprehensive CLI end-to-end integration tests (`CliTests.cs`).
