"""
NFS 2015 DualSense Controller Fix Tool
Automated, reversible, minimal patcher for Need for Speed (2015) controller issues on Windows.
"""

import os
import sys
import shutil
import datetime
import re
import ctypes
from ctypes import wintypes
from pathlib import Path

VERSION = "1.0.0"

# Win32 Shell call to resolve KnownFolder Documents even if redirected by OneDrive
def get_documents_path() -> Path:
    CSIDL_PERSONAL = 5  # My Documents
    SHGFP_TYPE_CURRENT = 0
    buf = ctypes.create_unicode_buffer(wintypes.MAX_PATH)
    shell32 = ctypes.windll.shell32
    if shell32.SHGetFolderPathW(None, CSIDL_PERSONAL, None, SHGFP_TYPE_CURRENT, buf) == 0:
        return Path(buf.value)
    
    # Fallback to standard environment variables
    userprofile = os.environ.get("USERPROFILE", "")
    onedrive = os.environ.get("OneDrive", "")
    if onedrive and (Path(onedrive) / "Documents").exists():
        return Path(onedrive) / "Documents"
    if onedrive and (Path(onedrive) / "Документы").exists():
        return Path(onedrive) / "Документы"
    if userprofile:
        p = Path(userprofile) / "Documents"
        if p.exists():
            return p
    return Path.home() / "Documents"


def find_profile_path(override_path: str = None) -> Path:
    if override_path:
        p = Path(override_path)
        if p.is_file():
            return p
        raise FileNotFoundError(f"Specified profile path does not exist: {override_path}")

    doc_dir = get_documents_path()
    candidates = [
        doc_dir / "Need For Speed" / "settings" / "PROFILEOPTIONS_profile",
        doc_dir / "Need for Speed" / "settings" / "PROFILEOPTIONS_profile",
        Path(os.environ.get("USERPROFILE", "")) / "OneDrive" / "Документы" / "Need For Speed" / "settings" / "PROFILEOPTIONS_profile",
        Path(os.environ.get("USERPROFILE", "")) / "OneDrive" / "Documents" / "Need For Speed" / "settings" / "PROFILEOPTIONS_profile",
        Path(os.environ.get("USERPROFILE", "")) / "Documents" / "Need For Speed" / "settings" / "PROFILEOPTIONS_profile",
    ]

    for c in candidates:
        if c.is_file():
            return c.resolve()

    raise FileNotFoundError("Could not locate PROFILEOPTIONS_profile automatically. Please specify with --path.")


