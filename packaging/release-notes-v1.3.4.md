## Sankalpa v1.3.4 — Process Lifecycle Performance, App Picker Guard & System Hardening

Sankalpa v1.3.4 delivers major process lifecycle optimizations, eliminates UI stalls during application launching in Zen lockdown, hardens the emergency escape chord under heavy load, prevents self-binary selection in the App Picker, restores the Windows Search Grid icon and Start Menu shortcuts, and ensures graceful resilience against missing audio assets.

---

### 🚀 Key Improvements & Architectural Highlights

1. **Service Suspension Hot-Loop Optimization (Bug 3A)**
   - **Per-Tick Caching:** Active session ID is queried once per tick and self-process name is cached statically, eliminating redundant IPC calls.
   - **Syscall Throttling:** Non-allowed processes that are already suspended are tracked in a dedicated set, bypassing redundant `NtSuspendProcess` syscalls.
   - **Watchdog Allocation Removal:** Replaced expensive `Process.GetProcessesByName()` and `GetProcessById()` allocations with lightweight, non-allocating process lookups.

2. **Strict Child Process Directory Exemption (Bug 3A)**
   - **Directory-Bound Inheritance:** Allowed applications (such as Microsoft Edge) can freely spawn renderer, gpu, and utility worker child processes provided they reside within the allowed parent application's installation folder.
   - **Default-Deny Preservation:** Non-allowed processes outside the allowed directory remain strictly suspended, preventing privilege leakage or escape vectors.

3. **Asynchronous Window Discovery & Launching (Bug 3A)**
   - **Background Thread Offloading:** App launch and window discovery run on `Task.Run` without blocking the WPF UI thread, keeping the session countdown timer and UI fluid.
   - **Dynamic PID Reconciliation:** Rebuilds candidate process ID sets on every polling iteration with proper disposal of process handles.

4. **Emergency Escape Chord Hardening (Bug 3B)**
   - **Thread Priority Elevation:** Low-level keyboard hook thread runs with `ThreadPriority.Highest`.
   - **Jitter Grace Period:** Tolerates transient chord state drops of up to 300 ms to compensate for laptop keyboard hardware rollover limitations.
   - **Redundant Polling Thread:** A dedicated background thread samples `GetAsyncKeyState` every 50 ms with single-fire synchronization, guaranteeing escape chord responsiveness even under high CPU stress.

5. **App Picker Self-Binary Exclusion (Bug 4)**
   - **Self-Binary Guard:** Excluded `Sankalpa.exe`, `Blocker.Service.exe`, `Blocker.NativeHost.exe`, and `sankalpa-cli.exe` from the "Pick apps..." scan list.
   - **Input Validation:** Added strict validation checks in `BlockEditorDialog` preventing manual or programmatic addition of Sankalpa binaries to blocklists.

6. **Windows Search Grid Icon & AUMID Fix (Bug 1)**
   - **Top-Level Start Menu Shortcut:** Registered `{autoprograms}\Sankalpa.lnk` with embedded PE `IconIndex: 0` for direct indexing by Windows 10/11 Search.
   - **AUMID Resolution:** Updated `ExtensionHelper.EnsureAumidRegistered` to ensure all shortcuts and toast notification AUMID entries resolve directly to `Sankalpa.exe`.
   - **Asset Deployment:** Configured `app.ico` and `logo*.png` to deploy into the application output directory.

7. **Resilient Audio Asset Handling (Bug 5)**
   - **Crash Prevention:** Replaced unhandled `FileNotFoundException` in `AmbientSoundEngine.SetSoundscape` with graceful fallback and property change notifications if audio files are removed from AppData.
   - **Global Dispatcher Guard:** Marked `DispatcherUnhandledException` as handled in `App.xaml.cs` to prevent abrupt process termination.

---

### 🧪 Verification & Automated Testing
- **Automated Test Suite:** **876 / 876 Tests Passed** (89 in `Sankalpa.Security.Tests`, 787 in `Sankalpa.Tests`).
- **Security Invariants Preserved:** Default-deny lockdown, key-blocking synchronicity, and anti-tamper mechanisms remain strictly intact.
