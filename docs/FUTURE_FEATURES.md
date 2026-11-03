# Sankalpa — Future Features & Backlog

This document tracks planned features, enhancements, and long-term architectural goals for **Sankalpa Blocker**.

---

## Priority Guidelines

| Priority | Definition |
| :--- | :--- |
| **High** | Core focus/blocking capabilities, major quality of life, and essential commercial user experience features. |
| **Medium** | Wellness additions, secondary productivity modes, moderation tools, design audits, and schedule polish. |
| **Low** | Features intentionally deferred during active development (e.g., uninstall protection). |
| **Extra Low** | High-risk OS-level tamper features with significant commercial liabilities (AV false positives, recovery brick risks). |

---

## Backlog

### 1. System-Wide Device Lockout (Complete Lock & Whitelist / Kiosk Mode)

- **Priority**: `High`
- **Reason for Priority Rating**: Critical deep-focus capability (equivalent to Cold Turkey's "Frozen Turkey"). Enables unavoidable digital detox by locking down the PC entirely or restricting usage exclusively to work-essential apps.
- **Category**: Core Blocking Engine & Digital Detox
- **Description**:
  Provides system-level lockdown options beyond traditional domain/process blacklists:
  1. **Complete Computer Block**: Completely locks the user out of the computer for a set duration. The screen is covered across all monitors, keyboard/mouse input is suppressed from accessing the desktop, and OS shortcuts are intercepted until the session timer expires.
  2. **Partial Lock (App Whitelist / Kiosk Mode)**: Inverts standard blocking logic—everything on the computer is blocked *except* for a few explicitly chosen/whitelisted applications (e.g., allowing only Word or VS Code). All unapproved apps, background launchers, and task switchers are blocked or suppressed.
- **Proposed Implementation Details**:
  - **Full Lock Screen Overlay**: Topmost borderless WPF window covering all connected monitors (`System.Windows.Forms.Screen.AllScreens`) with low-level keyboard hook (`SetWindowsHookEx` / `WH_KEYBOARD_LL`) intercepting `Alt+Tab`, `Win key`, `Ctrl+Esc`, and `Alt+F4`.
  - **Strict Whitelist Engine**: A dedicated mode in `Worker.cs` that checks active processes against an allowed list, terminating or minimizing any non-whitelisted executable.
  - **Fail-Safes**: Monotonic timer tracking across reboots, emergency challenge support, and graceful recovery when duration elapses.

---

### 2. Sankalpa Browser Extension Enhancements

- **Priority**: `High`
- **Reason for Priority Rating**: **Quality of life and complete user experience.** Users should not need to switch back and forth to the desktop app for daily block management tasks; the extension should offer complete, seamless control directly within the browser.
- **Category**: Companion Extension & UI/UX
- **Description**:
  Expand the Chrome/Edge companion extension from an active-only status widget into a comprehensive in-browser control center:
  1. **Rename Blocks from Extension**: Allow users to rename active and inactive block profiles directly from the extension popup interface.
  2. **Manage Inactive Blocks**: Display inactive block profiles alongside active blocks in the extension, giving users the ability to inspect them and add new websites directly to inactive blocks before starting a session.
  3. **In-Browser Block Configuration**: Quick-toggle rules, domain additions, and block settings without having to open the main desktop GUI.
- **Proposed Implementation Details**:
  - Add inline editing or modal dialogs in `popup.js` / `popup.html` for renaming blocks via the `RENAMEPROFILE` pipe command.
  - Expand extension state storage and heartbeat payloads to synchronize full profile rule lists and metadata for inactive profiles.
  - Handle asynchronous responses and user feedback toasts within the extension popup.

---

### 3. Comprehensive Statistics, Screentime & Distraction Insights

- **Priority**: `High`
- **Reason for Priority Rating**: **Complete user experience.** Awareness and visibility into habits are essential for long-term productivity; having clear, interactive feedback loops transforms the app from a simple enforcer into a habit-improving assistant.
- **Category**: Analytics, Extension & UI Dashboard
- **Description**:
  A complete statistics, tracking, and insights dashboard integrated into both the desktop UI and browser companion extension:
  1. **Blocked Attempt Counter**: Track and visualize how many times the user attempted to launch blocked applications or navigate to blocked URLs (identifying impulse triggers and habit patterns).
  2. **Per-Website Active Time Tracking (Extension)**: Measure active time spent on specific websites using the browser extension (only tracking foreground, active tabs when the user is engaged, omitting idle time).
  3. **Privacy-First & Optimized Architecture**: All tracking telemetry remains 100% local (stored in local SQLite / extension local storage), with zero cloud reporting, low memory footprint, and batched persistence.
  4. **Device Screentime Tracking *(Exploratory / Unfixed Concept)***: Measure total device active screen time across apps and windows to provide a high-level picture of daily PC usage. *(Scope is kept exploratory and unfixed; specific OS metrics and APIs to be determined).*
  5. **Actionable Behavioral Insights**: Surface meaningful trends from collected metrics (e.g., peak distraction hours, most attempted blocked targets, daily focus ratios, day-over-day improvement).
  6. **Interactive Visual UI**: Present stats through interactive charts, category breakdowns, daily/weekly filters, and clickable habit timelines rather than static text tables.
- **Proposed Implementation Details**:
  - **Extension Heartbeat Telemetry**: Extend extension background script to track focused tab time (`chrome.tabs.onActivated`, `idle.onStateChanged`), batching domain-level elapsed seconds into local SQLite via native host heartbeats.
  - **Service Event Aggregation**: Expand `BlockEvent` storage in `blocker.db` to record timestamps and source identifiers for attempted app/web blocks.
  - **Interactive Charts in WPF**: Embed a lightweight charting component or interactive vector SVG/Canvas dashboard in `Sankalpa.exe` with date-range filters (Today, This Week, Past 30 Days).
  - **Summary Cards in Extension**: Display quick summary metrics (e.g., "5 blocks avoided today", "Top site: 42m") directly inside the extension popup header.

---

### 4. Block Card Quick-Actions & Navigation Ergonomics

- **Priority**: `High`
- **Reason for Priority Rating**: Direct interaction efficiency and daily usability. Eliminates unnecessary friction and extra clicks by routing users straight to the specific configuration window they want to edit.
- **Category**: UI/UX & Interaction Design
- **Description**:
  Enhance the block profile cards on the main dashboard with instant-access touchpoints:
  1. **Split-Target Count Badges with Spacing**:
     - Provide visual spacing/separation between `0 apps` and `0 sites` on the card instead of combining them into a single non-clickable text string.
     - Clicking on `[X apps]` immediately opens the `BlockEditorDialog` routed directly to the **Applications** tab.
     - Clicking on `[Y sites]` immediately opens the `BlockEditorDialog` routed directly to the **Websites** tab.
  2. **Double-Click Quick Rename**:
     - Double-clicking the block title directly on its dashboard card switches the label into an inline editable `TextBox` (or opens a focused rename prompt).
     - Pressing `Enter` or clicking outside commits the rename via `RENAMEPROFILE`, while `Escape` cancels without changes.
- **Proposed Implementation Details**:
  - In `MainWindow.xaml`, split the profile summary text into two distinct clickable pills/links with a margin spacer (e.g., `Margin="0,0,8,0"`).
  - Expand `BlockEditorDialog(profile, initialTab: 0|1|2|3)` routing so clicking the apps badge passes the Apps tab index directly.
  - Add `MouseDoubleClick` handler or `IsEditing` boolean toggle to `ProfileViewModel` / `MainWindow.xaml` to bind title switching smoothly without disrupting lock states.

---

### 5. Meditation Mode

- **Priority**: `Medium`
- **Reason for Priority Rating**: High-value wellness and mental clarity feature, but built upon the primary System-Wide Lockout and process-blocking infrastructure.
- **Category**: Productivity & Wellness
- **Description**:
  A dedicated mindfulness and meditation environment that leverages the device lockout engine: **during Meditation Mode, everything on the computer is blocked apart from the meditation environment and specific items you choose** (e.g., an ambient audio stream or a reflection/journaling app).
- **Included Features**:
  1. **Lockdown Integration**: Automatically suppresses distractions and non-whitelisted apps for the duration of the meditation session.
  2. **Countdown Timer**: Configurable session durations (e.g., 5, 10, 20, 60 minutes) with gentle interval chimes and completion bells.
  3. **Background Sounds**: Ambient audio generation or loops (rain, flowing river, forest, white noise, Tibetan singing bowl, binaural focus tones).
  4. **Session Controls**: Clean pause, resume, and premature exit controls with soothing fade-out transitions.
  5. **Hold-to-End Button**: Prevents impulsive or accidental exits by requiring a deliberate hold/long-press (e.g., hold for 3 seconds with a circular fill animation) before ending the session.
  6. **Open-Ended Mode**: Option for unconstrained sessions without a fixed countdown (counts upward silently until manually ended).
  7. **Hide-the-Clock Option**: Allows users to hide the remaining time and clock display entirely to eliminate time anxiety and encourage presence.
- **Proposed Implementation Details**:
  - Connect to the Partial Lock / Whitelist engine to silence notifications and block disruptive apps during meditation.
  - Implement an ambient audio player using `NAudio` or `MediaPlayer` with smooth looping and fade-in/fade-out volume envelopes.
  - Build a minimalist, distraction-free WPF view with gentle breathing/pulsing visual cues and a custom long-press `HoldButton`.

---

### 6. Time Allowances & Daily Budgets (Moderation Engine)

- **Priority**: `Medium`
- **Reason for Priority Rating**: Promotes sustainable digital moderation and curbs binge-purge cycles by allowing intentional, capped consumption rather than forcing strictly binary bans.
- **Category**: Moderation & Habit Control
- **Description**:
  Permits users to define daily time quotas for specific apps or websites (e.g., "Allow up to 30 minutes total of YouTube/Reddit per day"). Once the allocated minutes are used up, the block engages automatically until the daily reset.
- **Proposed Implementation Details**:
  - Connect and activate the domain logic already established in `AllowanceAndRewardLogic.cs`.
  - Aggregate active domain usage from the companion extension and foreground application runtimes from `Worker.cs`.
  - Persist daily consumption in SQLite (`data/blocker.db`) with automatic local midnight resets.
  - Surface real-time remaining allowance badges in the extension popup and display toast notifications at 5-minute and 1-minute warnings.

---

### 7. Delayed Unlock & "Cool-Down" Commitment Timer

- **Priority**: `Medium`
- **Reason for Priority Rating**: Addresses psychological craving spikes: digital impulses typically peak and fade within 10–15 minutes. A forced delay prevents impulsive, emotion-driven unlocks while providing a legitimate exit for genuine emergencies.
- **Category**: Commitment & Friction Psychology
- **Description**:
  When a user requests to stop an active locked session or disable a strict block, provide a "Cool-Down Unlock" mechanism that enforces a mandatory waiting period (e.g., 10 minutes, 30 minutes, or 1 hour) before the block is released.
- **Proposed Implementation Details**:
  - Add `UnlockRequestedAtUtc` and `CooldownDurationSeconds` to `BlockSession` tracking in the database.
  - The background service continues enforcing all process and DNS blocks normally during the cool-down period.
  - Allow the user to cancel their unlock request at any point during the countdown to immediately resume their focus streak.
  - Present an active cool-down timer in the desktop UI and browser extension with an encouraging "Return to Focus" button.

---

### 8. Complete UI Analysis & Consistency Overhaul

- **Priority**: `Medium`
- **Reason for Priority Rating**: Features are still actively being added and the UI is not yet bloated. Detailed design overhaul can follow once the feature surface area stabilizes.
- **Category**: UI/UX & Design System
- **Description**:
  A comprehensive review and audit of the desktop GUI (`Sankalpa.exe`) and companion extension interfaces to ensure visual consistency, intuitive navigation, and effortless usability across all dialogs and workflows.
  *(Open-ended item: specific design details, flow diagrams, and ergonomics requirements will be expanded later as core features settle).*
- **Proposed Implementation Details**:
  - Audit and harmonize visual tokens (typography, color palettes, card spacing, iconography) between WPF views and the browser extension.
  - Streamline multi-step user workflows (creating a block, configuring complex schedules, locking, and recovery).
  - Ensure uniform keyboard accessibility, tab indices, and high-DPI scaling across varying display resolutions.

---

### 9. Uninstall Protection (Anti-Removal Guard)

- **Priority**: `Low`
- **Reason for Priority Rating**: **Sankalpa is still in active development.** Introducing strict uninstallation locking or tamper-proof removal guards at this stage increases the risk of locking developers or testers out of their own machines during rapid development, debugging, and continuous build deployments.
- **Category**: Security & Tamper Resistance
- **Description**:
  Prevent users from bypassing active locked sessions by uninstalling the application, stopping/deleting the Windows service, or running emergency unlock scripts.
- **Proposed Implementation Details**:
  1. **Emergency Unlock Safeguard**:
     - Require a master password or complex typing challenge before `sankalpa-cli emergency-unlock` or `CommandHandler.HandleEmergencyUnlock` can execute.
     - Optionally reject all emergency unlock attempts entirely if an active locked session is running.
  2. **Uninstaller Guard (`uninstall-service.bat` / Installer)**:
     - Check service state and database for active locked sessions before permitting service removal.
     - Block uninstallation when a block session with strict locking is currently active.
  3. **Windows Installer Integration**:
     - Package Sankalpa with an official installer (e.g., Inno Setup or WiX MSI) that registers in Windows *Add or Remove Programs*.
     - Hook uninstallation checks to require confirmation, password authentication, or session expiry before uninstall routines can proceed.
  4. **File-System ACL Hardening**:
     - Apply NTFS DACLs to the installation folder and SQLite database directory to prevent manual file deletion while the service is operational.

---

### 10. Safe Mode Enforcer (`SafeBoot` Registry Integration)

- **Priority**: `Extra Low` *(Commercial Liability Caution)*
- **Reason for Priority Rating**: **High commercial risk.**
  1. **Antivirus & SmartScreen False Positives:** Injecting into `HKLM\...\SafeBoot` is actively flagged by Windows Defender and enterprise EDRs as `Trojan:Win32/Persistence` or PUA/malware behavior.
  2. **Unrecoverable PC Liability:** Safe Mode is Microsoft's recovery environment. If a future update introduces a bug or crash on boot, running in Safe Mode prevents customers from fixing or uninstalling the app, risking bricked machines and severe chargeback/reputation damage.
  3. **Microsoft Store Violation:** Modifying core OS recovery keys fails Windows App Certification Kit (WACK) guidelines.
  4. **Target Audience:** ~98% of commercial paying customers never reboot into Safe Mode.
  - *Note:* If ever implemented commercially, it must strictly be an **optional, advanced opt-in setting** disabled by default with an explicit customer warning dialog, and deferred until an EV Code Signing Certificate is established.
- **Category**: Advanced Hardening & Bypass Defense
- **Description**:
  Registers `SankalpaService` to run in Windows Safe Mode (`SafeBoot\Minimal` and `SafeBoot\Network`) to close the Safe Mode reboot loophole for extreme users.
- **Proposed Implementation Details**:
  - Add optional advanced toggle in Settings (default: OFF).
  - Register under `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\SankalpaService` and `...\SafeBoot\Network\SankalpaService` (Value: `"Service"`).
  - Provide an offline recovery USB / WinPE script for emergencies.

---

## How to Add New Features

Use the template below when appending new feature ideas to this log:

```markdown
### [Feature Name]
- **Priority**: `High` / `Medium` / `Low`
- **Reason for Priority Rating**: [Why is it assigned this priority?]
- **Category**: [e.g., UI/UX, Blocking Engine, Companion Extension, Security, Scheduling]
- **Description**: [Brief overview of what the feature does]
- **Proposed Implementation Details**:
  - [Technical note 1]
  - [Technical note 2]
```
