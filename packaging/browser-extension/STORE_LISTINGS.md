# Sankalpa Companion Extension — Store Listings & Metadata

Use this document when submitting **Sankalpa Blocker** to the Chrome Web Store, Microsoft Edge Add-ons, and Mozilla Firefox Add-ons.

---

## 1. General Listing Information

### Extension Name
`Sankalpa Blocker`

### Summary / Short Description (Max 132 characters)
`Companion extension for Sankalpa. Enforces website blocks, focus sessions, and tamper-resistant productivity limits.`

---

## 2. Detailed Description (For Web Store Descriptions)

```markdown
Sankalpa Blocker is the official companion extension for the Sankalpa desktop productivity suite.

Transform your focus and reclaim control of your digital time. Sankalpa blocks distracting websites during active focus sessions, supports scheduled blocking, and coordinates with the Sankalpa desktop app and background service to provide seamless, tamper-resistant digital detox.

Key Features:
• Seamless Integration: Connects automatically with the Sankalpa desktop app to sync active blocking profiles and schedules.
• Instant Block Enforcement: Blocklists are evaluated locally with zero latency, redirecting blocked destinations to an elegant, distraction-free hold screen.
• Strict Lock Protection: Honors active Pomodoro timers, random text challenges, and strict password locks initiated from the desktop app.
• Privacy-First Architecture: 100% offline and on-device. No browsing history, queries, or personal data ever leaves your computer.
• Quick Dashboard Access: View active blocking sessions and remaining focus countdowns directly from your browser toolbar.

Note: This extension requires the Sankalpa desktop application for Windows installed on your computer. Download the desktop app at https://github.com/virex/Sankalpa.
```

---

## 3. Chrome Web Store: Permission Justifications

Google Chrome Web Store requires explicit justification for every requested permission. Copy and paste these exact explanations into the Developer Dashboard during submission:

| Permission | Justification |
| :--- | :--- |
| `nativeMessaging` | Communicates with the local Sankalpa desktop background service (`Blocker.NativeHost.exe`) via standard I/O pipes to synchronize active block profiles, session timers, and lock states without accessing any external network. |
| `webNavigation` | Intercepts navigation events before distracting web pages load, allowing the extension to redirect blocked URLs immediately to the local focus hold screen. |
| `tabs` | Reads the URL of active tabs to determine whether the visited site matches the user's active block profile rules. |
| `storage` | Stores cached profile identifiers, active session state, and UI preferences locally in the browser so enforcement persists across browser restarts. |
| `alarms` | Schedules a periodic local heartbeat every few seconds to verify connection health with the local desktop enforcer service. |
| `host_permissions` (`<all_urls>`) | Required to inspect URL hostnames across any website that the user chooses to add to their custom blocklists. No user data is sent across the network. |

### Single-Purpose Policy Statement
> "Sankalpa Blocker has a single clear purpose: to intercept and block distracting websites in accordance with user-configured focus sessions and schedules set in the local Sankalpa desktop application."

---

## 4. Categories & Classification

- **Category**: `Productivity`
- **Secondary Category**: `Workflow & Planning`
- **Pricing**: `Free`
- **Target Audience**: Students, professionals, and anyone seeking deep focus and digital distraction elimination.

---

## 5. Submission Checklist by Browser

### Google Chrome Web Store
1. Log in at [Chrome Web Store Developer Dashboard](https://chrome.google.com/webstore/devconsole).
2. Pay the one-time \$5 Google Developer registration fee (if not already paid).
3. Click **Add new item** and upload `packaging/dist/sankalpa-extension-v1.0.0.zip`.
4. Fill in the **Store Listing** fields from Section 1 & 2 above.
5. Upload at least one screenshot (1280x800 or 640x400) and the 128x128 icon (`icon128.png`).
6. In the **Privacy** tab:
   - Paste the URL to your hosted `PRIVACY_POLICY.md` (e.g., GitHub raw or project site).
   - Paste the permission justifications from Section 3 above.
   - Check "I do not sell or transfer user data to third parties".
7. Click **Submit for Review** (approval typically takes 24–72 hours).

### Microsoft Edge Add-ons
1. Log in at [Microsoft Partner Center](https://partner.microsoft.com/dashboard/microsoftedge).
2. Click **Create new extension**.
3. Choose either:
   - **Import from Chrome Web Store** (fastest, imports once Chrome listing is approved), or
   - Upload `packaging/dist/sankalpa-extension-v1.0.0.zip` directly.
4. Review is typically completed within 1–2 business days.

### Mozilla Firefox Add-ons (AMO)
1. Log in at [Mozilla Developer Hub](https://addons.mozilla.org/developers/).
2. Click **Submit a New Add-on**.
3. Upload `packaging/dist/sankalpa-extension-v1.0.0.zip`.
4. Automated validation checks manifest V3 settings.
5. Review is typically completed within a few hours.
