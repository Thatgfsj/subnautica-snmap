// SNInjector - 深海迷航小地图注入器 (trainer 式, 不随游戏启动)
// 用法: SNInjector.exe [进程名=Subnautica] [--pid=1234] [--watch]
// 流程: 按名字找 PID -> CreateRemoteThread + LoadLibraryW 注入 SNMapBoot.dll
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class SNInjector
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, IntPtr stack, IntPtr start, IntPtr param, uint flags, out IntPtr tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsWow64Process(IntPtr hProc, out bool wow64);

    const uint PROCESS_CREATE_THREAD = 0x0002, PROCESS_QUERY_INFORMATION = 0x0400,
               PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_READ = 0x0010;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_READWRITE = 0x04;

    static int Main(string[] args)
    {
        string procName = "Subnautica";
        int wantPid = -1;
        bool watch = false;
        foreach (string a in args)
        {
            if (a == "--watch") watch = true;
            else if (a.StartsWith("--pid=")) int.TryParse(a.Substring(6), out wantPid);
            else procName = a;
        }

        string here = AppDomain.CurrentDomain.BaseDirectory;
        string bootDll = Path.Combine(here, "SNMapBoot.dll");
        string managedDll = Path.Combine(here, "SNMapManaged.dll");
        string mapPng = Path.Combine(here, "map.png");
        Console.WriteLine("== SNInjector 深海迷航小地图注入器 ==");
        if (!File.Exists(bootDll) || !File.Exists(managedDll) || !File.Exists(mapPng))
        {
            Console.WriteLine("[X] 缺少文件: SNMapBoot.dll / SNMapManaged.dll / map.png 必须与本程序同目录");
            return 1;
        }

        Process target = null;
        int waited = 0;
        Console.Write("按进程名 [" + procName + "] 查找游戏进程");
        while (true)
        {
            target = FindNewest(procName, wantPid);
            if (target != null) break;
            Console.Write(".");
            Thread.Sleep(2000);
            waited += 2;
            if (!watch && waited >= 180)
            {
                Console.WriteLine();
                Console.WriteLine("[X] 等待超时: 没找到游戏进程。先启动游戏再运行本程序, 或用 SNInjector.exe --watch 挂机等待");
                return 2;
            }
        }
        Console.WriteLine();
        Console.WriteLine("[OK] 找到 " + target.ProcessName + ".exe  PID=" + target.Id);

        try
        {
            bool wow64;
            IsWow64Process(target.Handle, out wow64);
            if (wow64) Console.WriteLine("[!] 游戏是 32 位进程, 本注入器只支持 64 位");
        }
        catch (Exception) { }

        try
        {
            Inject(target.Id, bootDll);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[X] 注入失败: " + ex.Message);
            return 4;
        }
        Console.WriteLine("[OK] 注入完成, 等待游戏内初始化...");
        Thread.Sleep(4000);
        string logPath = Path.Combine(here, "SNMap.log");
        if (File.Exists(logPath))
        {
            string content = "";
            try { content = File.ReadAllText(logPath); } catch (Exception) { }
            if (content.Contains("installed (main thread ok)"))
            {
                Console.WriteLine("[OK] SNMap 已在游戏内加载! F9 开关大地图, F7 开关左上角坐标");
                return 0;
            }
        }
        Console.WriteLine("[..] 注入完成。若游戏内没反应: 点一下游戏窗口, 或稍等后重跑本程序; 详见 SNMap.log / 使用说明.txt");
        return 0;
    }

    static Process FindNewest(string name, int wantPid)
    {
        Process best = null;
        DateTime bestT = DateTime.MinValue;
        Process[] list = Process.GetProcessesByName(name);
        foreach (Process p in list)
        {
            if (wantPid >= 0 && p.Id != wantPid) continue;
            DateTime t = DateTime.MinValue;
            try { t = p.StartTime; } catch (Exception) { }
            if (t >= bestT) { bestT = t; best = p; }
        }
        return best;
    }

    static void Inject(int pid, string dllPath)
    {
        IntPtr h = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                               PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
            throw new Exception("OpenProcess 失败 (GetLastError=" + Marshal.GetLastWin32Error() + ")");
        try
        {
            IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
            IntPtr loadLib = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLib == IntPtr.Zero) throw new Exception("找不到 kernel32!LoadLibraryW");

            byte[] bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            IntPtr remote = VirtualAllocEx(h, IntPtr.Zero, (IntPtr)bytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero) throw new Exception("VirtualAllocEx 失败");
            IntPtr written;
            if (!WriteProcessMemory(h, remote, bytes, (IntPtr)bytes.Length, out written))
                throw new Exception("WriteProcessMemory 失败");
            IntPtr tid;
            IntPtr th = CreateRemoteThread(h, IntPtr.Zero, IntPtr.Zero, loadLib, remote, 0, out tid);
            if (th == IntPtr.Zero)
                throw new Exception("CreateRemoteThread 失败 (GetLastError=" + Marshal.GetLastWin32Error() + ")");
            WaitForSingleObject(th, 15000);
            uint exitCode;
            GetExitCodeThread(th, out exitCode);
            CloseHandle(th);
            if (exitCode == 0) throw new Exception("远程 LoadLibrary 返回 0 (DLL 加载失败)");
        }
        finally { CloseHandle(h); }
    }
}