class ProfilePatcher:
    def __init__(self, profile_path: Path):
        self.path = profile_path
        self.settings_dir = profile_path.parent
        self.original_backup = self.settings_dir / "PROFILEOPTIONS_profile.backup_original"

    def read_lines(self):
        with open(self.path, "r", encoding="utf-8", errors="replace") as f:
            return [line.rstrip("\r\n") for line in f]

    def write_lines(self, lines):
        with open(self.path, "w", encoding="utf-8", newline="\n") as f:
            for line in lines:
                f.write(line + "\n")

    def analyze(self):
        lines = self.read_lines()
        blocks = {}
        wheel_custom = None
        
        # Block regex: GstKeyBinding.<section>.<concept>.<slot>.<property> <value>
        pattern = re.compile(r"^(GstKeyBinding\.[^.]+\.[^.]+\.(\d+))\.(axis|button|negate|type)\s+(.*)$")
        
        for idx, line in enumerate(lines):
            line_s = line.strip()
            if line_s.startswith("GstInput.CustomControlsWheel"):
                parts = line_s.split()
                if len(parts) >= 2:
                    wheel_custom = parts[1]
            m = pattern.match(line_s)
            if m:
                prefix = m.group(1)
                prop = m.group(3)
                val = m.group(4)
                if prefix not in blocks:
                    blocks[prefix] = {"line_indices": {}, "props": {}}
                blocks[prefix]["props"][prop] = val
                blocks[prefix]["line_indices"][prop] = idx

        # Analyze problematic bindings
        problematic = []
        is_fixed = True
        total_type3 = 0
        fixed_type3 = 0

        for prefix, data in blocks.items():
            props = data["props"]
            b_type = props.get("type")
            axis = props.get("axis")
            button = props.get("button")
            negate = props.get("negate")

            if b_type == "3":
                total_type3 += 1
                # In a sanitized profile, axis must be 24 (or 0 with button 255) and button must be 255
                has_active_axis = (axis != "24" and axis is not None)
                has_active_button = (button != "255" and button is not None)

                if has_active_axis or has_active_button:
                    is_fixed = False
                    problematic.append({
                        "slot": prefix,
                        "axis": axis,
                        "button": button,
                        "negate": negate,
                        "reason": self._get_problem_description(prefix, axis, button)
                    })
                else:
                    fixed_type3 += 1

        if wheel_custom != "1":
            is_fixed = False

        return {
            "total_lines": len(lines),
            "custom_controls_wheel": wheel_custom,
            "total_type3": total_type3,
            "fixed_type3": fixed_type3,
            "problematic": problematic,
            "is_fixed": is_fixed
        }

    def _get_problem_description(self, prefix: str, axis: str, button: str) -> str:
        desc = []
        if "ConceptYaw.2" in prefix and axis == "4":
            desc.append("DirectInput Axis 4 (L2 Trigger) bound to Steering Left -> causes violent turn when braking!")
        elif "ConceptYaw.5" in prefix and axis == "0":
            desc.append("DirectInput Axis 0 (Left Stick X) bound to Steering Right -> conflicts with XInput stick!")
        elif "ConceptBrake.2" in prefix and button == "25":
            desc.append("Steering wheel brake bound to button 25 -> can trigger ghost braking!")
        elif has_axis := (axis is not None and axis != "24"):
            desc.append(f"Active wheel axis {axis}")
        elif has_btn := (button is not None and button != "255"):
            desc.append(f"Active wheel button {button}")
        return "; ".join(desc) if desc else "Active wheel binding"

    def backup(self) -> Path:
        timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        ts_backup = self.settings_dir / f"PROFILEOPTIONS_profile.backup_{timestamp}"
        
        # Always ensure original backup exists
        if not self.original_backup.exists():
            shutil.copy2(self.path, self.original_backup)
            print(f"[+] Created permanent original backup: {self.original_backup.name}")

        shutil.copy2(self.path, ts_backup)
        print(f"[+] Created safety backup: {ts_backup.name}")
        return ts_backup

    def install(self) -> bool:
        analysis = self.analyze()
        if analysis["is_fixed"]:
            print("[OK] Profile fix is ALREADY installed and active!")
            print(f"     GstInput.CustomControlsWheel = {analysis['custom_controls_wheel']}")
            print(f"     All {analysis['total_type3']} Steering Wheel (Type 3) bindings are properly neutralized.")
            return True

        print(f"[*] Found {len(analysis['problematic'])} conflicting Type 3 (Wheel) bindings.")
        print(f"[*] GstInput.CustomControlsWheel is currently: {analysis['custom_controls_wheel']}")

        self.backup()

        lines = self.read_lines()
        new_lines = []
        diffs = []

        pattern = re.compile(r"^(GstKeyBinding\.[^.]+\.[^.]+\.(\d+))\.(axis|button|negate|type)\s+(.*)$")
        
        # First pass: map line index to prefix
        line_to_prefix = {}
        prefix_types = {}
        for idx, line in enumerate(lines):
            m = pattern.match(line.strip())
            if m:
                pref = m.group(1)
                prop = m.group(3)
                val = m.group(4)
                line_to_prefix[idx] = (pref, prop)
                if prop == "type":
                    prefix_types[pref] = val

        # Second pass: generate patched lines
        for idx, line in enumerate(lines):
            line_s = line.strip()
            if line_s.startswith("GstInput.CustomControlsWheel"):
                parts = line_s.split()
                if len(parts) >= 2 and parts[1] != "1":
                    new_line = "GstInput.CustomControlsWheel 1"
                    diffs.append((line, new_line))
                    new_lines.append(new_line)
                    continue

            if idx in line_to_prefix:
                pref, prop = line_to_prefix[idx]
                p_type = prefix_types.get(pref)
                if p_type == "3":
                    # Neutralize type 3 slots
                    if prop == "axis" and not line_s.endswith(" 24"):
                        new_line = f"{pref}.axis 24"
                        diffs.append((line, new_line))
                        new_lines.append(new_line)
                        continue
                    elif prop == "button" and not line_s.endswith(" 255"):
                        new_line = f"{pref}.button 255"
                        diffs.append((line, new_line))
                        new_lines.append(new_line)
                        continue
                    elif prop == "negate" and not line_s.endswith(" 0"):
                        new_line = f"{pref}.negate 0"
                        diffs.append((line, new_line))
                        new_lines.append(new_line)
                        continue

            new_lines.append(line)

        # Write patched file
        self.write_lines(new_lines)

        print(f"\n[+] Successfully patched {len(diffs)} lines in {self.path.name}:")
        for old, new in diffs[:15]:
            print(f"    - {old}")
            print(f"    + {new}")
        if len(diffs) > 15:
            print(f"    ... and {len(diffs) - 15} more lines.")

        # Post-verification
        post = self.analyze()
        if post["is_fixed"]:
            print("\n[SUCCESS] Verification passed: all Wheel conflicts removed! Gamepad XInput will now work smoothly.")
            return True
        else:
            print("\n[WARNING] Some bindings might still be active. Re-run verify.")
            return False

    def restore(self) -> bool:
        # Search for backups
        backups = sorted(self.settings_dir.glob("PROFILEOPTIONS_profile.backup_*"), reverse=True)
        if not backups:
            print("[-] No backups found in settings folder!")
            return False

        # Pick the original backup if available, or latest timestamped backup
        target_backup = self.original_backup if self.original_backup.exists() else backups[0]
        print(f"[*] Restoring from: {target_backup.name}")
        shutil.copy2(target_backup, self.path)
        print("[+] Restore completed successfully!")
        
        post = self.analyze()
        print(f"    Current state: {'Fixed' if post['is_fixed'] else 'Original / Unpatched'}")
        print(f"    Active Type 3 bindings: {len(post['problematic'])}")
        return True


