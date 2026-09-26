using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Runtime-only anti-cheat shield for the supplied Perfect World Arena install.
// It never edits the installed binaries. It patches the loaded PvpAlive exports
// in the target process and records original bytes for an optional restore.
internal static class Program
{
    private const string DefaultRoot = @"C:\Program Files (x86)\perfectworldarena";
    private const string TargetExe = "完美世界竞技平台.exe";
    private const string AntiCheatDll = "PvpAlive.dll";
    private const string CommunityUrl = "https://qm.qq.com/q/BB2CSRSfZu";
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    private static readonly string[] ExportNames = {
        "startInstance", "stopInstance", "connectHost", "setProxy",
        "queryStatus", "getCurrentIngameParameters", "initMatchInfo", "postEvent",
        "swapData", "queryAvatar", "queryData", "queryLocalInfo",
        "setCurrentMatchId", "setLogger", "IsFeatureOpen", "debugShowInfo"
    };
    private static readonly HashSet<string> ZeroReturn = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "queryStatus", "getCurrentIngameParameters", "queryAvatar", "queryData",
        "queryLocalInfo", "IsFeatureOpen", "debugShowInfo"
    };
    private static readonly HashSet<string> TrueReturn = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "connectHost"
    };
    private static readonly object LogLock = new object();
    private static string root = DefaultRoot;
    private static string ledgerPath;
    private static int intervalMs = 1000;
    private static bool dryRun;
    private static bool once;
    private static bool stopDrivers;
    private static bool loginThenShield;
    private static bool exitAfterPatch;
    private static bool cleanupStaleGuard;
    private static bool cleanupAnalysisServices;
    private static string patchProfile = "report-only";
    private static string proxy;
    private static int specificPid = 0;
    private static int maxScans = 0;
    private static bool observedAny;
    private static readonly Dictionary<string, PatchRecord> Records = new Dictionary<string, PatchRecord>(StringComparer.OrdinalIgnoreCase);

    private sealed class PatchRecord
    {
        public int Pid;
        public long StartTicks;
        public string Module;
        public string Export;
        public long Address;
        public byte[] Original;
        public byte[] Patch;
    }

    private sealed class ModuleInfo
    {
        public string Name;
        public string FileName;
        public long BaseAddress;
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ShowLogo();
        ParseArgs(args);
        if (Environment.Is64BitProcess)
        {
            Log("[X] helper is x64; rebuild with /platform:x86 for the x86 game process");
            Environment.ExitCode = 3;
            return;
        }
        ledgerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shield-patches.log");
        if (HasArg(args, "--restore")) { RestoreLedger(); TryStartDrivers(); return; }
        if (HasArg(args, "--self-test")) { SelfTest(); return; }
        Log("[INFO] root=" + root + " dryRun=" + dryRun + " intervalMs=" + intervalMs +
            " profile=" + patchProfile + " proxy=" + (proxy ?? "direct"));
        if (!Directory.Exists(root)) { Log("[X] target root missing"); Environment.ExitCode = 2; return; }

        if (cleanupStaleGuard) CleanupStaleGames8thGuard();
        if (cleanupAnalysisServices) CleanupFridaServices();
        if (!HasArg(args, "--no-link")) OpenCommunityLink();
        Process launched = null;
        if (loginThenShield)
        {
            launched = LaunchTarget();
            WaitForLogin();
        }
        if (stopDrivers) TryBlockDrivers();
        if (!loginThenShield && HasArg(args, "--launch")) launched = LaunchTarget();
        if (HasArg(args, "--pid")) { /* attach mode is handled by the global scan */ }

        int stable = 0;
        int scans = 0;
        do
        {
            observedAny = false;
            int hits = ScanAndPatch();
            scans++;
            if (hits > 0 || observedAny) stable++; else stable = 0;
            Log("[SCAN] observed=" + observedAny + " patched=" + hits + " stablePasses=" + stable);
            if (once || (exitAfterPatch && hits > 0) || (maxScans > 0 && scans >= maxScans)) break;
            Thread.Sleep(intervalMs);
        } while (true);

        WriteLedger();
        if (Records.Count == 0) Log("[UNVERIFIED] PvpAlive was not observed; keep the shield running while entering a match.");
        else Log("[OK] runtime shield applied to " + Records.Count + " export entries");
    }

    private static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) root = Path.GetFullPath(args[++i]);
            else if (a.Equals("--interval", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[++i], out intervalMs);
            else if (a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) dryRun = true;
            else if (a.Equals("--once", StringComparison.OrdinalIgnoreCase)) once = true;
            else if (a.Equals("--stop-drivers", StringComparison.OrdinalIgnoreCase)) stopDrivers = true;
            else if (a.Equals("--no-drivers", StringComparison.OrdinalIgnoreCase)) stopDrivers = false;
            else if (a.Equals("--login-then-shield", StringComparison.OrdinalIgnoreCase))
            {
                loginThenShield = true;
                stopDrivers = false;
            }
            else if (a.Equals("--exit-after-patch", StringComparison.OrdinalIgnoreCase)) exitAfterPatch = true;
            else if (a.Equals("--cleanup-stale-guard", StringComparison.OrdinalIgnoreCase)) cleanupStaleGuard = true;
            else if (a.Equals("--cleanup-analysis-services", StringComparison.OrdinalIgnoreCase)) cleanupAnalysisServices = true;
            else if (a.Equals("--keep-stale-guard", StringComparison.OrdinalIgnoreCase)) cleanupStaleGuard = false;
            else if (a.Equals("--keep-analysis-services", StringComparison.OrdinalIgnoreCase)) cleanupAnalysisServices = false;
            else if (a.Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                patchProfile = args[++i].ToLowerInvariant();
            else if (a.Equals("--proxy", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                proxy = NormalizeProxy(args[++i]);
            else if (a.Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[++i], out specificPid);
            else if (a.Equals("--max-scans", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[++i], out maxScans);
        }
        if (intervalMs < 100) intervalMs = 100;
        if (maxScans < 0) maxScans = 0;
        if (patchProfile != "observe" && patchProfile != "report-only" &&
            patchProfile != "connection" && patchProfile != "legacy")
            throw new ArgumentException("profile must be observe, report-only, connection, or legacy");
    }

    private static bool HasArg(string[] args, string name) { return args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); }

    private static string NormalizeProxy(string value)
    {
        if (String.IsNullOrWhiteSpace(value)) return null;
        return value.IndexOf("://", StringComparison.Ordinal) >= 0 ? value : "http://" + value;
    }

    private static void ShowLogo()
    {
        ConsoleColor old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("+==============================================================+");
        Console.WriteLine("|                         G A M E S 8 T H                    |");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("|                         Games8Th.Team                      |");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("|                       PWA RUNTIME SHIELD                    |");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("+==============================================================+");
        Console.ForegroundColor = old;
        Console.Out.Flush();
        Thread.Sleep(2000);
    }

    private static void SelfTest()
    {
        string path = Path.Combine(root, "plugin", AntiCheatDll);
        if (!File.Exists(path)) { Log("[X] self-test input missing: " + path); Environment.ExitCode = 2; return; }
        Dictionary<string, uint> exports = PeExports.Read(path);
        foreach (string name in ExportNames)
            Log(exports.ContainsKey(name) ? "[OK] " + name + " RVA=0x" + exports[name].ToString("X") : "[X] missing export " + name);
    }

    private static Process LaunchTarget()
    {
        string exe = Path.Combine(root, TargetExe);
        if (!File.Exists(exe)) { Log("[X] target executable missing: " + exe); return null; }
        try
        {
            ProcessStartInfo info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                Arguments = proxy == null ? "" : "--proxy-server=" + proxy
            };
            Process p = Process.Start(info);
            Log("[OK] launched pid=" + p.Id);
            return p;
        }
        catch (Exception ex) { Log("[X] launch failed: " + ex.Message); return null; }
    }

    private static void OpenCommunityLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo(CommunityUrl) { UseShellExecute = true });
            Log("[OK] opened community link");
        }
        catch (Exception ex)
        {
            Log("[WARN] community link failed: " + ex.Message);
        }
    }

    private static void WaitForLogin()
    {
        Log("[INFO] platform started normally; finish login, then press Enter to arm the user-mode shield");
        if (Console.IsInputRedirected)
        {
            Log("[WARN] input is redirected; arming after 30 seconds");
            Thread.Sleep(30000);
            return;
        }
        Console.ReadLine();
    }

    private static int ScanAndPatch()
    {
        int patched = 0;
        string expectedModule = Path.GetFullPath(Path.Combine(root, "plugin", AntiCheatDll));
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (specificPid != 0 && p.Id != specificPid) continue;
                long startTicks = GetStartTicks(p);
                foreach (ModuleInfo m in EnumerateModules(p))
                {
                    if (!m.Name.Equals(AntiCheatDll, StringComparison.OrdinalIgnoreCase)) continue;
                    string path = m.FileName ?? "";
                    if (!PathsEqual(path, expectedModule)) continue;
                    observedAny = true;
                    patched += PatchModule(p.Id, startTicks, m.BaseAddress, path);
                }
            }
            catch (Exception ex)
            {
                if (p.ProcessName.IndexOf("perfect", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("[WARN] pid=" + p.Id + " module scan: " + ex.Message);
            }
            finally { p.Dispose(); }
        }
        return patched;
    }

    private static List<ModuleInfo> EnumerateModules(Process process)
    {
        var result = new List<ModuleInfo>();
        try
        {
            foreach (ProcessModule m in process.Modules)
            {
                result.Add(new ModuleInfo
                {
                    Name = m.ModuleName,
                    FileName = m.FileName,
                    BaseAddress = m.BaseAddress.ToInt64()
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            Log("[INFO] pid=" + process.Id + " Process.Modules unavailable; using Toolhelp: " + ex.Message);
        }

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)process.Id);
        if (snapshot == INVALID_HANDLE_VALUE) return result;
        try
        {
            MODULEENTRY32 entry = new MODULEENTRY32();
            entry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32));
            if (!Module32First(snapshot, ref entry)) return result;
            do
            {
                result.Add(new ModuleInfo
                {
                    Name = entry.szModule,
                    FileName = entry.szExePath,
                    BaseAddress = entry.modBaseAddr.ToInt64()
                });
                entry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32));
            } while (Module32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private static long GetStartTicks(Process process)
    {
        try { return process.StartTime.Ticks; }
        catch { return 0; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static int PatchModule(int pid, long startTicks, long moduleBase, string path)
    {
        Dictionary<string, uint> rvas;
        try { rvas = PeExports.Read(path); }
        catch (Exception ex) { Log("[X] export parse failed: " + ex.Message); return 0; }
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) { Log("[WARN] OpenProcess failed pid=" + pid + " err=" + Marshal.GetLastWin32Error()); return 0; }
        int count = 0;
        bool suspended = false;
        try
        {
            if (!dryRun) suspended = NtSuspendProcess(h) == 0;
            foreach (string name in ExportNames)
            {
                if (!rvas.ContainsKey(name)) continue;
                if (!ShouldPatch(name)) continue;
                long address = moduleBase + rvas[name];
                string key = pid + ":" + startTicks + ":" + name;
                byte[] patch = MakePatch(name);
                byte[] original = new byte[patch.Length];
                IntPtr n;
                if (!ReadProcessMemory(h, new IntPtr(address), original, original.Length, out n) || n.ToInt32() != original.Length) continue;
                if (Records.ContainsKey(key)) continue;
                if (!dryRun)
                {
                    uint oldProtect;
                    if (!VirtualProtectEx(h, new IntPtr(address), (UIntPtr)patch.Length, PAGE_EXECUTE_READWRITE, out oldProtect)) continue;
                    bool ok = WriteProcessMemory(h, new IntPtr(address), patch, patch.Length, out n) && n.ToInt32() == patch.Length;
                    FlushInstructionCache(h, new IntPtr(address), (UIntPtr)patch.Length);
                    uint ignored; VirtualProtectEx(h, new IntPtr(address), (UIntPtr)patch.Length, oldProtect, out ignored);
                    if (!ok) continue;
                    byte[] verify = new byte[patch.Length];
                    if (!ReadProcessMemory(h, new IntPtr(address), verify, verify.Length, out n) ||
                        n.ToInt32() != verify.Length || !verify.SequenceEqual(patch))
                    {
                        Log("[WARN] patch verify failed pid=" + pid + " " + name);
                        continue;
                    }
                }
                Records[key] = new PatchRecord { Pid = pid, StartTicks = startTicks, Module = path, Export = name, Address = address, Original = original, Patch = patch };
                Log((dryRun ? "[DRY] " : "[PATCH] ") + "pid=" + pid + " " + name + " @0x" + address.ToString("X"));
                count++;
            }
        }
        finally { if (suspended) NtResumeProcess(h); CloseHandle(h); }
        return count;
    }

    private static byte[] MakePatch(string name)
    {
        if (patchProfile == "report-only")
            return new byte[] { 0xC3 };
        if (patchProfile == "connection")
            return TrueReturn.Contains(name)
                ? new byte[] { 0xB8, 1, 0, 0, 0, 0xC3 }
                : new byte[] { 0xC3 };
        if (TrueReturn.Contains(name)) return new byte[] { 0xB8, 1, 0, 0, 0, 0xC3 };
        if (ZeroReturn.Contains(name)) return new byte[] { 0x33, 0xC0, 0xC3 };
        return new byte[] { 0xC3 };
    }

    private static bool ShouldPatch(string name)
    {
        if (patchProfile == "observe") return false;
        if (patchProfile == "report-only")
            return name.Equals("postEvent", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("debugShowInfo", StringComparison.OrdinalIgnoreCase);
        if (patchProfile == "connection")
            return name.Equals("connectHost", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static void CleanupStaleGames8thGuard()
    {
        try
        {
            string[] qc = Run("sc.exe", "qc Games8thGuard").ToArray();
            string binary = qc.FirstOrDefault(x => x.IndexOf("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase) >= 0);
            if (binary == null) return;
            int colon = binary.IndexOf(':');
            string path = colon >= 0 ? binary.Substring(colon + 1).Trim() : "";
            if (path.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase)) path = path.Substring(4);
            path = path.Trim('"');
            if (path.IndexOf("Games8thGuard.sys", StringComparison.OrdinalIgnoreCase) < 0 || File.Exists(path)) return;
            Log("[CLEAN] stale Games8thGuard service path=" + path);
            Run("sc.exe", "stop Games8thGuard");
            Run("sc.exe", "delete Games8thGuard");
            string after = string.Join(" ", Run("sc.exe", "query Games8thGuard"));
            Log(after.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0
                ? "[OK] stale Games8thGuard removed"
                : "[WARN] stale Games8thGuard removal unverified");
        }
        catch (Exception ex) { Log("[WARN] stale guard cleanup: " + ex.Message); }
    }

    private static void CleanupFridaServices()
    {
        try
        {
            foreach (string line in Run("sc.exe", "query type= service state= all"))
            {
                string name = line.Trim();
                if (!name.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase)) continue;
                string service = name.Substring("SERVICE_NAME:".Length).Trim();
                if (!service.StartsWith("frida-", StringComparison.OrdinalIgnoreCase)) continue;
                Log("[CLEAN] analysis service=" + service);
                Run("sc.exe", "stop " + service);
                Run("sc.exe", "delete " + service);
            }
        }
        catch (Exception ex) { Log("[WARN] analysis service cleanup: " + ex.Message); }
    }

    private static void TryBlockDrivers()
    {
        try
        {
            foreach (string line in Run("sc.exe", "query type= driver state= all"))
            {
                string name = line.Trim();
                if (!name.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase)) continue;
                string service = name.Substring("SERVICE_NAME:".Length).Trim();
                string qc = string.Join("\n", Run("sc.exe", "qc " + service));
                if (qc.IndexOf("MessageTransfer.sys", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Log("[DRIVER] candidate=" + service);
                if (dryRun)
                {
                    Log("[DRY] would stop service=" + service);
                    continue;
                }
                Run("sc.exe", "stop " + service);
                string state = string.Join(" ", Run("sc.exe", "query " + service));
                Log(state.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "[OK] driver stopped=" + service
                    : "[WARN] driver stop unverified=" + service);
            }
        }
        catch (Exception ex) { Log("[WARN] driver scan: " + ex.Message); }
    }

    private static void TryStartDrivers()
    {
        try
        {
            foreach (string line in Run("sc.exe", "query type= driver state= all"))
            {
                string name = line.Trim();
                if (!name.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase)) continue;
                string service = name.Substring("SERVICE_NAME:".Length).Trim();
                string qc = string.Join("\n", Run("sc.exe", "qc " + service));
                if (qc.IndexOf("MessageTransfer.sys", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string before = string.Join(" ", Run("sc.exe", "query " + service));
                if (before.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("[OK] driver already running=" + service);
                    continue;
                }
                Run("sc.exe", "start " + service);
                string after = string.Join(" ", Run("sc.exe", "query " + service));
                Log(after.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "[OK] driver started=" + service
                    : "[WARN] driver start unverified=" + service);
            }
        }
        catch (Exception ex) { Log("[WARN] driver restore: " + ex.Message); }
    }

    private static IEnumerable<string> Run(string file, string arguments)
    {
        Process p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
        string text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void WriteLedger()
    {
        if (dryRun)
        {
            Log("[INFO] dry-run; patch ledger not written");
            return;
        }
        using (StreamWriter w = new StreamWriter(ledgerPath, false, Encoding.UTF8))
        {
            foreach (PatchRecord r in Records.Values)
                w.WriteLine(r.Pid + "\t" + r.StartTicks + "\t" + r.Export + "\t0x" + r.Address.ToString("X") + "\t" + Hex(r.Original) + "\t" + Hex(r.Patch));
        }
    }

    private static void RestoreLedger()
    {
        if (!File.Exists(ledgerPath)) { Log("[X] no ledger: " + ledgerPath); Environment.ExitCode = 2; return; }
        int restored = 0;
        foreach (string line in File.ReadAllLines(ledgerPath))
        {
            string[] f = line.Split('\t');
            if (f.Length < 5) continue;
            int pid; long startTicks; long address; string export; string originalText; string patchText;
            if (!int.TryParse(f[0], out pid)) continue;
            if (f.Length >= 6)
            {
                if (!long.TryParse(f[1], out startTicks) || !long.TryParse(f[3].Substring(2), System.Globalization.NumberStyles.HexNumber, null, out address)) continue;
                export = f[2]; originalText = f[4]; patchText = f[5];
            }
            else
            {
                startTicks = 0;
                if (!long.TryParse(f[2].Substring(2), System.Globalization.NumberStyles.HexNumber, null, out address)) continue;
                export = f[1]; originalText = f[3]; patchText = f[4];
            }
            byte[] original = ParseHex(originalText);
            byte[] patch = ParseHex(patchText);
            IntPtr h = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { Log("[WARN] restore OpenProcess failed pid=" + pid); continue; }
            try
            {
                if (startTicks != 0)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(pid))
                            if (process.StartTime.Ticks != startTicks)
                            {
                                Log("[WARN] restore skipped pid=" + pid + " identity changed");
                                continue;
                            }
                    }
                    catch
                    {
                        Log("[WARN] restore skipped pid=" + pid + " identity unavailable");
                        continue;
                    }
                }
                byte[] current = new byte[patch.Length];
                IntPtr read;
                if (!ReadProcessMemory(h, new IntPtr(address), current, current.Length, out read) ||
                    read.ToInt32() != current.Length || !current.SequenceEqual(patch))
                {
                    Log("[WARN] restore skipped pid=" + pid + " " + export + " current bytes differ");
                    continue;
                }
                uint oldProtect;
                if (!VirtualProtectEx(h, new IntPtr(address), (UIntPtr)original.Length, PAGE_EXECUTE_READWRITE, out oldProtect)) continue;
                IntPtr written;
                bool ok = WriteProcessMemory(h, new IntPtr(address), original, original.Length, out written);
                FlushInstructionCache(h, new IntPtr(address), (UIntPtr)original.Length);
                uint ignored; VirtualProtectEx(h, new IntPtr(address), (UIntPtr)original.Length, oldProtect, out ignored);
                if (ok && written.ToInt32() == original.Length) { restored++; Log("[RESTORE] pid=" + pid + " " + export); }
            }
            finally { CloseHandle(h); }
        }
        Log("[OK] restored=" + restored);
    }

    private static byte[] ParseHex(string text)
    {
        if (text.Length % 2 != 0) return new byte[0];
        byte[] b = new byte[text.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
        return b;
    }

    private static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", ""); }
    private static void Log(string text)
    {
        lock (LogLock)
        {
            ConsoleColor old = Console.ForegroundColor;
            if (text.IndexOf("[X]", StringComparison.Ordinal) >= 0) Console.ForegroundColor = ConsoleColor.Red;
            else if (text.IndexOf("[WARN]", StringComparison.Ordinal) >= 0 || text.IndexOf("[UNVERIFIED]", StringComparison.Ordinal) >= 0 || text.IndexOf("[DRIVER]", StringComparison.Ordinal) >= 0) Console.ForegroundColor = ConsoleColor.Yellow;
            else if (text.IndexOf("[OK]", StringComparison.Ordinal) >= 0 || text.IndexOf("[PATCH]", StringComparison.Ordinal) >= 0 || text.IndexOf("[RESTORE]", StringComparison.Ordinal) >= 0 || text.IndexOf("[DRY]", StringComparison.Ordinal) >= 0) Console.ForegroundColor = ConsoleColor.Green;
            else if (text.IndexOf("[SCAN]", StringComparison.Ordinal) >= 0 || text.IndexOf("[INFO]", StringComparison.Ordinal) >= 0) Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(DateTime.Now.ToString("s") + " " + text);
            Console.ForegroundColor = old;
        }
    }

    private static class PeExports
    {
        public static Dictionary<string, uint> Read(string path)
        {
            byte[] b = File.ReadAllBytes(path); Func<int, uint> U32 = o => BitConverter.ToUInt32(b, o); Func<int, ushort> U16 = o => BitConverter.ToUInt16(b, o);
            int pe = (int)U32(0x3c); int sections = U16(pe + 6); ushort optSize = U16(pe + 20); int opt = pe + 24;
            uint exportRva = U32(opt + 96); int section = opt + optSize;
            uint raw = 0, va = 0, rawSize = 0, virt = 0;
            for (int i = 0; i < sections; i++) { int s = section + i * 40; uint sv = U32(s + 12), sr = U32(s + 16); if (exportRva >= sv && exportRva < sv + Math.Max(sr, U32(s + 8))) { va = sv; raw = U32(s + 20); rawSize = sr; virt = U32(s + 8); break; } }
            if (raw == 0) throw new InvalidDataException("export directory not mapped");
            Func<uint, int> Off = r => checked((int)(raw + (r - va)));
            int d = Off(exportRva); uint names = U32(d + 32), funcs = U32(d + 28), ords = U32(d + 36); uint count = U32(d + 24), fcount = U32(d + 20);
            var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; i < count; i++) { uint nr = U32(Off(names + i * 4)); int no = Off(nr); string n = ReadAscii(b, no); ushort ord = U16(Off(ords + i * 2)); if (ord < fcount) result[n] = U32(Off(funcs + (uint)ord * 4)); }
            return result;
        }
        private static string ReadAscii(byte[] b, int o) { int e = o; while (e < b.Length && b[e] != 0) e++; return Encoding.ASCII.GetString(b, o, e - o); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, UIntPtr size, uint protect, out uint oldProtect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32First(IntPtr snapshot, ref MODULEENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32Next(IntPtr snapshot, ref MODULEENTRY32 entry);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }
}
