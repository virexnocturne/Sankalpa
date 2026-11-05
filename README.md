# Sankalpa (संकल्प)

<p align="center">
  <img src="src/Blocker.App/Assets/logo.png" alt="Sankalpa Logo" width="128" height="128" />
</p>

<p align="center">
  <strong>Tamper-resistant focus, website, and application blocker for Windows.</strong><br />
  Reclaim your digital focus with unbreakable focus sessions, automatic schedule lockouts, delayed unlocks, and deep anti-tamper protections.
</p>

<p align="center">
  <a href="https://github.com/virexnocturne/Sankalpa/releases/latest"><img src="https://img.shields.io/github/v/release/virexnocturne/Sankalpa?color=047857&label=Release&logo=windows" alt="Latest Release: v1.2.0" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-blue?logo=windows" alt="Platform: Windows" />
  <img src="https://img.shields.io/badge/Framework-.NET%208.0%20WPF-purple?logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Privacy-100%25%20Offline%20%26%20Local-success?logo=shield" alt="Privacy First" />
  <a href="licenses/"><img src="https://img.shields.io/badge/License-OFL%20%2F%20ISC-orange" alt="Licenses" /></a>
</p>

---

<p align="center">
  <img src="docs/screenshots/dashboard-black.png" alt="Sankalpa Dashboard - Obsidian Black Theme" width="850" />
</p>

---

## 🌟 What's New in v1.2.0

- 🔒 **Automatic Schedule Lockdown**: Block profiles with active schedules are now automatically locked during their scheduled time windows (`Schedule 🔒`). The toggle switch is disabled so you cannot simply turn off blocks mid-session.
- ⏳ **Delayed Unlock**: Added a tamper-resistant cooldown timer option (15m, 30m, 1h, 2h). Must wait out an active timer in front of the screen before a locked block can be modified.
- 🛑 **Strict Uninstall Guard**: Anti-tamper protection integrated into Windows Add-Remove Programs (`unins000.exe`). If any block session or scheduled block is active, uninstallation is strictly forbidden and aborted immediately.
- 🎨 **Streamlined Desktop UI**: Removed misleading Stop buttons on locked blocks and eliminated header text clipping in the schedule editor for a cleaner experience.
- 🌐 **Updated Companion Extension (v1.2.0)**: Fully synchronized with latest engine rules, real-time native messaging, and seamless multi-browser support.

---

