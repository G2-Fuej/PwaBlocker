using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class PvpAliveHarness
{
    private const uint DONT_RESOLVE_DLL_REFERENCES = 0x00000001;
    private static readonly string[] Exports = {
        "postEvent", "debugShowInfo", "connectHost",
        "startInstance", "stopInstance", "initMatchInfo"
    };

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: harness <dll> <stop-file>"); return 2; }
        string dll = Path.GetFullPath(args[0]);
        string stop = Path.GetFullPath(args[1]);
        IntPtr module = LoadLibraryEx(dll, IntPtr.Zero, DONT_RESOLVE_DLL_REFERENCES);
        if (module == IntPtr.Zero) { Console.Error.WriteLine("LOAD_ERROR=" + Marshal.GetLastWin32Error()); return 3; }

        try
        {
            var addresses = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            var previous = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Exports)
            {
                IntPtr address = GetProcAddress(module, name);
                if (address == IntPtr.Zero) { Console.Error.WriteLine("MISSING=" + name); return 4; }
                byte value = Marshal.ReadByte(address);
                addresses[name] = address;
                previous[name] = value;
                Console.WriteLine("BASE {0} address=0x{1:X} byte={2:X2}", name, address.ToInt64(), value);
            }

            int pid = Process.GetCurrentProcess().Id;
            Console.WriteLine("READY pid={0} module=0x{1:X}", pid, module.ToInt64());
            Console.Out.Flush();
            DateTime deadline = DateTime.UtcNow.AddMinutes(3);
            while (!File.Exists(stop) && DateTime.UtcNow < deadline)
            {
                foreach (string name in Exports)
                {
                    byte current = Marshal.ReadByte(addresses[name]);
                    if (current == previous[name]) continue;
                    Console.WriteLine("CHANGE {0} old={1:X2} new={2:X2}", name, previous[name], current);
                    Console.Out.Flush();
                    previous[name] = current;
                }
                Thread.Sleep(25);
            }
            Console.WriteLine("STOP pid={0}", pid);
            Console.WriteLine("EXIT=0");
            Console.Out.Flush();
            return 0;
        }
        finally { FreeLibrary(module); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);
}
