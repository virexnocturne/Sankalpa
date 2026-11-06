# Sankalpa — Future Features & Roadmap

This document tracks planned features, enhancements, and architectural goals for **Sankalpa Blocker**, clearly separated between active backlog items and shipped capabilities.

---

## Priority Guidelines

| Priority | Definition |
| :--- | :--- |
| **High** | Core focus/blocking capabilities, major quality of life, and essential commercial user experience features. |
| **Medium** | Wellness additions, secondary productivity modes, moderation tools, design audits, and schedule polish. |
| **Low** | Features intentionally deferred during active development (e.g., uninstall protection). |
| **Extra Low** | High-risk OS-level tamper features with significant commercial liabilities (AV false positives, recovery brick risks). |

---

## 🚀 Active Roadmap & Backlog

### 2. In-App Modal & Overlay Dialog System (Unified Window Experience)

- **Status**: `Planned`
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

- **Status**: `Planned`
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

### 6. Time Allowances & Daily Budgets (Moderation Engine)

- **Status**: `Planned`
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

### 8. Hierarchical Folder Organization for Notes in Meditation Mode

- **Status**: `Planned`
- **Priority**: `Medium`
- **Reason for Priority Rating**: Essential knowledge-management evolution for deep-focus practitioners. As users accumulate daily intentions, session reflections, study logs, and tasks during meditation and lockdown sessions, a flat list of cards quickly becomes unwieldy and hard to review.
- **Category**: Wellness, Focus Journaling & Notes Vault Architecture
- **Description**:
  Introduces a folder organization hierarchy to the slide-out reflection notes drawer (`NotesDrawerControl`) and Obsidian-compatible vault:
  1. **User-Defined Folder Hierarchy**: Allows users to create, rename, and organize folders (e.g., `Reflections/`, `Project Ideas/`, `Daily Intentions/`, `Tasks/`, `Insights/`) directly within the notes drawer.
  2. **Drawer Navigation & Folder Filtering**: A collapsible folder tree / sidebar or clean breadcrumb dropdown in the notes drawer header enabling 1-click filtering to view notes in a specific folder or view all notes.
  3. **Folder Assignment in Composer**: A folder selector pill in the note creation / edit flyout allowing immediate assignment upon drafting, plus context-menu actions to move existing notes between folders.
  4. **Obsidian Vault Directory Mirroring**: Notes saved to `%LOCALAPPDATA%\Sankalpa\notes\` or external Obsidian vaults are physically mirrored into corresponding subdirectories on disk (e.g., `Vault/Reflections/2026-11-06-Session.md`), preserving complete bidirectional interoperability with external Markdown tools (Obsidian, Logseq, VS Code).
  5. **Contextual Default Folders**: Option to set auto-routing defaults based on session type (e.g., auto-routing meditation reflections to a `Meditation/` folder and whitelist work notes to `Work/`).
- **Proposed Implementation Details**:
  - Update `NoteCard` in `src/Blocker.Core/NoteCard.cs` to include `public string Folder { get; set; } = "Default";` (or `FolderPath`).
  - Update `active_notes.json` persistence in `NotesDrawerControl.xaml.cs` to serialize and restore folder tags and hierarchical structure.
  - Implement a folder filter navigation pill/tree in `NotesDrawerControl.xaml` above `NotesCollection`.
  - Extend `UniversalNotesDialog` and `NoteCardCollection` export utilities to export structured directory trees alongside the single-file session bundle and master journal.

---

### 9. Full-Screen Distraction-Free Notes Mode (Zen Writer) *(Still in Thinking)*

- **Status**: `Still in Thinking / Conceptual Exploration`
- **Priority**: `Medium–Low` *(Exploratory Phase)*
- **Reason for Priority Rating**: Highly compelling distraction-free writing environment for writers, coders, and deep-thinkers who want to draft and journal without visual competition from desktop windows or timers. Currently kept in exploratory thinking mode to resolve open UX design questions regarding navigation and ambient timer integration.
- **Category**: UI/UX & Distraction-Free Focus Environment
- **Description**:
  An optional, fully immersive full-screen writing and reflection interface ("Zen Writer") accessible during Meditation Mode and system lockdown:
  1. **Full-Screen Canvas (Beyond Slide-Out Drawer)**: Seamlessly expands the notes composer from the 380px right-edge slide-out drawer into an edge-to-edge, full-monitor distraction-free writing canvas.
  2. **Zen Typography & Minimalist Layout**: Centered, readable markdown column (configurable 680px–800px width), generous line height, subtle caret pulsing, and optional typewriter scrolling (keeping the active cursor line vertically centered on screen).
  3. **Non-Intrusive Ambience & Timer Visibility**: Ambient soundscape controls and session countdown fade into low-opacity, ultra-subtle indicators in the corner or hide completely until mouse movement, keeping the user in pure flow.
  4. **Instant Split / Return Toggle**: Smooth shortcut-driven or button-driven transition (`F11` or `Ctrl+Alt+F`) to switch between the ambient Zen Shield clock view, the drawer view, and the full-screen writer view without losing cursor position or unsaved drafts.
  5. **Open Design Questions *(Still in Thinking)***:
     - *Scope vs. Simplicity*: Should full-screen mode include rich live markdown formatting and split-pane previews, or remain strictly clean typography / distraction-free monospaced text?
     - *Timer Coexistence*: Should the circular breathing halo remain as a very faint watermark background behind the text, or should visual breathing animations be disabled entirely in full-screen writing mode?
     - *Multi-Note Navigation*: How should switching between notes in a folder work while in full-screen mode without introducing visual clutter (e.g., an auto-hiding left sidebar vs. a command palette / fuzzy search `Ctrl+P`)?
- **Proposed Implementation Details**:
  - Add a full-screen toggle command in `NotesDrawerControl.xaml` and `ZenShieldWindow.xaml`.
  - Introduce a `ZenWriterView` or expand the `NoteComposerControl` to fill the entire grid of `ZenShieldWindow` when `IsFullScreenNotesMode` is active.
  - Animate opacity transitions for background breathing halo and countdown timer when entering/exiting full-screen notes mode.

---

### 11. Custom User Audio & Soundtrack Import for Ambient Sound Engine

- **Status**: `Planned`
- **Priority**: `Medium`
- **Reason for Priority Rating**: High user customization value. Different users focus best with distinct acoustic profiles (binaural beats, brown noise, lo-fi study loops, classical compositions, ambient drone, personal field recordings). Allowing users to import their own audio transforms the ambient engine into a flexible, personalized focus soundscape.
- **Category**: Wellness, Audio Engine & User Customization
- **Description**:
  Extends the offline sound engine in `AmbientSoundEngine.cs` to support importing, managing, and looping custom user-provided soundtracks:
  1. **Custom Soundtrack Import UI**: A dedicated "Custom Soundscapes" manager in Meditation Settings (`MeditationEditDialog.xaml`) and Appearance Settings with an `+ Import Audio Track` action browsing local storage (`.mp3`, `.wav`, `.ogg`, `.flac`, `.m4a`).
  2. **Local Audio Storage & Management**: Imported files are automatically copied and stored in `%LOCALAPPDATA%\Sankalpa\custom_audio\`, keeping them permanent, safe from source file deletion, and fully functional offline without network dependencies.
  3. **Soundtrack Metadata & Configuration**:
     - Custom track name and customizable emoji/icon identifier (e.g., `☕ Coffee Shop`, `🎵 Lo-Fi Beats`, `⚡ Brown Noise`, `🌊 Ocean Waves`).
     - Looping mode configuration (infinite seamless loop vs. play-through once).
     - Individual track gain / volume trim setting to equalize perceived loudness against built-in soundscapes.
  4. **Dynamic Soundscape Selector**:
     - The top-left soundscape pill (`SoundscapePillControl.xaml`) and meditation settings dialog dynamically list custom tracks alongside built-in presets (`Rain`, `Library`, `Forest`, `Singing Bowls`).
     - Smooth 1-second crossfading and fade-in/fade-out envelopes automatically apply to custom user tracks via `AmbientSoundEngine`.
  5. **Quick Folder Shortcut**: "Open Audio Folder" action allowing advanced users to drop multiple audio files directly into the custom audio directory for instant batch discovery.
- **Proposed Implementation Details**:
  - Update `AmbientSoundEngine.cs` to accept custom track paths alongside the `SoundscapeType` enum (e.g., `SetCustomSoundscape(string trackId, string filePath)`).
  - Add `CustomSoundtrack` model (ID, Name, Icon, FilePath, TrimVolume) and persist user tracks in `UiSettingsStore.cs` (`List<CustomSoundtrackDto> CustomSoundtracks`).
  - Add file import dialog (`Microsoft.Win32.OpenFileDialog`) with format filters and file size validation.
  - Update `SoundscapePillControl.xaml` and `MeditationEditDialog.xaml` to bind to an aggregated `ObservableCollection<SoundscapeItemViewModel>` rather than hardcoded radio buttons.

---

### 10. Safe Mode Enforcer (`SafeBoot` Registry Integration)

- **Status**: `Deferred / Extra Low Priority`
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

## ✅ Completed Features (Shipped)

### 1. System-Wide Device Lockout (Complete Lock & Whitelist / Kiosk Mode)

- **Status**: `Completed & Shipped (v1.3.0 as Zen Shield)`
- **Priority**: `High`
- **Category**: Core Blocking Engine & Digital Detox
- **Description**:
  Provides system-level lockdown options beyond traditional domain/process blacklists:
  1. **Complete Computer Block**: Completely locks the user out of the computer for a set duration. The screen is covered across all monitors, keyboard/mouse input is suppressed from accessing the desktop, and OS shortcuts are intercepted until the session timer expires.
  2. **Partial Lock (App Whitelist / Kiosk Mode)**: Inverts standard blocking logic—everything on the computer is blocked *except* for a few explicitly chosen/whitelisted applications (e.g., allowing only Word or VS Code). All unapproved apps, background launchers, and task switchers are blocked or suppressed.
- **Shipped Implementation**:
  - Full-screen multi-monitor `ZenShieldWindow.xaml` covering all connected screens.
  - Low-level keyboard hook (`LowLevelKeyboardHook.cs`) suppressing Start Menu, `Alt+Tab`, `Win+Tab`, and task gestures.
  - Strict whitelist enforcer and watchdog in `Worker.cs` and `ZenShieldWindow.xaml.cs`.
  - Mini-HUD floating pill when working in allowed applications.

---

### 5. Meditation Mode Core Engine & Notes Drawer

- **Status**: `Completed & Shipped (v1.3.0 & v1.3.1)`
- **Priority**: `Medium`
- **Category**: Productivity & Wellness
- **Description**:
  A dedicated mindfulness and meditation environment that leverages the device lockout engine: during Meditation Mode, everything on the computer is blocked apart from the meditation environment and specific items you choose.
- **Shipped Capabilities**:
  1. **Lockdown Integration**: Automatically suppresses distractions and non-whitelisted apps via Zen Shield kiosk.
  2. **Countdown Timer**: Adaptive `mm:ss` / `hh:mm:ss` countdown timer with pulsing breathing halo animation.
  3. **Background Sounds Engine**: Offline ambient audio player with smooth crossfade and volume envelopes in `AmbientSoundEngine.cs`.
  4. **Session Controls**: Clean pause, resume, and premature exit controls with fade-out transitions.
  5. **Hold-to-End Button**: Deliberate 3-second circular hold-press to exit without impulse cancels.
  6. **Open-Ended Mode & Hide Clock**: Unconstrained timer counting upward, and clock display toggle to eliminate time anxiety.
  7. **Notes Drawer**: Slide-out reflection notes drawer on the right edge with Markdown cards, task checkboxes, and Obsidian vault export.
  *(Note: Acoustic audio recording upgrade is tracked in FUTURE_PATCHES.md Item 5; folder organization is tracked in Item 8; custom soundtracks in Item 11; full-screen writer in Item 9).*

---

### 7. Delayed Unlock & "Cool-Down" Commitment Timer

- **Status**: `Completed & Shipped (v1.2.0 & v1.2.1)`
- **Priority**: `Medium`
- **Category**: Commitment & Friction Psychology
- **Description**:
  When a user requests to stop an active locked session or disable a strict block, enforces a mandatory waiting period before the block is released.
- **Shipped Implementation**:
  - Implemented `LockMode.DelayedUnlock = 4` with presets: 15 mins, 30 mins, 45 mins, 1 hour.
  - Pre-commitment confirmation dialog (`DelayedUnlockPromptDialog`) warning of active presence requirement.
  - Foreground Active Timer Window (`CoolDownTimerWindow`) with strict anti-bypass hooks (minimizing, deactivating, or closing cancels unlock).
  - Clear `"Changed My Mind — Keep Me Focused"` button to reward restraint and preserve streaks.
  - Backend validation via `DelayedUnlockManager` and cryptographically generated session tokens.

---

## How to Add New Features

Use the template below when appending new feature ideas to this log:

```markdown
### [Feature Name]
- **Status**: `Planned` / `Completed`
- **Priority**: `High` / `Medium` / `Low`
- **Reason for Priority Rating**: [Why is it assigned this priority?]
- **Category**: [e.g., UI/UX, Blocking Engine, Companion Extension, Security, Scheduling]
- **Description**: [Brief overview of what the feature does]
- **Proposed Implementation Details**:
  - [Technical note 1]
  - [Technical note 2]
```
