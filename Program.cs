using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
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
    private const uint FILE_DEVICE_PWA = 0x8000;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_READ_ACCESS = 1;
    private const uint FILE_WRITE_ACCESS = 2;
    private const uint IOCTL_PWA_QUERY_TARGET = (FILE_DEVICE_PWA << 16) | (FILE_READ_ACCESS << 14) | (0x801u << 2) | METHOD_BUFFERED;
    private const uint IOCTL_PWA_APPLY_PATCH = (FILE_DEVICE_PWA << 16) | (FILE_WRITE_ACCESS << 14) | (0x802u << 2) | METHOD_BUFFERED;
    private const uint IOCTL_PWA_RESTORE_PATCH = (FILE_DEVICE_PWA << 16) | (FILE_WRITE_ACCESS << 14) | (0x803u << 2) | METHOD_BUFFERED;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
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
    private static bool noDrivers;
    private static bool loginThenShield;
    private static bool exitAfterPatch;
    private static bool cleanupStaleGuard;
    private static bool cleanupAnalysisServices;
    private static string patchProfile = "report-only";
    private static string proxy;
    private static int specificPid = 0;
    private static int maxScans = 0;
    private static bool observedAny;
    private static bool rootExplicit;
    private static bool launchRequested;
    private static bool pauseOnStartupError;
    private static bool kernelShield;
    private const string KernelDevice = @"\\.\PwaKernelShield";
    private static readonly Dictionary<string, PatchRecord> Records = new Dictionary<string, PatchRecord>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PatchRecord> LedgerRecords = new Dictionary<string, PatchRecord>(StringComparer.OrdinalIgnoreCase);

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
        pauseOnStartupError = args.Length == 0;
        if (Environment.Is64BitProcess)
        {
            Log("[X] helper is x64; rebuild with /platform:x86 for the x86 game process");
            Environment.ExitCode = 3;
            return;
        }
        ledgerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shield-patches.log");
        if (HasArg(args, "--restore"))
        {
            RestoreLedger();
            if (!noDrivers && !stopDrivers) TryStartDrivers();
            return;
        }
        LoadLedgerRecords();
        if (!ResolveTargetRoot())
        {
            Log("[X] target executable was not found; install Perfect World Arena or pass --root <directory>");
            Environment.ExitCode = 2;
            PauseBeforeExit();
            return;
        }
        if (HasArg(args, "--self-test")) { SelfTest(); return; }
        Log("[INFO] root=" + root + " dryRun=" + dryRun + " intervalMs=" + intervalMs +
            " profile=" + patchProfile + " proxy=" + (proxy ?? "direct") + " kernel=" + kernelShield);
        if (kernelShield && !dryRun && !KernelDeviceAvailable())
        {
            Log("[X] PwaKernelShield device is unavailable; refusing user-mode fallback");
            Environment.ExitCode = 4;
            PauseBeforeExit();
            return;
        }
        if (!Directory.Exists(root)) { Log("[X] target root missing"); Environment.ExitCode = 2; return; }

        if (cleanupStaleGuard) CleanupStaleGames8thGuard();
        if (cleanupAnalysisServices) CleanupFridaServices();
        if (args.Length > 0 && !HasArg(args, "--no-link")) OpenCommunityLink();
        Process launched = null;
        if (!dryRun && !noDrivers && !stopDrivers && (loginThenShield || launchRequested)) TryStartDrivers();
        if (loginThenShield)
        {
            launched = LaunchTarget();
            if (launched == null) { Environment.ExitCode = 2; PauseBeforeExit(); return; }
            WaitForLogin();
        }
        if (stopDrivers) TryBlockDrivers();
        if (!loginThenShield && launchRequested)
        {
            launched = LaunchTarget();
            if (launched == null) { Environment.ExitCode = 2; PauseBeforeExit(); return; }
        }
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
        if (args.Length == 0) launchRequested = true;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                root = Path.GetFullPath(args[++i]);
                rootExplicit = true;
            }
            else if (a.Equals("--interval", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[++i], out intervalMs);
            else if (a.Equals("--launch", StringComparison.OrdinalIgnoreCase)) launchRequested = true;
            else if (a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) dryRun = true;
            else if (a.Equals("--once", StringComparison.OrdinalIgnoreCase)) once = true;
            else if (a.Equals("--stop-drivers", StringComparison.OrdinalIgnoreCase)) { stopDrivers = true; noDrivers = false; }
            else if (a.Equals("--no-drivers", StringComparison.OrdinalIgnoreCase)) { noDrivers = true; stopDrivers = false; }
            else if (a.Equals("--login-then-shield", StringComparison.OrdinalIgnoreCase))
            {
                loginThenShield = true;
                stopDrivers = false;
                noDrivers = false;
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
            else if (a.Equals("--kernel-shield", StringComparison.OrdinalIgnoreCase)) kernelShield = true;
        }
        if (intervalMs < 100) intervalMs = 100;
        if (maxScans < 0) maxScans = 0;
        if (patchProfile != "observe" && patchProfile != "report-only" &&
            patchProfile != "connection" && patchProfile != "legacy")
            throw new ArgumentException("profile must be observe, report-only, connection, or legacy");
        if (kernelShield && patchProfile != "report-only")
            throw new ArgumentException("--kernel-shield supports only --profile report-only");
    }

    private static bool HasArg(string[] args, string name) { return args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); }

    private static bool ResolveTargetRoot()
    {
        if (HasTargetExecutable(root)) return true;
        if (rootExplicit) return false;

        var candidates = new List<string>();
        candidates.Add(AppDomain.CurrentDomain.BaseDirectory);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "perfectworldarena"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "perfectworldarena"));
        candidates.AddRange(ReadInstalledRoots(Registry.CurrentUser));
        candidates.AddRange(ReadInstalledRoots(Registry.LocalMachine));

        foreach (string candidate in candidates.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { continue; }
            if (!HasTargetExecutable(full)) continue;
            root = full;
            Log("[INFO] selected target root=" + root);
            return true;
        }
        return false;
    }

    private static bool HasTargetExecutable(string directory)
    {
        return !String.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, TargetExe));
    }

    private static IEnumerable<string> ReadInstalledRoots(RegistryKey hive)
    {
        var roots = new List<string>();
        string[] uninstallKeys = {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (string keyPath in uninstallKeys)
        {
            RegistryKey uninstall = null;
            try { uninstall = hive.OpenSubKey(keyPath); }
            catch { }
            if (uninstall == null) continue;
            using (uninstall)
            {
                foreach (string subName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey app = uninstall.OpenSubKey(subName))
                        {
                            if (app == null) continue;
                            string displayName = app.GetValue("DisplayName") as string;
                            if (String.IsNullOrWhiteSpace(displayName) ||
                                (displayName.IndexOf("perfectworldarena", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 displayName.IndexOf("完美世界竞技平台", StringComparison.OrdinalIgnoreCase) < 0)) continue;
                            string installLocation = app.GetValue("InstallLocation") as string;
                            if (!String.IsNullOrWhiteSpace(installLocation)) roots.Add(installLocation.Trim('"'));
                            string displayIcon = app.GetValue("DisplayIcon") as string;
                            if (String.IsNullOrWhiteSpace(displayIcon)) continue;
                            int comma = displayIcon.LastIndexOf(',');
                            if (comma > 0) displayIcon = displayIcon.Substring(0, comma);
                            displayIcon = displayIcon.Trim().Trim('"');
                            string iconDirectory = Path.GetDirectoryName(displayIcon);
                            if (!String.IsNullOrWhiteSpace(iconDirectory)) roots.Add(iconDirectory);
                        }
                    }
                    catch { }
                }
            }
        }
        return roots;
    }

    private static void PauseBeforeExit()
    {
        if (!pauseOnStartupError || Console.IsInputRedirected) return;
        Log("[INFO] press Enter to close");
        try { Console.ReadLine(); } catch { }
    }

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
        int kernelRequestSize = Marshal.SizeOf(typeof(KernelPatchRequest));
        int kernelTargetSize = Marshal.SizeOf(typeof(KernelTargetInfo));
        Log(kernelTargetSize == 544 ? "[OK] kernel target size=544" : "[X] kernel target size=" + kernelTargetSize);
        Log(kernelRequestSize == 124 ? "[OK] kernel request size=124" : "[X] kernel request size=" + kernelRequestSize);
        Log(IOCTL_PWA_QUERY_TARGET == 0x80006004u ? "[OK] kernel query ioctl=0x80006004" : "[X] kernel query ioctl mismatch");
        Log(IOCTL_PWA_APPLY_PATCH == 0x8000A008u ? "[OK] kernel apply ioctl=0x8000A008" : "[X] kernel apply ioctl mismatch");
        Log(IOCTL_PWA_RESTORE_PATCH == 0x8000A00Cu ? "[OK] kernel restore ioctl=0x8000A00C" : "[X] kernel restore ioctl mismatch");
        if (kernelTargetSize != 544 || kernelRequestSize != 124 || IOCTL_PWA_QUERY_TARGET != 0x80006004u ||
            IOCTL_PWA_APPLY_PATCH != 0x8000A008u || IOCTL_PWA_RESTORE_PATCH != 0x8000A00Cu)
            Environment.ExitCode = 3;
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
            foreach (Process existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TargetExe)))
            {
                try
                {
                    if (PathsEqual(existing.MainModule.FileName, exe))
                    {
                        Log("[OK] using running platform pid=" + existing.Id);
                        return existing;
                    }
                }
                catch { }
                existing.Dispose();
            }
            ProcessStartInfo info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = root,
                UseShellExecute = true,
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
        if (kernelShield) return ScanKernelTarget();
        int patched = 0;
        string expectedModule = Path.GetFullPath(Path.Combine(root, "plugin", AntiCheatDll));
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (specificPid != 0 && p.Id != specificPid) continue;
                foreach (ModuleInfo m in EnumerateModules(p))
                {
                    if (!m.Name.Equals(AntiCheatDll, StringComparison.OrdinalIgnoreCase)) continue;
                    string path = m.FileName ?? "";
                    if (!PathsEqual(path, expectedModule)) continue;
                    observedAny = true;
                    long startTicks = GetStartTicks(p);
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

    private static int ScanKernelTarget()
    {
        KernelTargetInfo target;
        if (!KernelQueryTarget(out target)) return 0;
        if (specificPid != 0 && target.ProcessId != specificPid) return 0;

        string expectedModule = Path.GetFullPath(Path.Combine(root, "plugin", AntiCheatDll));
        const string expectedSuffix = @"\Program Files (x86)\perfectworldarena\plugin\PvpAlive.dll";
        if (String.IsNullOrWhiteSpace(target.ImagePath) ||
            !target.ImagePath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            Log("[WARN] kernel target path rejected=" + (target.ImagePath ?? "<null>"));
            return 0;
        }
        if (!File.Exists(expectedModule) || target.ImageBase == 0 || target.ImageSize == 0) return 0;

        observedAny = true;
        Dictionary<string, uint> rvas;
        try { rvas = PeExports.Read(expectedModule); }
        catch (Exception ex) { Log("[X] kernel export parse failed: " + ex.Message); return 0; }

        int count = 0;
        long identity = unchecked((long)target.ProcessStartKey);
        foreach (string name in ExportNames)
        {
            uint rva;
            if (!ShouldPatch(name) || !rvas.TryGetValue(name, out rva)) continue;
            byte[] patch = MakePatch(name);
            if (patch.Length != 1 || patch[0] != 0xC3 || (ulong)rva + 1UL > (ulong)target.ImageSize) continue;

            byte[] expected;
            try { expected = PeExports.ReadBytesAtRva(expectedModule, rva, patch.Length); }
            catch (Exception ex) { Log("[WARN] disk byte read failed " + name + ": " + ex.Message); continue; }

            long address = checked((long)(target.ImageBase + rva));
            string key = target.ProcessId + ":" + identity + ":" + name;
            if (Records.ContainsKey(key)) continue;

            if (dryRun)
            {
                Records[key] = new PatchRecord {
                    Pid = target.ProcessId, StartTicks = identity, Module = expectedModule,
                    Export = name, Address = address, Original = expected, Patch = patch
                };
                Log("[DRY] kernel pid=" + target.ProcessId + " " + name + " @0x" + address.ToString("X"));
                count++;
                continue;
            }

            byte[] actualOriginal;
            if (!KernelApplyPatch(target.ProcessId, target.ProcessStartKey, address, expected, patch, out actualOriginal)) continue;
            if (actualOriginal.SequenceEqual(patch))
            {
                PatchRecord prior;
                if (LedgerRecords.TryGetValue(key, out prior) && prior.Address == address && prior.Patch.SequenceEqual(patch))
                {
                    Records[key] = prior;
                    Log("[INFO] kernel target already patched; retained trusted ledger pid=" + target.ProcessId + " " + name);
                }
                else
                {
                    Log("[WARN] kernel target already patched but no trusted ledger exists pid=" + target.ProcessId + " " + name);
                }
                continue;
            }

            PatchRecord record = new PatchRecord {
                Pid = target.ProcessId, StartTicks = identity, Module = expectedModule,
                Export = name, Address = address, Original = actualOriginal, Patch = patch
            };
            Records[key] = record;
            LedgerRecords[key] = record;
            Log("[PATCH] kernel pid=" + target.ProcessId + " " + name + " @0x" + address.ToString("X"));
            count++;
        }
        return count;
    }

    private static List<ModuleInfo> EnumerateModules(Process process)
    {
        var result = new List<ModuleInfo>();
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
                if (original.SequenceEqual(patch))
                {
                    PatchRecord prior;
                    if (LedgerRecords.TryGetValue(key, out prior) && prior.Address == address && prior.Patch.SequenceEqual(patch))
                    {
                        Records[key] = prior;
                        Log("[INFO] already patched; retained trusted ledger pid=" + pid + " " + name);
                    }
                    else
                    {
                        Log("[WARN] already patched but no trusted ledger exists pid=" + pid + " " + name);
                    }
                    continue;
                }
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
                        uint rollbackProtect;
                        if (VirtualProtectEx(h, new IntPtr(address), (UIntPtr)original.Length, PAGE_EXECUTE_READWRITE, out rollbackProtect))
                        {
                            IntPtr rollbackWritten;
                            bool rolledBack = WriteProcessMemory(h, new IntPtr(address), original, original.Length, out rollbackWritten) &&
                                rollbackWritten.ToInt32() == original.Length;
                            FlushInstructionCache(h, new IntPtr(address), (UIntPtr)original.Length);
                            uint rollbackIgnored;
                            VirtualProtectEx(h, new IntPtr(address), (UIntPtr)original.Length, rollbackProtect, out rollbackIgnored);
                            Log(rolledBack
                                ? "[RESTORE] rollback after verify failure pid=" + pid + " " + name
                                : "[X] rollback after verify failure failed pid=" + pid + " " + name);
                        }
                        continue;
                    }
                }
                PatchRecord record = new PatchRecord { Pid = pid, StartTicks = startTicks, Module = path, Export = name, Address = address, Original = original, Patch = patch };
                Records[key] = record;
                LedgerRecords[key] = record;
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
        if (Records.Count == 0)
        {
            Log("[INFO] no verified patch records; existing ledger preserved");
            return;
        }
        using (StreamWriter w = new StreamWriter(ledgerPath, false, Encoding.UTF8))
        {
            foreach (PatchRecord r in Records.Values)
                w.WriteLine(r.Pid + "\t" + r.StartTicks + "\t" + r.Export + "\t0x" + r.Address.ToString("X") + "\t" + Hex(r.Original) + "\t" + Hex(r.Patch));
        }
    }

    private static void LoadLedgerRecords()
    {
        if (!File.Exists(ledgerPath)) return;
        foreach (string line in File.ReadAllLines(ledgerPath))
        {
            string[] f = line.Split('\t');
            if (f.Length < 6) continue;
            int pid; long startTicks; long address;
            if (!int.TryParse(f[0], out pid) || !long.TryParse(f[1], out startTicks) ||
                !long.TryParse(f[3].Substring(2), System.Globalization.NumberStyles.HexNumber, null, out address)) continue;
            byte[] original = ParseHex(f[4]);
            byte[] patch = ParseHex(f[5]);
            if (original.Length == 0 || patch.Length == 0) continue;
            string key = pid + ":" + startTicks + ":" + f[2];
            LedgerRecords[key] = new PatchRecord { Pid = pid, StartTicks = startTicks, Export = f[2], Address = address, Original = original, Patch = patch };
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct KernelTargetInfo
    {
        public int ProcessId;
        public uint ImageSize;
        public ulong ImageBase;
        public ulong ProcessStartKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ImagePath;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KernelPatchRequest
    {
        public int ProcessId;
        public int Length;
        public long Address;
        public ulong ProcessStartKey;
        public int ExpectedLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Expected;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Replacement;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Original;
    }

    private static bool KernelDeviceAvailable()
    {
        IntPtr h = CreateFile(KernelDevice, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return false;
        CloseHandle(h);
        return true;
    }

    private static bool KernelQueryTarget(out KernelTargetInfo target)
    {
        target = new KernelTargetInfo();
        int size = Marshal.SizeOf(typeof(KernelTargetInfo));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            IntPtr returned;
            IntPtr h = CreateFile(KernelDevice, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;
            try
            {
                bool ok = DeviceIoControl(h, IOCTL_PWA_QUERY_TARGET, IntPtr.Zero, 0, buffer, size, out returned, IntPtr.Zero);
                if (!ok || returned.ToInt64() < size) return false;
                target = (KernelTargetInfo)Marshal.PtrToStructure(buffer, typeof(KernelTargetInfo));
                return true;
            }
            finally { CloseHandle(h); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool KernelApplyPatch(int pid, ulong processStartKey, long address, byte[] expected, byte[] replacement, out byte[] actualOriginal)
    {
        actualOriginal = null;
        var request = new KernelPatchRequest
        {
            ProcessId = pid,
            Length = replacement.Length,
            Address = address,
            ProcessStartKey = processStartKey,
            ExpectedLength = expected.Length,
            Expected = new byte[32],
            Replacement = new byte[32],
            Original = new byte[32]
        };
        Array.Copy(expected, request.Expected, expected.Length);
        Array.Copy(replacement, request.Replacement, replacement.Length);
        Array.Copy(expected, request.Original, expected.Length);
        int size = Marshal.SizeOf(typeof(KernelPatchRequest));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(request, buffer, false);
            IntPtr returned;
            IntPtr h = CreateFile(KernelDevice, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                Log("[X] kernel device open failed err=" + Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                bool ok = DeviceIoControl(h, IOCTL_PWA_APPLY_PATCH, buffer, size, buffer, size, out returned, IntPtr.Zero);
                if (!ok) Log("[WARN] kernel patch failed pid=" + pid + " err=" + Marshal.GetLastWin32Error());
                if (ok && returned.ToInt64() >= size)
                {
                    request = (KernelPatchRequest)Marshal.PtrToStructure(buffer, typeof(KernelPatchRequest));
                    actualOriginal = request.Original.Take(request.Length).ToArray();
                }
                if (ok && (actualOriginal == null || actualOriginal.Length != replacement.Length)) return false;
                return ok;
            }
            finally { CloseHandle(h); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool KernelRestorePatch(int pid, ulong processStartKey, long address, byte[] patch, byte[] original)
    {
        var request = new KernelPatchRequest
        {
            ProcessId = pid,
            Length = original.Length,
            Address = address,
            ProcessStartKey = processStartKey,
            ExpectedLength = patch.Length,
            Expected = new byte[32],
            Replacement = new byte[32],
            Original = new byte[32]
        };
        Array.Copy(patch, request.Expected, patch.Length);
        Array.Copy(original, request.Original, original.Length);
        int size = Marshal.SizeOf(typeof(KernelPatchRequest));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(request, buffer, false);
            IntPtr returned;
            IntPtr h = CreateFile(KernelDevice, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                Log("[X] kernel device open failed during restore err=" + Marshal.GetLastWin32Error());
                return false;
            }
            try { return DeviceIoControl(h, IOCTL_PWA_RESTORE_PATCH, buffer, size, buffer, size, out returned, IntPtr.Zero); }
            finally { CloseHandle(h); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
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
            if (kernelShield)
            {
                KernelTargetInfo target;
                if (!KernelQueryTarget(out target) || target.ProcessId != pid ||
                    (startTicks != 0 && unchecked((long)target.ProcessStartKey) != startTicks))
                {
                    Log("[WARN] kernel restore skipped pid=" + pid + " identity changed or unavailable");
                    continue;
                }
                ulong offset = unchecked((ulong)address) - target.ImageBase;
                if (unchecked((ulong)address) < target.ImageBase || offset >= (ulong)target.ImageSize ||
                    (ulong)original.Length > (ulong)target.ImageSize - offset)
                {
                    Log("[WARN] kernel restore skipped pid=" + pid + " address outside current image");
                    continue;
                }
                if (KernelRestorePatch(pid, target.ProcessStartKey, address, patch, original))
                {
                    restored++;
                    Log("[RESTORE] kernel pid=" + pid + " " + export);
                }
                else Log("[WARN] kernel restore failed pid=" + pid + " " + export);
                continue;
            }

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
        public static byte[] ReadBytesAtRva(string path, uint rva, int length)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException("length");
            byte[] b = File.ReadAllBytes(path);
            Func<int, uint> U32 = o => BitConverter.ToUInt32(b, o);
            Func<int, ushort> U16 = o => BitConverter.ToUInt16(b, o);
            int pe = checked((int)U32(0x3c));
            int sections = U16(pe + 6);
            int section = pe + 24 + U16(pe + 20);
            for (int i = 0; i < sections; i++)
            {
                int s = section + i * 40;
                uint virtualSize = U32(s + 8);
                uint virtualAddress = U32(s + 12);
                uint rawSize = U32(s + 16);
                uint rawAddress = U32(s + 20);
                uint span = Math.Max(virtualSize, rawSize);
                if (rva < virtualAddress || rva - virtualAddress >= span) continue;
                uint delta = rva - virtualAddress;
                if (delta > rawSize || (uint)length > rawSize - delta)
                    throw new InvalidDataException("RVA has no complete raw-file backing");
                int offset = checked((int)(rawAddress + delta));
                if (offset < 0 || length > b.Length - offset)
                    throw new InvalidDataException("RVA is outside the file");
                byte[] result = new byte[length];
                Buffer.BlockCopy(b, offset, result, 0, length);
                return result;
            }
            throw new InvalidDataException("RVA is not mapped by a section");
        }

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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out IntPtr returned, IntPtr overlapped);
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
