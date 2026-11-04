import os
import shutil
import subprocess
import sys

REPO_ROOT = r"C:\Users\virex\.gemini\antigravity\scratch\Blocker"
OUTPUT_DIR = r"C:\Users\virex\.gemini\antigravity\scratch\Sankalpa dev"

print(f"=== Publishing Sankalpa Latest to: {OUTPUT_DIR} ===")

# 1. Ensure output directory exists
os.makedirs(OUTPUT_DIR, exist_ok=True)

# 2. Stop existing service if running so binaries can be replaced
print("Checking and stopping SankalpaService if running...")
subprocess.run(["sc.exe", "stop", "SankalpaService"], capture_output=True)

# Kill any existing Sankalpa or Blocker processes
for proc in ["Sankalpa", "Blocker.Service", "Blocker.NativeHost", "sankalpa-cli"]:
    subprocess.run(["taskkill.exe", "/F", "/T", "/IM", f"{proc}.exe"], capture_output=True)

# 3. Publish all projects as self-contained single-file win-x64 Release binaries
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
        "-o", OUTPUT_DIR
    ]
    res = subprocess.run(cmd, text=True, capture_output=True)
    if res.returncode != 0:
        print(f"ERROR publishing {name}:\n{res.stderr}\n{res.stdout}")
        sys.exit(1)
    else:
        print(f"SUCCESS: {name} published.")

# 4. Copy Assets and Licenses
print("\n--- Copying Assets, Licenses & Config ---")
assets_src = os.path.join(REPO_ROOT, "src", "Blocker.App", "Assets")
assets_dest = os.path.join(OUTPUT_DIR, "Assets")
if os.path.exists(assets_src):
    if os.path.exists(assets_dest):
        shutil.rmtree(assets_dest)
    shutil.copytree(assets_src, assets_dest)
    print("Assets copied.")

licenses_src = os.path.join(REPO_ROOT, "licenses")
licenses_dest = os.path.join(OUTPUT_DIR, "licenses")
if os.path.exists(licenses_src):
    if os.path.exists(licenses_dest):
        shutil.rmtree(licenses_dest)
    shutil.copytree(licenses_src, licenses_dest)
    print("Licenses copied.")

config_src = os.path.join(REPO_ROOT, "src", "Blocker.Service", "appsettings.json")
if os.path.exists(config_src):
    shutil.copy2(config_src, os.path.join(OUTPUT_DIR, "appsettings.json"))

# Copy run-sankalpa launcher
launcher_cmd = os.path.join(OUTPUT_DIR, "run-sankalpa.cmd")
with open(launcher_cmd, "w", encoding="utf-8") as f:
    f.write("@echo off\r\nstart \"\" \"%~dp0Sankalpa.exe\"\r\n")
print(f"Created launcher: {launcher_cmd}")

# 5. Register native messaging host from the new Sankalpa dev location
native_host_exe = os.path.join(OUTPUT_DIR, "Blocker.NativeHost.exe")
if os.path.exists(native_host_exe):
    print("\n--- Registering Native Host & AUMID from Sankalpa dev ---")
    reg_res = subprocess.run([native_host_exe, "--register"], capture_output=True, text=True)
    print(reg_res.stdout)

# 6. Update Windows Service configuration to point to Sankalpa dev and start it
print("\n--- Updating SankalpaService binPath to Sankalpa dev ---")
service_exe = os.path.join(OUTPUT_DIR, "Blocker.Service.exe")
if os.path.exists(service_exe):
    config_cmd = ["sc.exe", "config", "SankalpaService", f'binPath= "{service_exe}"']
    subprocess.run(config_cmd, capture_output=True)
    print(f"Service binPath set to: {service_exe}")
    start_res = subprocess.run(["sc.exe", "start", "SankalpaService"], capture_output=True, text=True)
    print("Service start result:", start_res.stdout.strip() or start_res.stderr.strip())

# 7. Delete every other older versions
print("\n--- Cleaning up older versions ---")
older_dirs = [
    os.path.join(REPO_ROOT, "dist", "app"),
    os.path.join(REPO_ROOT, "dist", "cli"),
    os.path.join(REPO_ROOT, "dist", "service"),
    os.path.join(REPO_ROOT, "dist", "Sankalpa-Release"),
]

for d in older_dirs:
    if os.path.exists(d):
        try:
            shutil.rmtree(d)
            print(f"Deleted older version directory: {d}")
        except Exception as ex:
            print(f"Could not delete {d}: {ex}")

# Also ensure dist has a junction or clean mirror to Sankalpa dev if requested
dist_dir = os.path.join(REPO_ROOT, "dist")
os.makedirs(dist_dir, exist_ok=True)
dist_sankalpa_dev = os.path.join(dist_dir, "Sankalpa dev")
if os.path.exists(dist_sankalpa_dev):
    try:
        if os.path.islink(dist_sankalpa_dev) or os.path.isdir(dist_sankalpa_dev):
            subprocess.run(["cmd.exe", "/c", f'rmdir "{dist_sankalpa_dev}"'], capture_output=True)
    except:
        pass
subprocess.run(["cmd.exe", "/c", f'mklink /J "{dist_sankalpa_dev}" "{OUTPUT_DIR}"'], capture_output=True)

print("\n=== Release to 'Sankalpa dev' completed successfully! ===")
