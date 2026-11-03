# Privacy Policy for Sankalpa & Sankalpa Blocker Companion

**Effective Date:** November 3, 2026  
**Last Updated:** November 3, 2026  

Sankalpa ("we", "our", or "the application") is committed to protecting your privacy. This Privacy Policy explains our practices regarding data collection, storage, and processing when using the **Sankalpa Desktop Application** and the **Sankalpa Blocker Browser Extension** (collectively, "Sankalpa").

---

## 1. Core Principle: 100% Local & Offline

Sankalpa is built from the ground up as a **privacy-first, offline-only productivity tool**. 

- **No Remote Servers**: Sankalpa does not operate external cloud servers or databases that store your information.
- **No Analytics / Telemetry**: Sankalpa contains no tracking cookies, analytics SDKs, advertising beacons, or telemetry collectors.
- **No Data Selling**: We do not sell, rent, monetize, or trade any user data to any third party under any circumstances.

---

## 2. Information Handled by the Application

### A. Sankalpa Desktop Application (Windows)
- **Block Profiles & Rules**: Custom lists of domains, application executable names, and schedule timers configured by you are stored exclusively on your local machine (`%LocalAppData%\Sankalpa\data\`).
- **Session History & Statistics**: Records of focus blocks and attempt counters remain in your local SQLite database on your device and are never transmitted elsewhere.
- **System Permissions**: Elevated permissions used by the Sankalpa Enforcer Service are used strictly to modify the local hosts file, manage local firewall redirection, and inspect local running processes to enforce blocking rules.

### B. Sankalpa Blocker Browser Companion Extension
- **Browsing Navigation**: When you navigate to a web page, the extension inspects the destination domain URL locally within your browser to determine whether it matches an active blocking profile.
- **No History Logging**: URLs that do not match active blocklists are immediately discarded. No log of your web browsing history is ever transmitted or stored outside your device.
- **Native Inter-Process Communication**: The extension communicates exclusively with the local `Blocker.NativeHost.exe` process via standard OS pipes (`nativeMessaging`) to synchronize session state. It makes zero outbound network requests.

---

## 3. Data Storage and Security

All application data, profiles, and configuration files are stored locally within the user profile on your operating system:
- Windows: `%LocalAppData%\Sankalpa\`

You have full control over this data. Uninstalling the application or clearing browser extension storage deletes all locally stored data.

---

## 4. Children's Privacy

Sankalpa does not collect personal information from any user, including children under the age of 13.

---

## 5. Changes to This Privacy Policy

Because Sankalpa does not collect or transmit user data, our privacy model is simple and static. If any changes are made to how local data is processed, this policy will be updated in the project repository and release notes.

---

## 6. Contact Information

If you have questions, feedback, or concerns regarding this Privacy Policy, please open an issue on our GitHub repository:
- **Repository**: https://github.com/virex/Sankalpa
