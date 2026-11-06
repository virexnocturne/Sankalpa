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

### 2. In-App Modal & Overlay Dialog System (Unified Window Experience)

- **Priority**: `High`
- **Reason for Priority Rating**: Major quality-of-life and visual polish requirement. Eliminates disjointed multi-window behavior where dialogs (such as the Block Editor) open as separate floating OS windows with native title bars (`—`, `□`, `✕`), replacing them with a seamless, modern single-window experience standard in contemporary desktop applications.
- **Category**: UI/UX & Desktop Architecture
- **Description**:
  Replace standalone operating system dialog windows with an integrated in-app modal overlay system hosted inside `MainWindow`:
  1. **Integrated Modal Presentation**: Heavy editors and dialogs (Block Editor, Appearance settings, App Picker) render as styled cards centered inside `MainWindow` instead of spawning separate Windows OS windows.
  2. **Backdrop & Focus Trapping**: Dims/blurs the background view while a dialog is active, visually anchoring the user and keeping keyboard/tab navigation trapped within the active dialog.
  3. **Elimination of Secondary Window Controls**: Removes OS-level minimize, maximize, and title bar drag areas from dialog screens, preventing child dialogs from being detached or moved outside the app boundaries.
  4. **Responsive Sizing & Scrollability**: Ensures embedded dialogs adaptively fit within `MainWindow` dimensions with internal scrolling, preventing controls or action buttons from being cut off at smaller window sizes.
- **Proposed Implementation Details**:
  - Convert dialog windows (beginning with `BlockEditorDialog.xaml`) from `<Window>` to `<UserControl>`.
  - Add a dedicated `<Grid x:Name="ModalHost">` overlay container in `MainWindow.xaml` featuring a semi-transparent scrim (`#80000000`) and centered modal presenter.
  - Transition dialog invocation from blocking `ShowDialog()` calls to an asynchronous modal manager (e.g., `ShowModalAsync<TResult>(UserControl content)`) or ViewModel-driven state bindings.
  - Implement nested modal management or hierarchical modal stack support for child dialogs (e.g., launching `AppPickerDialog` from within the Block Editor).

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

- **Status**: `Completed`
- **Priority**: `Medium`
- **Reason for Priority Rating**: Addresses psychological craving spikes: digital impulses typically peak and fade within 10–15 minutes. A forced delay prevents impulsive, emotion-driven unlocks while providing a legitimate exit for genuine emergencies.
- **Category**: Commitment & Friction Psychology
- **Description**:
  When a user requests to stop an active locked session or disable a strict block, provide a "Cool-Down Unlock" mechanism that enforces a mandatory waiting period before the block is released.
- **Implementation Details**:
  - Implemented `LockMode.DelayedUnlock = 4` with presets: 15 mins, 30 mins, 45 mins, 1 hour (no custom inputs).
  - Pre-commitment confirmation dialog (`DelayedUnlockPromptDialog`) warning the user of the active presence requirement.
  - Dedicated foreground Active Timer Window (`CoolDownTimerWindow`) with strict anti-bypass event hooks:
    - Minimizing window cancels unlock.
    - Overlaying or focusing another app (`Deactivated`) cancels unlock.
    - Closing window cancels unlock.
  - Clear `"Changed My Mind — Keep Me Focused"` button to reward restraint and preserve streaks.
  - Backend validation via `DelayedUnlockManager` and cryptographically generated session tokens.



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