def check_system_diagnostics():
    print("\n" + "=" * 60)
    print(" SYSTEM & CONTROLLER DIAGNOSTICS")
    print("=" * 60)

    # 1. Check Connected Devices via Windows PowerShell
    import subprocess
    cmd = 'Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match "dualsense|wireless controller|xbox|vigem|hidhide" } | Select-Object Class, FriendlyName, InstanceId | Format-Table -AutoSize'
    try:
        res = subprocess.run(["powershell", "-NoProfile", "-Command", cmd], capture_output=True, text=True, timeout=5)
        print(res.stdout.strip() if res.stdout else "No matching PnP controllers found.")
    except Exception as e:
        print(f"PnP query error: {e}")

    # 2. Check XInput slots
    print("\n--- XInput Status ---")
    try:
        xinput = ctypes.windll.xinput1_4
    except Exception:
        try:
            xinput = ctypes.windll.xinput1_3
        except Exception:
            xinput = None

    if xinput:
        state_buf = (ctypes.c_byte * 16)()
        for slot in range(4):
            ret = xinput.XInputGetState(slot, state_buf)
            status = "CONNECTED (Virtual or Physical Xbox 360)" if ret == 0 else f"Disconnected (code {ret})"
            print(f"Slot {slot}: {status}")
    else:
        print("XInput DLL not found.")

    # 3. Check Steam & EA App processes
    print("\n--- Active Middleware / Platform Processes ---")
    chk_cmd = 'Get-Process -Name "*steam*", "*ds4*", "*dualsense*", "*hidhide*", "*ea*", "*nfs*" -ErrorAction SilentlyContinue | Select-Object Id, ProcessName | Format-Table -AutoSize'
    try:
        res2 = subprocess.run(["powershell", "-NoProfile", "-Command", chk_cmd], capture_output=True, text=True, timeout=5)
        print(res2.stdout.strip() if res2.stdout else "No relevant platform processes found.")
    except Exception as e:
        print(f"Process query error: {e}")
    print("=" * 60 + "\n")


def main():
    import argparse
    parser = argparse.ArgumentParser(
        description="Need for Speed (2015) Controller & DualSense Auto-Fix Utility",
        epilog="Examples:\n  nfs2015-controller-fix.exe verify\n  nfs2015-controller-fix.exe install\n  nfs2015-controller-fix.exe restore"
    )
    parser.add_argument("action", choices=["verify", "install", "restore", "diag"], help="Action to perform")
    parser.add_argument("--path", dest="profile_path", default=None, help="Custom path to PROFILEOPTIONS_profile")
    args = parser.parse_args()

    try:
        profile_path = find_profile_path(args.profile_path)
    except FileNotFoundError as e:
        print(f"[-] Error: {e}")
        sys.exit(1)

    print(f"[*] Target Profile: {profile_path}")
    patcher = ProfilePatcher(profile_path)

    if args.action == "verify":
        analysis = patcher.analyze()
        print(f"[*] Total lines in profile: {analysis['total_lines']}")
        print(f"[*] CustomControlsWheel:    {analysis['custom_controls_wheel']}")
        print(f"[*] Type 3 (Wheel) slots:   {analysis['total_type3']}")
        print(f"[*] Neutralized slots:      {analysis['fixed_type3']}")
        print(f"[*] Conflicting slots:      {len(analysis['problematic'])}")

        if analysis["problematic"]:
            print("\n[-] Found Conflicting Bindings:")
            for p in analysis["problematic"][:10]:
                print(f"    - {p['slot']}: axis={p['axis']}, button={p['button']} ({p['reason']})")
            if len(analysis["problematic"]) > 10:
                print(f"    ... and {len(analysis['problematic']) - 10} more.")

        if analysis["is_fixed"]:
            print("\n[OK] Status: FIX IS INSTALLED. Need for Speed 2015 controller bug is neutralized!")
        else:
            print("\n[!] Status: FIX IS NOT INSTALLED. Conflicting steering wheel bindings are active.")
            print("    Run 'install' to apply the automated fix.")

        check_system_diagnostics()

    elif args.action == "install":
        success = patcher.install()
        sys.exit(0 if success else 1)

    elif args.action == "restore":
        success = patcher.restore()
        sys.exit(0 if success else 1)

    elif args.action == "diag":
        check_system_diagnostics()


if __name__ == "__main__":
    main()