## Table of Contents
- [Download & Quick Start](#-download--quick-start)
- [How It Works (Architecture)](#-how-it-works-architecture)
- [Comprehensive Features](#-comprehensive-features)
  - [1. Multi-Target Blocklists (Websites & Apps)](#1-multi-target-blocklists-websites--apps)
  - [2. Focus Schedules & Daily Allowances](#2-focus-schedules--daily-allowances)
  - [3. Unbreakable Lock Protections](#3-unbreakable-lock-protections)
  - [4. Distraction-Free Themes](#4-distraction-free-themes)
  - [5. Companion Browser Extension](#5-companion-browser-extension)
- [Security Architecture & Anti-Tamper Hardening](#-security-architecture--anti-tamper-hardening)
- [Privacy Guarantee](#-privacy-guarantee)
- [Command Line Interface (CLI)](#-command-line-interface-cli)
- [Testing & Verification](#-testing--verification)
- [Building from Source](#-building-from-source)
- [Licenses](#-licenses)

---

## 🚀 Download & Quick Start

### 1. Download Latest Release (v1.2.0)
Download the installer or standalone release directly from [GitHub Releases](https://github.com/virexnocturne/Sankalpa/releases/latest):

| File | Type | Size | SHA-256 Checksum |
| :--- | :--- | :--- | :--- |
| **[`Sankalpa-Setup-1.2.0.exe`](https://github.com/virexnocturne/Sankalpa/releases/download/v1.2.0/Sankalpa-Setup-1.2.0.exe)** | **Recommended Installer** | 115 MB | `FB400EBA47A1424E21E14DAA13D50EBABAFD93627452298159567AC4DCE867BF` |
| **[`Sankalpa.exe`](https://github.com/virexnocturne/Sankalpa/releases/download/v1.2.0/Sankalpa.exe)** | Portable Standalone | 158 MB | `955EE0739B845A99BB2A186F3C9D71FFBDB61D7919F595A034BF5BECC9AD6348` |
| **[`sankalpa-extension-v1.2.0.zip`](https://github.com/virexnocturne/Sankalpa/releases/download/v1.2.0/sankalpa-extension-v1.2.0.zip)** | Companion Extension | 39 KB | `E0E27ABF25C9E6D74C63A67180CFD83DFCB68A4340A54620C82D97518862EA3E` |

### 2. Run the Setup Wizard
1. Run **`Sankalpa-Setup-1.2.0.exe`**.
2. Click **Yes** when Windows User Account Control (UAC) prompts (Administrator rights are needed **once** during installation to register the tamper-resistant background service).
3. The setup automatically installs the desktop application, background enforcement service, native messaging bridge, and desktop shortcuts.

> **Note on Windows SmartScreen**: Windows Defender SmartScreen may display *"Windows protected your PC"* on fresh releases. Click **More info** → **Run anyway**.

### 3. Install Companion Extension
For instantaneous website blocking and incognito protection:
1. Open your browser's extension manager:
   - Chrome / Brave / Opera: `chrome://extensions`
   - Microsoft Edge: `edge://extensions`
   - Mozilla Firefox: `about:debugging`
2. Enable **Developer mode** toggle.
3. Unzip **`sankalpa-extension-v1.2.0.zip`** and click **Load unpacked** (or click **"Open folder"** from the desktop app's status bar).
4. In extension details, enable **"Allow in Incognito / InPrivate"**.

---

## ⚙️ How It Works (Architecture)

Sankalpa is built with a **dual-layer protection model** that cleanly isolates user configuration from system-level enforcement:

```
┌─────────────────────────────────────────────────────────────────┐
│                    User Experience Layer                        │
│                                                                 │
│   ┌───────────────────────────┐     ┌───────────────────────┐   │
│   │   Sankalpa Desktop UI     │     │   Browser Companion   │   │
│   │     (Sankalpa.exe)        │     │  (Chrome/Edge/Firefox)│   │
│   └─────────────┬─────────────┘     └───────────┬───────────┘   │
└─────────────────┼───────────────────────────────┼───────────────┘
                  │                               │ Standard I/O
                  │ Named Pipe IPC                │ NativeMessaging
┌─────────────────┼───────────────────────────────┼───────────────┐
│                 │                               │               │
│                 ▼                               ▼               │
│        ┌─────────────────────────────────────────────────┐      │
│        │        Native Messaging Bridge                  │      │
│        │        (Blocker.NativeHost.exe)                 │      │
│        └────────────────────────┬────────────────────────┘      │
│                                 │ Named Pipe IPC                │
│                                 ▼                               │
│        ┌─────────────────────────────────────────────────┐      │
│        │       Sankalpa Enforcer Service                 │      │
│        │         (Blocker.Service.exe)                   │      │
│        │       * Runs as LocalSystem                     │      │
│        │       * Auto-Restart Crash Watchdog             │      │
│        └────┬─────────────────────────────┬──────────────┘      │
│             │                             │                     │
│             ▼                             ▼                     │
│    System Hosts File              Process Termination           │
│   & Local DNS Redirection          & App Blacklisting           │
│                                                                 │
│                     Enforcement Core Layer                      │
└─────────────────────────────────────────────────────────────────┘
```

1. **Desktop Client (`Sankalpa.exe`)**: Modern WPF application with responsive design, instant search, inline profile renaming, and dark/light themes.
2. **Background Enforcer Service (`Blocker.Service.exe`)**: Runs continuously under the `NT AUTHORITY\SYSTEM` account. Maintains timers across reboots, kills unauthorized apps, and applies system DNS/hosts redirection.
3. **Native Messaging Host (`Blocker.NativeHost.exe`)**: Dispatches real-time extension heartbeats and Windows Shell Action Center toast alerts even when the UI is closed.
4. **Auto-Restart Watchdog**: Windows Service Control Manager automatically restarts the service with 0ms delay if terminated (`sc failure restart/0/restart/0`).

---

## 🎯 Comprehensive Features

### 1. Multi-Target Blocklists (Websites & Apps)
Create customized, context-specific block profiles (e.g. *Deep Work*, *Study Mode*, *Social Media Detoxing*):

- **Websites Tab**: Block specific domains (e.g., `youtube.com`, `reddit.com`), entire categories, or enable **Block All Websites** with custom whitelist exceptions.
- **Applications Tab**: Scan running processes and installed Windows desktop applications. Distraction apps (Steam, Discord, Telegram, games) are immediately terminated if launched during active sessions.

<p align="center">
  <img src="docs/screenshots/block-editor-websites.png" alt="Website Blocklist" width="48%" />
  <img src="docs/screenshots/block-editor-apps.png" alt="Application Blocklist" width="48%" />
</p>

---

### 2. Focus Schedules & Daily Allowances
- **Automated Recurring Schedules**: Configure multi-day time windows (e.g., Mon–Fri 08:00–17:00). When the scheduled time arrives, enforcement activates and locks automatically.
- **Daily Usage Allowance**: Grant yourself a limited amount of daily recreation time (e.g. 30 mins) before the profile enters complete lockdown.
- **Pomodoro & Focus Timers**: Start on-demand focus intervals with custom durations and optional short/long break rewards.

<p align="center">
  <img src="docs/screenshots/block-editor-schedule.png" alt="Schedule Configuration" width="48%" />
  <img src="docs/screenshots/start-session.png" alt="Focus Session Start" width="48%" />
</p>

---

### 3. Unbreakable Lock Protections
To prevent giving in to temptation, Sankalpa provides multiple unyielding lock modes:

| Lock Mode | Behavior |
| :--- | :--- |
| **Complete / Timed Lock** | Completely locks the block. Cannot be stopped, edited, or turned off until the timer expires. |
| **Schedule Lock (`Schedule 🔒`)** | Automatically locks the block for the entire duration of configured schedule windows. Toggle switch is disabled. |
| **Delayed Unlock (`Delay ⏳`)** | Requires waiting out an active 15m, 30m, 1h, or 2h on-screen countdown timer to prevent impulsive unlocks. |
| **Random Typing Challenge** | Requires correctly typing a complex, randomized 80-character text with zero typos. |
| **Password Protection** | Requires entering a secure emergency password set when configuring the profile. |

<p align="center">
  <img src="docs/screenshots/editor-locked.png" alt="Schedule Lock" width="48%" />
  <img src="docs/screenshots/delayed-unlock-prompt-dark.png" alt="Delayed Unlock" width="48%" />
</p>

---

### 4. Distraction-Free Themes
Sankalpa includes four tailored themes designed for clarity and eye comfort:
- **Obsidian Black**: Deep OLED black for zero glare during night sessions.
- **Dark**: Balanced slate dark mode.
- **Grey**: Softer neutral contrast.
- **Light**: High-contrast daylight mode.
All themes support 6 vibrant accent palettes (Emerald, Indigo, Violet, Amber, Rose, Slate).

<p align="center">
  <img src="docs/screenshots/main-window.png" alt="Dark Theme" width="48%" />
  <img src="docs/screenshots/main-window-light.png" alt="Light Theme" width="48%" />
</p>

---

### 5. Companion Browser Extension
- Compatible with **Chrome**, **Microsoft Edge**, **Brave**, **Firefox**, **Vivaldi**, and **Opera**.
- Zero-latency page redirection to an inspiring focus hold screen.
- Real-time countdown timer directly in the browser toolbar.
- Incognito/InPrivate tamper detection with autonomous browser termination fallback.

<p align="center">
  <img src="docs/screenshots/extension-dialog.png" alt="Extension Dialog" width="48%" />
  <img src="docs/screenshots/blocked-screen.png" alt="Focus Blocked Screen" width="48%" />
</p>

---

## 🛡️ Security Architecture & Anti-Tamper Hardening

1. **Strict Uninstall Protection (`UninstallGuard`)**:
   - `sankalpa-cli check-uninstall` inspects both the live service and offline SQLite database/system hosts file.
   - If an active block session, schedule, or locked profile is active, Windows Settings / Add-Remove Programs (`unins000.exe`) and standalone uninstallers strictly refuse uninstallation.
2. **Autonomous Browser Termination**:
   - Browsers opened in Incognito without extension permission or unextended secondary browsers are detected, granted a 60-second grace countdown with Windows toast alerts, and automatically terminated if compliance is not met.
3. **Named Pipe Client Authentication**:
   - Default-deny IPC server (`\\.\pipe\BlockerControl`) verifies caller tokens via Windows Impersonation (`stream.RunAsClient`).
   - Administrative commands and emergency unlocks require Administrator or `LocalSystem` credentials.
4. **Monotonic Timers & Reboot Immunity**:
   - Session timers and schedule states are stored in an encrypted/system-protected local SQLite database. Changing system clock or rebooting will not bypass active sessions.

---

## 🔒 Privacy Guarantee

- **100% Offline & Local**: Zero network telemetry, zero analytical trackers, zero third-party calls.
- **No Accounts Required**: No email, cloud login, or subscriptions.
- **Your Data Remains on Your Machine**: All profiles, rules, and logs reside locally in `%LocalAppData%\Sankalpa\data\`.
- Complete [Privacy Policy](packaging/browser-extension/PRIVACY_POLICY.md).

---

## 💻 Command Line Interface (CLI)

Sankalpa includes `sankalpa-cli.exe` in the application directory for scripting and advanced users:

```powershell
# Check current protection status and active blocks
sankalpa-cli status

# List all configured profiles and lock modes
sankalpa-cli list

# Start a profile manually for 45 minutes
sankalpa-cli start --profile "Deep Work" --duration 45m

# Stop an active session (if unlocked)
sankalpa-cli stop --profile "Deep Work"

# Verify whether uninstallation is safe or blocked by active rules
sankalpa-cli check-uninstall
```

---

## 🧪 Testing & Verification

Sankalpa contains a comprehensive automated test suite with **over 660 unit, integration, and security tests**:

```powershell
# Run the complete test suite
dotnet test Blocker.sln

# Run isolated security simulation scenarios
.\tests\Simulation\setup.ps1
.\tests\Simulation\run-tests.ps1
.\tests\Simulation\cleanup.ps1
```

---

## 🛠️ Building from Source

### Prerequisites
- **Windows 10 / 11 (64-bit)**
- **.NET 8.0 SDK** ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Inno Setup 6+** (`winget install JRSoftware.InnoSetup`)

### Steps
1. Clone the repository:
   ```powershell
   git clone https://github.com/virexnocturne/Sankalpa.git
   cd Sankalpa
   ```
2. Build the solution:
   ```powershell
   dotnet build Blocker.sln -c Release
   ```
3. Compile the Inno Setup installer:
   ```powershell
   powershell -ExecutionPolicy Bypass -File packaging\windows-installer\build-installer.ps1 -Version "1.2.0"
   ```
   The installer executable will be generated at `packaging\dist\Sankalpa-Setup-1.2.0.exe`.

---

## 📜 Licenses
- Fonts: **Familjen Grotesk** & **IBM Plex** (SIL Open Font License 1.1)
- Icons: **Lucide Icons** (ISC License)
- See details in the [`licenses/`](licenses/) directory.
