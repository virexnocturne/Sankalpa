# Sankalpa Blocker: Named-Pipe Permissions & Command Authorization Audit

## Executive Summary

Sankalpa Blocker exposes a local Windows Named Pipe (`\\.\pipe\BlockerControl`) for inter-process communication between client applications (Desktop UI, CLI, Browser Native Host) and the background Windows Service (`Blocker.Service`).

Prior to security hardening (Issue 1 & Issue 5):
1. The named pipe allowed connection by `Authenticated Users`.
2. The server dispatched requests to `CommandHandler.Handle` without extracting or checking the client's Windows identity.
3. High-privilege administrative actions—specifically `EMERGENCYUNLOCK`, which resets all block sessions, process/service DACL restrictions, DNS locks, and hosts file entries—could be executed by unprivileged callers without restriction.

This document formalizes the security architecture, Named-Pipe ACLs, client impersonation protocol, and command-level authorization matrix implemented in `Blocker.Core` and `Blocker.Service`.

---

## 1. Named Pipe Security Architecture & ACLs

### 1.1 Pipe Endpoint & Discretionary Access Control List (DACL)
- **Pipe Name**: `\\.\pipe\BlockerControl`
- **Creation API**: `NamedPipeServerStreamAcl.Create` using Windows `PipeSecurity`

```csharp
PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow)
PipeAccessRule(LocalSystemSid, PipeAccessRights.FullControl, AccessControlType.Allow)
PipeAccessRule(BuiltinAdministratorsSid, PipeAccessRights.FullControl, AccessControlType.Allow)
PipeAccessRule(AuthenticatedUserSid, PipeAccessRights.ReadWrite, AccessControlType.Allow)
```

- **Anonymous / Network Access**: Blocked. Anonymous callers and remote/network connections cannot connect or read/write to the pipe.
- **Local Authenticated Users**: Granted `ReadWrite` stream access to send command requests and receive JSON responses.

### 1.2 Identity Impersonation & Extraction
When a client connects to the named pipe, the service inspects the client's token using Windows named pipe impersonation:

```csharp
stream.RunAsClient(() =>
{
    using var winId = WindowsIdentity.GetCurrent();
    // Validate IsAuthenticated, SID, LocalSystem, and Administrator role via WindowsPrincipal
});
```

The server creates an `IClientIdentity` snapshot containing:
- `Name`: Account name (e.g., `DOMAIN\User`, `NT AUTHORITY\SYSTEM`)
- `Sid`: Security Identifier string
- `IsAuthenticated`: Whether the client is authenticated by Windows
- `IsAdministrator`: Whether the caller holds elevated administrative privileges (`WindowsBuiltInRole.Administrator`)
- `IsLocalSystem`: Whether the caller is running as `NT AUTHORITY\SYSTEM`

If impersonation fails or the connection is broken, the identity defaults safely to `ClientIdentity.Anonymous` (`IsAuthenticated = false`).

---

## 2. Command Authorization Matrix

All commands received over the pipe are verified by `CommandHandler.Handle(request, store, clientIdentity)`.

