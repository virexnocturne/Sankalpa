import os
import shutil
import subprocess
import sys
import time

REPO_ROOT = r"C:\Users\virex\.gemini\antigravity\scratch\Blocker"
OUTPUT_DIR = r"C:\Users\virex\.gemini\antigravity\scratch\Sankalpa dev"
STAGING_DIR = os.path.join(REPO_ROOT, "dist", "staging")

print(f"=== Publishing Sankalpa Latest to: {OUTPUT_DIR} ===")

os.makedirs(OUTPUT_DIR, exist_ok=True)
os.makedirs(STAGING_DIR, exist_ok=True)

# 1. Stop service so it releases locks
print("Checking and stopping SankalpaService...")
subprocess.run(["sc.exe", "stop", "SankalpaService"], capture_output=True)

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

# 3. Terminate running UI / NativeHost instances before copying
for proc in ["Sankalpa", "Blocker.NativeHost", "Blocker.Service", "sankalpa-cli"]:
    subprocess.run(["taskkill.exe", "/F", "/T", "/IM", f"{proc}.exe"], capture_output=True)
time.sleep(0.5)

# 4. Copy from STAGING_DIR to OUTPUT_DIR with atomic rename fallback for locked files
print("\n--- Deploying Staged Binaries to Sankalpa dev ---")
for item in os.listdir(STAGING_DIR):
    src_file = os.path.join(STAGING_DIR, item)
    dest_file = os.path.join(OUTPUT_DIR, item)
    if os.path.isfile(src_file):
        try:
            shutil.copy2(src_file, dest_file)
        except PermissionError:
            # File is currently held open by another process: rename old file and place new one
            old_file = dest_file + f".old_{int(time.time())}"
            try:
                os.rename(dest_file, old_file)
                shutil.copy2(src_file, dest_file)
                print(f"Replaced locked file {item} (old renamed to {os.path.basename(old_file)})")
            except Exception as e:
                print(f"Warning: Could not replace {item}: {e}")

# 5. Copy Assets, Licenses, Config & Extension
print("\n--- Copying Assets, Licenses, Config & Extension ---")
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

ext_src = os.path.join(REPO_ROOT, "extension")
ext_dest = os.path.join(OUTPUT_DIR, "extension")
if os.path.exists(ext_src):
    if os.path.exists(ext_dest):
        shutil.rmtree(ext_dest)
    shutil.copytree(ext_src, ext_dest)
    print("Extension folder copied to Sankalpa dev.")

# Also copy to dist/Sankalpa-Release so installer builder includes it
rel_ext_dest = os.path.join(REPO_ROOT, "dist", "Sankalpa-Release", "extension")
if os.path.exists(ext_src):
    if os.path.exists(rel_ext_dest):
        shutil.rmtree(rel_ext_dest)
    shutil.copytree(ext_src, rel_ext_dest)
    print("Extension folder copied to dist/Sankalpa-Release.")

config_src = os.path.join(REPO_ROOT, "src", "Blocker.Service", "appsettings.json")
if os.path.exists(config_src):
    shutil.copy2(config_src, os.path.join(OUTPUT_DIR, "appsettings.json"))

launcher_cmd = os.path.join(OUTPUT_DIR, "run-sankalpa.cmd")
with open(launcher_cmd, "w", encoding="utf-8") as f:
    f.write("@echo off\r\nstart \"\" \"%~dp0Sankalpa.exe\"\r\n")

# 6. Register Native Messaging Host & AUMID
native_host_exe = os.path.join(OUTPUT_DIR, "Blocker.NativeHost.exe")
if os.path.exists(native_host_exe):
    print("\n--- Registering Native Host & AUMID from Sankalpa dev ---")
    reg_res = subprocess.run([native_host_exe, "--register"], capture_output=True, text=True)
    print(reg_res.stdout)

# 7. Update service path in dist/Sankalpa-Release and restart service
service_dist_dest = os.path.join(REPO_ROOT, "dist", "Sankalpa-Release", "Blocker.Service.exe")
if os.path.exists(os.path.dirname(service_dist_dest)):
    try:
        shutil.copy2(os.path.join(OUTPUT_DIR, "Blocker.Service.exe"), service_dist_dest)
    except Exception as e:
        print(f"Copying service to dist: {e}")

print("Starting SankalpaService...")
subprocess.run(["sc.exe", "start", "SankalpaService"], capture_output=True)

# 8. Clean up staging directory
shutil.rmtree(STAGING_DIR, ignore_errors=True)

print("\n=== Release to 'Sankalpa dev' completed successfully! ===")
