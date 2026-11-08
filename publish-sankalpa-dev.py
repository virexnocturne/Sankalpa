import os
import shutil
import subprocess
import sys
import time

REPO_ROOT = r"C:\Users\virex\.gemini\antigravity\scratch\Blocker"
DEV_DIR = r"C:\Users\virex\.gemini\antigravity\scratch\Sankalpa dev"
DEV_LOCAL_DIR = os.path.join(REPO_ROOT, "dist", "Sankalpa dev")
RELEASE_DIR = os.path.join(REPO_ROOT, "dist", "Sankalpa-Release")
STAGING_DIR = os.path.join(REPO_ROOT, "dist", "staging")

CANDIDATE_DIRS = [DEV_DIR, DEV_LOCAL_DIR, RELEASE_DIR]
TARGET_DIRS = []
for d in CANDIDATE_DIRS:
    os.makedirs(d, exist_ok=True)
    if not any(os.path.exists(t) and os.path.samefile(d, t) for t in TARGET_DIRS):
        TARGET_DIRS.append(d)

print(f"=== Publishing Sankalpa to Dev & Release Targets ===")
print(f"Targets: {[os.path.abspath(d) for d in TARGET_DIRS]}")

for d in TARGET_DIRS:
    os.makedirs(d, exist_ok=True)
os.makedirs(STAGING_DIR, exist_ok=True)

# 1. Stop service so it releases locks cleanly without triggering emergency wipe
print("\nChecking and stopping SankalpaService...")
subprocess.run(["sc.exe", "stop", "SankalpaService"], capture_output=True)
for _ in range(10):
    res = subprocess.run(["sc.exe", "query", "SankalpaService"], capture_output=True, text=True)
    if "STOPPED" in res.stdout:
        break
    time.sleep(0.5)

def kill_all_processes():
    for proc in ["Sankalpa", "Blocker.NativeHost", "Blocker.Service", "sankalpa-cli"]:
        subprocess.run(["taskkill.exe", "/F", "/T", "/IM", f"{proc}.exe"], capture_output=True)
    ps_cmd = "Get-Process | Where-Object { $_.ProcessName -like 'Blocker.NativeHost*' -or $_.ProcessName -like 'Sankalpa*' -or $_.ProcessName -like 'Blocker.Service*' -or $_.ProcessName -like 'sankalpa-cli*' } | Stop-Process -Force"
    subprocess.run(["powershell", "-NoProfile", "-Command", ps_cmd], capture_output=True)

# Terminate running UI / NativeHost instances before copying
kill_all_processes()
time.sleep(0.5)

# 2. Publish all projects into STAGING_DIR first
projects = [
    ("Blocker.NativeHost", os.path.join(REPO_ROOT, "src", "Blocker.NativeHost", "Blocker.NativeHost.csproj")),
    ("Blocker.Service", os.path.join(REPO_ROOT, "src", "Blocker.Service", "Blocker.Service.csproj")),
    ("Blocker.Cli", os.path.join(REPO_ROOT, "src", "Blocker.Cli", "Blocker.Cli.csproj")),
    ("Blocker.App", os.path.join(REPO_ROOT, "src", "Blocker.App", "Blocker.App.csproj")),
]

for name, proj_path in projects:
    print(f"\n--- Publishing {name} ---")
    cmd = [
        "dotnet", "publish", proj_path,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "/p:PublishSingleFile=true",
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", STAGING_DIR
    ]
    res = subprocess.run(cmd, text=True, capture_output=True)
    if res.returncode != 0:
        print(f"ERROR publishing {name}:\n{res.stderr}\n{res.stdout}")
        sys.exit(1)
    else:
        print(f"SUCCESS: {name} published.")

# 3. Ensure processes are stopped again if dotnet publish restarted anything
kill_all_processes()
time.sleep(0.5)

# 4. Deploy staged binaries to both targets
for target_dir in TARGET_DIRS:
    print(f"\n--- Deploying Staged Binaries to: {target_dir} ---")
    for item in os.listdir(STAGING_DIR):
        src_file = os.path.join(STAGING_DIR, item)
        dest_file = os.path.join(target_dir, item)
        if os.path.isfile(src_file):
            try:
                shutil.copy2(src_file, dest_file)
            except PermissionError:
                old_file = dest_file + f".old_{int(time.time())}"
                try:
                    os.rename(dest_file, old_file)
                    shutil.copy2(src_file, dest_file)
                    print(f"Replaced locked file {item} (old renamed to {os.path.basename(old_file)})")
                except Exception as e:
                    print(f"Warning: Could not replace {item}: {e}")

    # Copy Assets
    assets_src = os.path.join(REPO_ROOT, "src", "Blocker.App", "Assets")
    assets_dest = os.path.join(target_dir, "Assets")
    if os.path.exists(assets_src):
        if os.path.exists(assets_dest):
            shutil.rmtree(assets_dest)
        shutil.copytree(assets_src, assets_dest)
        print(f"Assets copied to {os.path.basename(target_dir)}.")

    # Copy Licenses
    licenses_src = os.path.join(REPO_ROOT, "licenses")
    licenses_dest = os.path.join(target_dir, "licenses")
    if os.path.exists(licenses_src):
        if os.path.exists(licenses_dest):
            shutil.rmtree(licenses_dest)
        shutil.copytree(licenses_src, licenses_dest)
        print(f"Licenses copied to {os.path.basename(target_dir)}.")

    # Copy Extension
    ext_src = os.path.join(REPO_ROOT, "extension")
    ext_dest = os.path.join(target_dir, "extension")
    if os.path.exists(ext_src):
        if os.path.exists(ext_dest):
            shutil.rmtree(ext_dest)
        shutil.copytree(ext_src, ext_dest)
        print(f"Extension copied to {os.path.basename(target_dir)}.")

    # Copy Appsettings
    config_src = os.path.join(REPO_ROOT, "src", "Blocker.Service", "appsettings.json")
    if os.path.exists(config_src):
        shutil.copy2(config_src, os.path.join(target_dir, "appsettings.json"))

    # Copy Launchers
    launcher_cmd = os.path.join(target_dir, "run-sankalpa.cmd")
    with open(launcher_cmd, "w", encoding="utf-8") as f:
        f.write("@echo off\r\nstart \"\" \"%~dp0Sankalpa.exe\"\r\n")

    uninstaller_cmd = os.path.join(REPO_ROOT, "packaging", "clean-uninstall.cmd")
    if os.path.exists(uninstaller_cmd):
        shutil.copy2(uninstaller_cmd, os.path.join(target_dir, "clean-uninstall.cmd"))

# 5. Register Native Messaging Host & AUMID from Sankalpa dev
native_host_exe = os.path.join(DEV_DIR, "Blocker.NativeHost.exe")
if os.path.exists(native_host_exe):
    print("\n--- Registering Native Host & AUMID from Sankalpa dev ---")
    reg_res = subprocess.run([native_host_exe, "--register"], capture_output=True, text=True)
    print(reg_res.stdout)

# 6. Restart SankalpaService
print("\nStarting SankalpaService...")
subprocess.run(["sc.exe", "start", "SankalpaService"], capture_output=True)
time.sleep(1.5)

# 7. Clean up staging directory and leftover .old_ files
shutil.rmtree(STAGING_DIR, ignore_errors=True)
for d in TARGET_DIRS:
    if os.path.exists(d):
        for item in os.listdir(d):
            if ".old_" in item:
                try:
                    os.remove(os.path.join(d, item))
                except Exception:
                    pass

print("\n=== Deployment to 'Sankalpa dev' and 'Sankalpa-Release' completed successfully! ===")
