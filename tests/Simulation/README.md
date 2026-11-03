# Sankalpa Security Simulation Environment

This directory provides an isolated, deterministic, and safe simulation environment designed to verify the security fixes and operational resilience of **Sankalpa** without modifying the host operating system.

---

## 1. Purpose & Strategy

A dedicated virtual machine is not required to achieve high-assurance verification. Instead, this test harness follows an **in-process and dependency-isolated simulation strategy**:

- **Fake Client Identity Provider**: Exercises named-pipe caller authorization (anonymous, standard non-admin users, local administrators, and `LocalSystem`) without creating low-privilege Windows user accounts.
- **Service Security Descriptor Verifier & Process Runner Double**: Simulates `sc.exe` process execution, timeouts, non-zero return codes, and security descriptor (SDDL) queries without mutating the real Windows Service Control Manager.
- **Controllable Heartbeat & Clock Simulation**: Fast-forwards logical time deterministically to verify the browser extension's 10-minute fail-open timeout without waiting 600 wall-clock seconds.
- **Isolated Mutex Acquisition Double**: Simulates concurrent mutex contention, abandoned mutex states, and OS access-denied exceptions without system-wide namespace conflicts.

### Safety Boundaries

The simulation guarantees:
1. **No modification to Windows `hosts`** (`%SystemRoot%\System32\drivers\etc\hosts`).
2. **No modification to Windows DNS settings or network adapter configurations**.
3. **No modification to Windows service permissions or service state** (`SankalpaService`).
4. **No execution of real privileged DACL lockdown on the host machine**.
5. **No modification to real browser profiles or active extension storage**.
6. **No Administrator privileges required** to run the complete test suite.

---

## 2. Directory Structure

```text
tests/
  Sankalpa.Tests/                  # Baseline core engine regression tests
  Sankalpa.Security.Tests/         # Security fixes and authorization unit tests
  Simulation/
    README.md                      # This documentation
    setup.ps1                      # Environment preparation and prerequisites check
    run-tests.ps1                  # Unified test executor and summary reporter
    cleanup.ps1                    # Disposable artifacts cleanup script
    fixtures/
      temp/                        # Disposable sandbox scratch directory
    mocks/                         # Simulation doubles and fake providers
    scenarios/                     # End-to-end scenario definitions and Node.js tests
```

---

## 3. How to Use

### Step 1: Setup the Simulation Environment
Run `setup.ps1` to verify prerequisites (.NET SDK, Node.js), create disposable fixture folders, and restore dependencies:
```powershell
.\tests\Simulation\setup.ps1
```

### Step 2: Run All Tests and Scenarios
Execute `run-tests.ps1` to run the security suite, baseline tests, and extension scenarios:
```powershell
.\tests\Simulation\run-tests.ps1
```

### Step 3: Cleanup
Run `cleanup.ps1` to cleanly remove all temporary files:
```powershell
.\tests\Simulation\cleanup.ps1
```

---

## 4. The 12 Simulation Scenarios

| # | Scenario | Test Implementation | Key Verification |
|---|---|---|---|
| **1** | **Unauthorized Local Client Emergency Unlock** | `PipeAuthorizationTests` / `SimulationScenariosTests` | Non-admin caller receives authorization rejection; lock and profiles remain active. |
| **2** | **Authorized Client Emergency Unlock** | `EmergencyUnlockTests` / `SimulationScenariosTests` | Elevated Administrator or LocalSystem caller successfully unlocks; or standard user with valid emergency recovery file succeeds. |
| **3** | **Service Unavailable While Cached Heartbeat Sent** | `NativeHostTests` / `heartbeat-replay.test.mjs` | Native messaging host returns payload flagged with `"cached": true` and `"serviceConnected": false`. |
| **4** | **Browser Extension Receiving Stale Data Repeatedly** | `heartbeat-replay.test.mjs` | Receiving repeated payloads with `"cached": true` does NOT refresh `lastResponseTime`. |
| **5** | **Heartbeat Timeout Expiring (Fail-Open)** | `heartbeat-replay.test.mjs` | When `lastResponseTime` exceeds 10 minutes (`FAIL_OPEN_MS`), `evaluateCurrentRules` fails open (`blocked: false, reason: 'fail_open_stale'`). |
| **6** | **Service Reconnecting** | `heartbeat-replay.test.mjs` | When service reconnects with `"cached": false`, `lastResponseTime` resets to current time and enforcement resumes. |
| **7** | **Two Service Instances Mutex Contention** | `SingletonMutexTests` / `SimulationScenariosTests` | Second instance fails to acquire mutex, logs error, and exits cleanly with code 1. |
| **8** | **Abandoned Mutex Acquisition** | `SingletonMutexTests` / `SimulationScenariosTests` | Waiting thread catches `AbandonedMutexException`, successfully acquires ownership, logs warning, and proceeds safely. |
| **9** | **Failed Service Lockdown Command** | `SecurityHardenerTests` / `SimulationScenariosTests` | Non-zero exit code from `sc.exe` causes lockdown to return `false` with error details; `IsServiceLockedDown` remains unchanged. |
| **10** | **Successful Service Lockdown Command** | `SecurityHardenerTests` / `SimulationScenariosTests` | Zero exit code and verified SDDL descriptor sets `IsServiceLockedDown = true`. |
| **11** | **Process Timeout During Lockdown** | `SecurityHardenerTests` / `SimulationScenariosTests` | Process exceeding timeout is killed; operation returns `false` with timeout message; state unchanged. |
| **12** | **Failed Security Descriptor Verification** | `SecurityHardenerTests` / `SimulationScenariosTests` | Even if `sc.exe` returns exit code 0, if SDDL query does not verify the lockdown descriptor, lockdown fails and state remains unchanged. |
