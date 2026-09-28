using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Nfs2015ControllerFix
{
    class Program
    {
        const string Version = "1.0.0";

        [DllImport("shell32.dll")]
        static extern int SHGetFolderPath(IntPtr hwndOwner, int nFolder, IntPtr hToken, uint dwFlags, [Out] StringBuilder pszPath);

        [DllImport("winmm.dll")]
        static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

        [DllImport("winmm.dll")]
        static extern int joyGetNumDevs();

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern int XInputGetState_1_4(int dwUserIndex, ref byte[] pState);

        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        static extern int XInputGetState_1_3(int dwUserIndex, ref byte[] pState);

        [StructLayout(LayoutKind.Sequential)]
        struct JOYINFOEX
        {
            public int dwSize;
            public int dwFlags;
            public int dwXpos;
            public int dwYpos;
            public int dwZpos;
            public int dwRpos;
            public int dwUpos;
            public int dwVpos;
            public int dwButtons;
            public int dwButtonNumber;
            public int dwPOV;
            public int dwReserved1;
            public int dwReserved2;
        }

        static string GetDocumentsPath()
        {
            const int CSIDL_PERSONAL = 5;
            StringBuilder sb = new StringBuilder(260);
            if (SHGetFolderPath(IntPtr.Zero, CSIDL_PERSONAL, IntPtr.Zero, 0, sb) == 0)
            {
                return sb.ToString();
            }

            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            string oneDrive = Environment.GetEnvironmentVariable("OneDrive") ?? "";
            if (!string.IsNullOrEmpty(oneDrive) && Directory.Exists(Path.Combine(oneDrive, "Documents")))
                return Path.Combine(oneDrive, "Documents");
            if (!string.IsNullOrEmpty(oneDrive) && Directory.Exists(Path.Combine(oneDrive, "Документы")))
                return Path.Combine(oneDrive, "Документы");
            if (!string.IsNullOrEmpty(userProfile))
                return Path.Combine(userProfile, "Documents");

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        static string FindProfilePath(string customPath = null)
        {
            if (!string.IsNullOrEmpty(customPath))
            {
                if (File.Exists(customPath)) return Path.GetFullPath(customPath);
                throw new FileNotFoundException($"Specified profile file not found: {customPath}");
            }

            string docDir = GetDocumentsPath();
            string userProf = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";

            string[] candidates = new string[]
            {
                Path.Combine(docDir, "Need For Speed", "settings", "PROFILEOPTIONS_profile"),
                Path.Combine(docDir, "Need for Speed", "settings", "PROFILEOPTIONS_profile"),
                Path.Combine(userProf, "OneDrive", "Документы", "Need For Speed", "settings", "PROFILEOPTIONS_profile"),
                Path.Combine(userProf, "OneDrive", "Documents", "Need For Speed", "settings", "PROFILEOPTIONS_profile"),
                Path.Combine(userProf, "Documents", "Need For Speed", "settings", "PROFILEOPTIONS_profile"),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return Path.GetFullPath(c);
            }

            throw new FileNotFoundException("Could not locate PROFILEOPTIONS_profile automatically. Please provide path using --path <path>.");
        }

        struct SlotBinding
        {
            public string Prefix;
            public string Type;
            public string Axis;
            public string Button;
            public string Negate;
        }

        static (string WheelCustom, Dictionary<string, SlotBinding> Slots, List<string> Lines) ParseProfile(string filePath)
        {
            var lines = new List<string>(File.ReadAllLines(filePath, Encoding.UTF8));
            var slots = new Dictionary<string, SlotBinding>();
            string wheelCustom = "0";

            var regex = new Regex(@"^(GstKeyBinding\.[^.]+\.[^.]+\.(\d+))\.(axis|button|negate|type)\s+(.*)$");

            foreach (var line in lines)
            {
                string s = line.Trim();
                if (s.StartsWith("GstInput.CustomControlsWheel"))
                {
                    var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) wheelCustom = parts[1];
                }
                var m = regex.Match(s);
                if (m.Success)
                {
                    string prefix = m.Groups[1].Value;
                    string prop = m.Groups[3].Value;
                    string val = m.Groups[4].Value;

                    if (!slots.ContainsKey(prefix))
                    {
                        slots[prefix] = new SlotBinding { Prefix = prefix };
                    }
                    var item = slots[prefix];
                    if (prop == "type") item.Type = val;
                    else if (prop == "axis") item.Axis = val;
                    else if (prop == "button") item.Button = val;
                    else if (prop == "negate") item.Negate = val;
                    slots[prefix] = item;
                }
            }

            return (wheelCustom, slots, lines);
        }

        static bool IsFixed(string wheelCustom, Dictionary<string, SlotBinding> slots, out List<string> issues)
        {
            issues = new List<string>();
            bool ok = true;

            if (wheelCustom != "1")
            {
                ok = false;
                issues.Add($"GstInput.CustomControlsWheel is '{wheelCustom}' (must be '1' so NFS respects custom bindings instead of restoring defaults)");
            }

            foreach (var kvp in slots)
            {
                var slot = kvp.Value;
                if (slot.Type == "3") // Wheel / DirectInput
                {
                    bool activeAxis = slot.Axis != null && slot.Axis != "24";
                    bool activeButton = slot.Button != null && slot.Button != "255";

                    if (activeAxis || activeButton)
                    {
                        ok = false;
                        string desc = $"{slot.Prefix}: axis={slot.Axis}, button={slot.Button}";
                        if (slot.Prefix.Contains("ConceptYaw.2") && slot.Axis == "4")
                            desc += " [DirectInput Axis 4 (L2 Trigger) bound to Steering Left -> causes violent turn on brake!]";
                        else if (slot.Prefix.Contains("ConceptYaw.5") && slot.Axis == "0")
                            desc += " [DirectInput Axis 0 (Stick X) bound to Steering Right -> steering collision!]";
                        else if (slot.Prefix.Contains("ConceptBrake.2") && slot.Button == "25")
                            desc += " [Wheel brake button 25 -> ghost braking bug!]";
                        issues.Add(desc);
                    }
                }
            }

            return ok;
        }

        static void Install(string profilePath)
        {
            var (wheelCustom, slots, lines) = ParseProfile(profilePath);
            if (IsFixed(wheelCustom, slots, out _))
            {
                Console.WriteLine("[OK] Controller Fix is ALREADY installed and active in this profile.");
                Console.WriteLine($"     GstInput.CustomControlsWheel: {wheelCustom}");
                Console.WriteLine($"     All Type 3 (Wheel) bindings are neutralized.");
                return;
            }

            string settingsDir = Path.GetDirectoryName(profilePath);
            string origBackup = Path.Combine(settingsDir, "PROFILEOPTIONS_profile.backup_original");
            if (!File.Exists(origBackup))
            {
                File.Copy(profilePath, origBackup);
                Console.WriteLine($"[+] Created permanent original backup: {Path.GetFileName(origBackup)}");
            }

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string tsBackup = Path.Combine(settingsDir, $"PROFILEOPTIONS_profile.backup_{ts}");
            File.Copy(profilePath, tsBackup);
            Console.WriteLine($"[+] Created safety backup: {Path.GetFileName(tsBackup)}");

            // Perform minimal patching
            var newLines = new List<string>();
            var diffs = new List<(string Old, string New)>();
            var regex = new Regex(@"^(GstKeyBinding\.[^.]+\.[^.]+\.(\d+))\.(axis|button|negate|type)\s+(.*)$");

            foreach (var line in lines)
            {
                string s = line.Trim();
                if (s.StartsWith("GstInput.CustomControlsWheel"))
                {
                    var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1] != "1")
                    {
                        string newLine = "GstInput.CustomControlsWheel 1";
                        diffs.Add((line, newLine));
                        newLines.Add(newLine);
                        continue;
                    }
                }

                var m = regex.Match(s);
                if (m.Success)
                {
                    string prefix = m.Groups[1].Value;
                    string prop = m.Groups[3].Value;
                    if (slots.TryGetValue(prefix, out var slot) && slot.Type == "3")
                    {
                        if (prop == "axis" && !s.EndsWith(" 24"))
                        {
                            string newLine = $"{prefix}.axis 24";
                            diffs.Add((line, newLine));
                            newLines.Add(newLine);
                            continue;
                        }
                        else if (prop == "button" && !s.EndsWith(" 255"))
                        {
                            string newLine = $"{prefix}.button 255";
                            diffs.Add((line, newLine));
                            newLines.Add(newLine);
                            continue;
                        }
                        else if (prop == "negate" && !s.EndsWith(" 0"))
                        {
                            string newLine = $"{prefix}.negate 0";
                            diffs.Add((line, newLine));
                            newLines.Add(newLine);
                            continue;
                        }
                    }
                }

                newLines.Add(line);
            }

            File.WriteAllLines(profilePath, newLines, new UTF8Encoding(false));

            Console.WriteLine($"\n[+] Successfully patched {diffs.Count} lines in {Path.GetFileName(profilePath)}:");
            int shown = 0;
            foreach (var d in diffs)
            {
                if (shown++ < 15)
                {
                    Console.WriteLine($"    - {d.Old}");
                    Console.WriteLine($"    + {d.New}");
                }
            }
            if (diffs.Count > 15) Console.WriteLine($"    ... and {diffs.Count - 15} more lines.");

            var (postWheel, postSlots, _) = ParseProfile(profilePath);
            if (IsFixed(postWheel, postSlots, out _))
            {
                Console.WriteLine("\n[SUCCESS] Fix verified! Conflicting DirectInput wheel bindings neutralized.");
                Console.WriteLine("          Your Sony DualSense via Steam Input or DS4Windows will now handle all driving smoothly.");
            }
            else
            {
                Console.WriteLine("\n[WARNING] Some bindings might still need attention.");
            }
        }

        static void Restore(string profilePath)
        {
            string settingsDir = Path.GetDirectoryName(profilePath);
            string origBackup = Path.Combine(settingsDir, "PROFILEOPTIONS_profile.backup_original");

            string target = null;
            if (File.Exists(origBackup)) target = origBackup;
            else
            {
                var files = Directory.GetFiles(settingsDir, "PROFILEOPTIONS_profile.backup_*");
                Array.Sort(files);
                if (files.Length > 0) target = files[files.Length - 1];
            }

            if (target == null)
            {
                Console.WriteLine("[-] No backups found in settings folder!");
                return;
            }

            Console.WriteLine($"[*] Restoring from: {Path.GetFileName(target)}");
            File.Copy(target, profilePath, true);
            Console.WriteLine("[+] Restored successfully!");

            var (wheelCustom, slots, _) = ParseProfile(profilePath);
            bool fixedNow = IsFixed(wheelCustom, slots, out var issues);
            Console.WriteLine($"    Status: {(fixedNow ? "Patched" : "Original / Unpatched")}");
            Console.WriteLine($"    Remaining active wheel bindings: {issues.Count}");
        }

        static void Verify(string profilePath)
        {
            var (wheelCustom, slots, lines) = ParseProfile(profilePath);
            bool fixedNow = IsFixed(wheelCustom, slots, out var issues);

            Console.WriteLine($"[*] Profile file:         {profilePath}");
            Console.WriteLine($"[*] Total profile lines:  {lines.Count}");
            Console.WriteLine($"[*] CustomControlsWheel:  {wheelCustom}");
            Console.WriteLine($"[*] Total Wheel slots:    {CountType(slots, "3")}");
            Console.WriteLine($"[*] Gamepad (XInput):     {CountType(slots, "2")}");
            Console.WriteLine($"[*] Keyboard (DirectInput): {CountType(slots, "0")}");

            if (issues.Count > 0)
            {
                Console.WriteLine($"\n[!] Found {issues.Count} Conflicting Issues:");
                int shown = 0;
                foreach (var issue in issues)
                {
                    if (shown++ < 10) Console.WriteLine($"    - {issue}");
                }
                if (issues.Count > 10) Console.WriteLine($"    ... and {issues.Count - 10} more.");
            }

            if (fixedNow)
            {
                Console.WriteLine("\n[OK] STATUS: FIX IS INSTALLED. Need for Speed 2015 wheel conflict bug is eliminated.");
            }
            else
            {
                Console.WriteLine("\n[!] STATUS: FIX IS NOT INSTALLED. Conflicting steering wheel bindings are active.");
                Console.WriteLine("    Run 'install' to apply the fix automatically.");
            }

            RunDiagnostics();
        }

        static int CountType(Dictionary<string, SlotBinding> slots, string t)
        {
            int c = 0;
            foreach (var s in slots.Values) if (s.Type == t) c++;
            return c;
        }

        static void RunDiagnostics()
        {
            Console.WriteLine("\n" + new string('=', 65));
            Console.WriteLine(" SYSTEM & CONTROLLER DIAGNOSTICS");
            Console.WriteLine(new string('=', 65));

            // WinMM / DirectInput Joysticks
            Console.WriteLine("--- DirectInput / WinMM Devices ---");
            int numJoysticks = joyGetNumDevs();
            int connectedJoys = 0;
            for (int i = 0; i < 16; i++)
            {
                JOYINFOEX joy = new JOYINFOEX();
                joy.dwSize = Marshal.SizeOf(joy);
                joy.dwFlags = 0xFF; // JOY_RETURNALL
                if (joyGetPosEx(i, ref joy) == 0)
                {
                    connectedJoys++;
                    Console.WriteLine($"Joy {i}: Connected! Axes: X={joy.dwXpos} Y={joy.dwYpos} Z={joy.dwZpos} R={joy.dwRpos} U={joy.dwUpos} V={joy.dwVpos} Buttons=0x{joy.dwButtons:X}");
                }
            }
            if (connectedJoys == 0) Console.WriteLine("No DirectInput joysticks reported by WinMM.");

            // XInput Slots
            Console.WriteLine("\n--- XInput Status ---");
            for (int slot = 0; slot < 4; slot++)
            {
                byte[] state = new byte[16];
                int ret = -1;
                try { ret = XInputGetState_1_4(slot, ref state); }
                catch
                {
                    try { ret = XInputGetState_1_3(slot, ref state); }
                    catch { }
                }

                if (ret == 0)
                    Console.WriteLine($"Slot {slot}: CONNECTED (Virtual or Physical Xbox 360 Controller)");
                else
                    Console.WriteLine($"Slot {slot}: Disconnected (code {ret})");
            }

            // PnP devices query via powershell
            Console.WriteLine("\n--- PnP Controllers Detected ---");
            try
            {
                var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match 'dualsense|wireless controller|xbox|vigem|hidhide' } | Select-Object Class, FriendlyName, InstanceId | Format-Table -AutoSize\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                string outStr = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                Console.WriteLine(string.IsNullOrWhiteSpace(outStr) ? "None found." : outStr.Trim());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PnP query error: {ex.Message}");
            }

            // Platform processes
            Console.WriteLine("\n--- Platform & Middleware Processes ---");
            try
            {
                var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"Get-Process -Name '*steam*', '*ds4*', '*dualsense*', '*hidhide*', '*ea*', '*nfs*' -ErrorAction SilentlyContinue | Select-Object Id, ProcessName | Format-Table -AutoSize\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                string outStr = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                Console.WriteLine(string.IsNullOrWhiteSpace(outStr) ? "None running." : outStr.Trim());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process query error: {ex.Message}");
            }

            Console.WriteLine(new string('=', 65) + "\n");
        }

        static void Main(string[] args)
        {
            Console.WriteLine($"NFS 2015 Controller & DualSense Auto-Fix Tool v{Version}");
            Console.WriteLine("Author: Google Deepmind Pair Programmer");
            Console.WriteLine();

            string action = "verify";
            string customPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "install" || a == "verify" || a == "restore" || a == "diag") action = a;
                else if (a == "--path" && i + 1 < args.Length) customPath = args[++i];
                else if (a == "--help" || a == "-h")
                {
                    Console.WriteLine("Usage:\n  nfs2015-controller-fix.exe [install|verify|restore|diag] [--path <path>]");
                    return;
                }
            }

            try
            {
                string profilePath = FindProfilePath(customPath);
                if (action == "install") Install(profilePath);
                else if (action == "restore") Restore(profilePath);
                else if (action == "diag") RunDiagnostics();
                else Verify(profilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error: {ex.Message}");
            }
        }
    }
}
