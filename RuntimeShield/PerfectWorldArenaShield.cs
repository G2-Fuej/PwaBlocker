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
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint CREATE_SUSPENDED = 0x00000004;
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
    private static bool stopDrivers = true;
    private static int specificPid = 0;
    private static readonly Dictionary<string, PatchRecord> Records = new Dictionary<string, PatchRecord>(StringComparer.OrdinalIgnoreCase);

    private sealed class PatchRecord
    {
        public int Pid;
        public string Module;
        public string Export;
        public long Address;
        public byte[] Original;
        public byte[] Patch;
    }

    private static void Main(string[] args)
    {
        ParseArgs(args);
        ledgerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shield-patches.log");
        if (HasArg(args, "--restore")) { RestoreLedger(); return; }
        if (HasArg(args, "--self-test")) { SelfTest(); return; }
        Log("[INFO] root=" + root + " dryRun=" + dryRun + " intervalMs=" + intervalMs);
        if (!Directory.Exists(root)) { Log("[X] target root missing"); Environment.ExitCode = 2; return; }

        if (stopDrivers) TryBlockDrivers();
        Process launched = null;
        if (HasArg(args, "--launch")) launched = LaunchTarget();
        if (HasArg(args, "--pid")) { /* attach mode is handled by the global scan */ }

        int stable = 0;
        do
        {
            int hits = ScanAndPatch();
            if (hits > 0) stable++; else stable = 0;
            Log("[SCAN] patched=" + hits + " stablePasses=" + stable);
            if (once) break;
            Thread.Sleep(intervalMs);
            if (launched != null && launched.HasExited && stable > 0) break;
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
            else if (a.Equals("--no-drivers", StringComparison.OrdinalIgnoreCase)) stopDrivers = false;
            else if (a.Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[++i], out specificPid);
        }
        if (intervalMs < 100) intervalMs = 100;
    }

    private static bool HasArg(string[] args, string name) { return args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); }

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
            Process p = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = root, UseShellExecute = false });
            Log("[OK] launched pid=" + p.Id);
            return p;
        }
        catch (Exception ex) { Log("[X] launch failed: " + ex.Message); return null; }
    }

    private static int ScanAndPatch()
    {
        int patched = 0;
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (specificPid != 0 && p.Id != specificPid) continue;
                foreach (ProcessModule m in p.Modules)
                {
                    if (!m.ModuleName.Equals(AntiCheatDll, StringComparison.OrdinalIgnoreCase)) continue;
                    string path = m.FileName ?? "";
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    patched += PatchModule(p.Id, m, path);
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

    private static int PatchModule(int pid, ProcessModule module, string path)
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
            long baseAddress = module.BaseAddress.ToInt64();
            if (!dryRun) suspended = NtSuspendProcess(h) == 0;
            foreach (string name in ExportNames)
            {
                if (!rvas.ContainsKey(name)) continue;
                long address = baseAddress + rvas[name];
                string key = pid + ":" + name;
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
                }
                Records[key] = new PatchRecord { Pid = pid, Module = path, Export = name, Address = address, Original = original, Patch = patch };
                Log((dryRun ? "[DRY] " : "[PATCH] ") + "pid=" + pid + " " + name + " @0x" + address.ToString("X"));
                count++;
            }
        }
        finally { if (suspended) NtResumeProcess(h); CloseHandle(h); }
        return count;
    }

    private static byte[] MakePatch(string name)
    {
        if (TrueReturn.Contains(name)) return new byte[] { 0xB8, 1, 0, 0, 0, 0xC3 };
        if (ZeroReturn.Contains(name)) return new byte[] { 0x33, 0xC0, 0xC3 };
        return new byte[] { 0xC3 };
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
                if (!dryRun) Run("sc.exe", "stop " + service);
            }
        }
        catch (Exception ex) { Log("[WARN] driver scan: " + ex.Message); }
    }

    private static IEnumerable<string> Run(string file, string arguments)
    {
        Process p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
        string text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void WriteLedger()
    {
        using (StreamWriter w = new StreamWriter(ledgerPath, false, Encoding.UTF8))
        {
            foreach (PatchRecord r in Records.Values)
                w.WriteLine(r.Pid + "\t" + r.Export + "\t0x" + r.Address.ToString("X") + "\t" + Hex(r.Original) + "\t" + Hex(r.Patch));
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
            int pid; long address;
            if (!int.TryParse(f[0], out pid) || !long.TryParse(f[2].Substring(2), System.Globalization.NumberStyles.HexNumber, null, out address)) continue;
            byte[] original = ParseHex(f[3]);
            IntPtr h = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { Log("[WARN] restore OpenProcess failed pid=" + pid); continue; }
            try
            {
                uint oldProtect;
                if (!VirtualProtectEx(h, new IntPtr(address), (UIntPtr)original.Length, PAGE_EXECUTE_READWRITE, out oldProtect)) continue;
                IntPtr written;
                bool ok = WriteProcessMemory(h, new IntPtr(address), original, original.Length, out written);
                FlushInstructionCache(h, new IntPtr(address), (UIntPtr)original.Length);
                uint ignored; VirtualProtectEx(h, new IntPtr(address), (UIntPtr)original.Length, oldProtect, out ignored);
                if (ok && written.ToInt32() == original.Length) { restored++; Log("[RESTORE] pid=" + pid + " " + f[1]); }
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
    private static void Log(string text) { lock (LogLock) { Console.WriteLine(DateTime.Now.ToString("s") + " " + text); } }

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
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
}
