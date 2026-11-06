# Sankalpa — Future Patches & Bug Fixes

This document tracks planned bug fixes, regression repairs, and behavioral patches for **Sankalpa Blocker**.

---

## Patches & Fixes

### 1. Scheduled Block in Extension Does Not Show "Locked" Badge During Active Schedule

- **Status**: `Resolved & Verified`
- **Priority**: `High`
- **Category**: Extension UI & Protocol Synchronization / Status Accuracy
- **Affected Components**:
  - `src/Blocker.Core/CommandHandler.cs` (`HandleGetActiveWebRules`)
  - `extension/popup.js` (`renderBlockRow` / `renderActiveList`)
  - `src/Blocker.Core/Protocol.cs` (`ActiveProfileDto`, `ProfileSummaryDto`)

- **Description / Symptoms**:
  - When a block profile is triggered by a schedule and actively enforcing rules, the browser companion extension popup correctly displays `ACTIVE` and the remaining scheduled countdown timer (e.g., `10:20 remaining`).
  - However, the `🔒 Locked` badge is completely missing from the block row in the extension popup (while other locked blocks, such as manually locked sessions, display `🔒 Locked` as expected).
  - Even though the core blocker rules define scheduled sessions as locked and non-tamperable while active, the extension UI fails to reflect this locked status to the user.

- **Root Cause Analysis**:
  1. In `src/Blocker.Core/CommandHandler.cs` (`HandleGetActiveWebRules`):
     - The calculation for `isLocked` currently checks:
       ```csharp
       bool isLocked = (session != null && session.Locked && session.ProfileId == profile.Id) ||
                       (profile.LockMode != LockMode.None);
       ```
     - It does **not** call `ProfileStateLogic.IsProfileLockedNow(profile, session, localNow)` or check if the profile is actively locked via its schedule (`ScheduleLogic.IsActive`).
     - Consequently, if a profile has `LockMode.None` configured on its general lock tab, `HandleGetActiveWebRules` broadcasts `Locked = false` and `LockMode = "None"` in the payload to the browser extension, ignoring the fact that it is currently executing an active scheduled block.
  2. In `extension/popup.js`:
     - The popup determines whether to show the lock badge via:
       ```javascript
       const isLocked = p.locked || (p.lockMode && p.lockMode !== 'None');
       ```
     - Since the backend sent `locked: false` and `lockMode: "None"`, the popup omits the `<span class="badge-locked">🔒 Locked</span>` tag.

- **Proposed Fix**:
  1. Update `HandleGetActiveWebRules` in `CommandHandler.cs` to leverage `ProfileStateLogic.IsProfileLockedNow(profile, session, localNow)` (or `IsProfileLockedNowWithSessions`), which already recognizes that active schedules are locked:
     ```csharp
     bool isLocked = ProfileStateLogic.IsProfileLockedNow(profile, session, localNow) ||
                     (session != null && session.Locked && session.ProfileId == profile.Id) ||
                     (profile.LockMode != LockMode.None);
     ```
  2. Ensure both `ActiveProfileDto` and `ProfileSummaryDto` accurately convey the active locked state to the browser extension so the popup dynamically renders the `🔒 Locked` badge alongside the remaining countdown timer.

---

### 2. Service DACL Lockdown Can Fail Silently Without Being Retried

- **Status**: `Resolved & Verified`
- **Priority**: `Medium–High`
- **Category**: Security Hardening & Background Service Tamper Protection
- **Affected Components**:
  - `src/Blocker.Service/Worker.cs` (`CheckActiveSessionsAndSync`, lines 157–175 & 555–574)
  - `src/Blocker.Core/SecurityHardener.cs` (`IsLockedDown`, `IsServiceLockedDown`)

- **Description / Symptoms**:
  - When an active session or scheduled block enters a locked state, the background service attempts to harden both the running process and the Windows Service DACL (to prevent termination, suspension, stopping, or service deletion via `sc.exe`).
  - If process lockdown succeeds but service lockdown fails (e.g., transient `sc.exe` timeout, process creation failure, or SCM lock contention), the service falsely logs that both process and service DACLs were locked down, and never retries the service lockdown on subsequent polling cycles.
  - As a result, the session remains locked in UI, but the underlying Windows Service (`SankalpaService`) is left vulnerable to administrative stopping (`net stop`, `sc stop`, or Task Manager Services tab) or service deletion.