| Command | Permitted Principal | Privilege Classification | Potential Impact / Risk | Lock Mode Enforcement & Authorization Rule |
| :--- | :--- | :--- | :--- | :--- |
| **PING** | Anonymous, Authenticated User, Admin/System | Public | None / Health check | Always permitted without authentication. Returns `"pong"`. |
| **GETSTATUS** | Authenticated User | Standard User | Low (Information disclosure of active profiles and block status) | Requires authenticated client. Read-only operation. |
| **HOSTSSTATUS** | Authenticated User | Standard User | Low (Telemetry on hosts file blocking and line count) | Requires authenticated client. Read-only operation. |
| **LISTPROFILES** | Authenticated User | Standard User | Low (Lists configured focus profiles and schedules) | Requires authenticated client. Read-only operation. |
| **GETPROFILE** | Authenticated User | Standard User | Low (Reads specific profile configuration) | Requires authenticated client. Read-only operation. |
| **GETEVENTS** | Authenticated User | Standard User | Low (Reads blocking audit events) | Requires authenticated client. Read-only operation. |
| **GETACTIVEWEBRULES** | Authenticated User | Standard User | Low (Supplies web rules to browser extension) | Requires authenticated client. Read-only operation. |
| **GETUNLOCKCHALLENGE** | Authenticated User | Standard User | Low (Generates typing challenge phrase for profile) | Requires authenticated client. |
| **GETDNSCHALLENGE** | Authenticated User | Standard User | Low (Generates 64-digit numeric challenge for DNS) | Requires authenticated client. |
| **EXTHEARTBEAT** | Authenticated User | Standard User | Medium (Browser health tracking & fallback triggers) | Requires authenticated client. Updates extension heartbeat for fallback planner. |
| **STARTSESSION** | Authenticated User | Standard User | Medium (Activates focus lock session) | Requires authenticated client. Disallowed if another session is currently active. |
| **RENAMEPROFILE** | Authenticated User | Standard User | Low (Renames existing profile) | Requires authenticated client. Strictly rejects requests carrying non-rename changes. |
| **ADDWEBSITE** | Authenticated User | Standard User | Low/Medium (Adds site to profile block list) | Additive-only. Strictly forbids remove, exception, allow, or stop parameters. |
| **ENABLEPROFILE** | Authenticated User | Standard User | Low/Medium (Enables profile schedule/blocking) | Requires authenticated client. |
| **DISABLEPROFILE** | Authenticated User | Standard User | High (Suspends active profile blocking) | Enforces lock constraints: blocked if profile is currently locked unless typing challenge or unlock password is valid. |
| **SAVEPROFILE** | Authenticated User | Standard User | High (Modifies profile rules, schedules, lock modes) | Enforces lock constraints: if profile is locked or in active locked session, only additive edits are permitted. Changing lock mode from Password requires current password. |
| **DELETEPROFILE** | Authenticated User | Standard User | High (Deletes profile) | Disallowed while profile is enabled or in active session. Requires unlock password if profile was password-protected. |
| **STOPSESSION** | Authenticated User, Administrator / LocalSystem | Standard User / Privileged | High (Terminates running focus block session) | Standard user: strictly enforced by lock timer and challenge/password verification. Elevated Admin/System or valid emergency recovery file: overrides active session lock. |
| **CLEANHOSTS** | Authenticated User (when unlocked), Administrator / LocalSystem | Standard User / Privileged | High (Removes blocking entries from Windows hosts file) | Standard user: rejected if active session or active profile is locked. Elevated Admin/System or valid emergency recovery file: allowed to clean hosts. Consumes recovery file if present. |
| **GETDNSCONFIG** | Authenticated User | Standard User | Low (Inspects DNS provider configuration) | Requires authenticated client. Read-only operation. |
| **SAVEDNSCONFIG** | Authenticated User | Standard User | High (Changes DNS servers) | Requires authenticated client. Refused if DNS configuration is currently locked. |
| **LOCKDNS** | Authenticated User | Standard User | Medium (Locks DNS configuration) | Requires authenticated client. |
| **UNLOCKDNS** | Authenticated User | Standard User | High (Unlocks DNS configuration) | Requires 64-digit typing challenge validation or timer expiration. |
| **EMERGENCYUNLOCK** | Administrator / LocalSystem (OR Standard User with Physical Emergency Recovery File) | Privileged / Administrative | Critical (Clears all blocks, active sessions, DNS locks, process/service DACLs) | **Strictly Authorized**: Permitted for Administrator or LocalSystem. For standard users: only permitted if `store.IsEmergencyUnlockPresent()` is true (recovery file placed in protected system directory). Consumes recovery file upon execution. Otherwise rejected with `401 Unauthorized`. |

---

## 3. Emergency Unlock Security Protocol

### 3.1 Threat Model
In distraction-blocking systems, users under self-control conflicts may attempt to bypass locks using scripted pipe calls to `EMERGENCYUNLOCK`. Because `Blocker.Service` runs with elevated privileges, an unauthenticated or non-elevated `EMERGENCYUNLOCK` pipe command represented a severe privilege escalation / self-bypass vulnerability.

### 3.2 Recovery Authorization Levels
1. **Elevated Administrator or SYSTEM caller**:
   - `clientIdentity.IsAdministrator == true` OR `clientIdentity.IsLocalSystem == true`.
   - Execution is authorized immediately.
2. **Standard Non-Admin User**:
   - Must have administrative intervention via an emergency recovery file placed in the protected application directory:
     - `%AppContext.BaseDirectory%\EMERGENCY_UNLOCK`
     - `%ProgramData%\Blocker\EMERGENCY_UNLOCK`
     - `%ProgramData%\Sankalpa\EMERGENCY_UNLOCK`
   - Placing a file in these protected paths requires Windows UAC elevation (Administrator write access).
   - Once verified, the unlock operation executes and automatically **consumes (deletes)** the emergency token file to prevent replay.
3. **Unprivileged caller without emergency token**:
   - Request is rejected: `Response.Fail("Unauthorized: EMERGENCYUNLOCK requires elevated administrator privileges or an emergency recovery file.")`.
   - All locks, sessions, and DACLs remain completely intact.

---

## 4. Audit Findings & Resolution Summary

| Issue | Vulnerability | Severity | Remediation | Status |
| :--- | :--- | :--- | :--- | :--- |
| **Issue 1** | Unauthenticated callers could issue `EMERGENCYUNLOCK` to bypass all active locks and restrictions. | Critical | Enforced Windows token impersonation and elevation check; required administrator privileges or physical recovery file in protected system directory. | **Resolved** |
| **Issue 5** | Named pipe command dispatcher executed commands without verifying caller authentication or privilege classification. | High | Integrated `IClientIdentity` contract; rejected all non-PING commands for unauthenticated/anonymous connections; enforced privilege checks on sensitive operations (`EMERGENCYUNLOCK`, `CLEANHOSTS`, `STOPSESSION`). | **Resolved** |