- **Root Cause Analysis**:
  1. In `src/Blocker.Core/SecurityHardener.cs`:
     - `IsLockedDown` (line 190) tracks only the current *process's* kernel DACL status (updated by `LockdownCurrentProcess`).
     - `IsServiceLockedDown` (line 191) independently tracks the *Windows service's* DACL status (updated by `LockdownService`).
  2. In `src/Blocker.Service/Worker.cs` (lines 157–167 and 555–565):
     ```csharp
     if (anyHardLocked && !SecurityHardener.IsLockedDown)
     {
         SecurityHardener.LockdownCurrentProcess(out var secErr);
         SecurityHardener.LockdownService(out _);
         if (secErr == null)
         {
             string secMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SECURITY] Sankalpa process & service DACLs locked down. Terminate and stop access denied.";
             Console.WriteLine(secMsg);
             _logger.LogInformation("{Message}", secMsg);
         }
     }
     ```
     - **Coupled Gate Condition**: The branch condition checks only `!SecurityHardener.IsLockedDown`. Once `LockdownCurrentProcess` succeeds, `IsLockedDown` becomes `true`.
     - **Silent Failure & Skipped Retries**: If `SecurityHardener.LockdownService(out _)` fails, its error is discarded (`out _`), `IsServiceLockedDown` remains `false`, and every subsequent monitoring cycle skips the entire block because `!SecurityHardener.IsLockedDown` is now `false`.
     - **False Positive Logging**: If `secErr` (the process error) is `null`, the logger announces that both process *and* service were locked down, even when service lockdown failed.
  3. **Unlock Asymmetry**:
     - On release (`else if (!anyHardLocked && SecurityHardener.IsLockedDown)`), unlocking also checks only `SecurityHardener.IsLockedDown`, meaning if process lockdown had failed initially, service unlock would never be invoked.

- **Proposed Fix**:
  1. Decouple process lockdown and service lockdown state checks in `Worker.cs`:
     ```csharp
     if (anyHardLocked)
     {
         if (!SecurityHardener.IsLockedDown)
         {
             if (SecurityHardener.LockdownCurrentProcess(out var procErr))
             {
                 _logger.LogInformation("[SECURITY] Process DACL locked down.");
             }
             else
             {
                 _logger.LogWarning("[SECURITY] Failed to lock down process DACL: {Error}", procErr);
             }
         }

         if (!SecurityHardener.IsServiceLockedDown)
         {
             if (SecurityHardener.LockdownService(out var svcErr))
             {
                 _logger.LogInformation("[SECURITY] Service DACL locked down.");
             }
             else
             {
                 _logger.LogWarning("[SECURITY] Failed to lock down service DACL: {Error}", svcErr);
             }
         }
     }
     else
     {
         if (SecurityHardener.IsLockedDown)
         {
             SecurityHardener.UnlockCurrentProcess(out _);
         }
         if (SecurityHardener.IsServiceLockedDown)
         {
             SecurityHardener.UnlockService(out _);
         }
     }
     ```
  2. This guarantees that any transient failure in `sc.exe sdset` will be naturally retried on the next worker cycle until `IsServiceLockedDown` is successfully established.

---

### 3. Scheduled Block Does Not Automatically Turn On at Scheduled Time if Block Was Toggled "Off"

- **Status**: `Resolved & Verified`
- **Priority**: `High`
- **Category**: Scheduling Engine & Profile Lifecycle Automation
- **Affected Components**:
  - `src/Blocker.Core/ProfileStateLogic.cs` (`IsProfileActive`, `IsProfileActiveWithSessions`)
  - `src/Blocker.Service/Worker.cs`
  - `src/Blocker.App/MainViewModel.cs` (`ToggleProfile`, `DisableProfile`)

- **Description / Symptoms**:
  - If a user toggles a block profile "OFF" (or if a profile with a schedule is created/saved while disabled), the scheduled block does not automatically turn on when the clock reaches the scheduled time window.
  - The block remains completely inactive even though the current time matches the configured schedule (e.g. 9:00 AM – 5:00 PM on weekdays).
  - Expected behavior: When a schedule time window begins, the scheduled block should automatically turn on and enforce blocking rules, regardless of whether the profile was toggled off prior to the scheduled period.

- **Root Cause Analysis**:
  1. In `src/Blocker.Core/ProfileStateLogic.cs` (`IsProfileActive`):
     ```csharp
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

     if (ScheduleLogic.IsActive(profile.Schedule, localNow))
     {
         activeReason = "Schedule";
         return true;
     }
     ```
  2. When the user flips the toggle to "OFF" in the UI, `DisableProfile` sets `profile.Enabled = false` in the database.
  3. Because `!profile.Enabled` is checked *before* the schedule check (`ScheduleLogic.IsActive`), any profile marked `Enabled = false` immediately returns `false` (`activeReason = "None"`).
  4. The scheduler engine is thus permanently bypassed for any profile that was toggled off, so it never wakes up or turns on when the scheduled time arrives.

- **Proposed Fix**:
  1. **Option A (Schedule Precedence in `ProfileStateLogic`)**:
     Evaluate active schedule rules before the general `!profile.Enabled` fallback, or treat `Schedule` definitions as an automatic activator:
     ```csharp
     // If a schedule is actively matching right now, it should activate regardless of whether manual 'Enabled' was toggled off
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
     ```
  2. **Option B (Separate Schedule Enablement from Manual Active State)**:
     Distinguish between "Schedule Automation Enabled" and manual "Block Active Right Now", so turning a profile off outside of a schedule only disables always-on/manual blocking without disabling the scheduled triggers.
