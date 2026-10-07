// SNInjectorGui - 深海迷航小地图注入器 (蓝色主题, 检测到游戏自动注入)
// 自动: 按进程名找 Subnautica -> 自动注入; 找不到时可[选择窗口]手动选 -> 点[注入]兜底
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

internal class PidEntry
{
    public int Pid;
    public DateTime Start;
    public string Label;
}

internal class WinEntry
{
    public IntPtr Hwnd;
    public int Pid;
    public string Title;
    public WinEntry(IntPtr h, int p, string t) { Hwnd = h; Pid = p; Title = t; }
}

public class MainForm : Form
{
    private static readonly Color ThemeBg = Color.FromArgb(232, 241, 252);
    private static readonly Color ThemeAccent = Color.FromArgb(25, 118, 210);
    private static readonly Color ThemeAccentDark = Color.FromArgb(13, 71, 161);
    private static readonly Color ThemeHover = Color.FromArgb(30, 136, 229);
    private static readonly Color ThemeText = Color.FromArgb(21, 60, 100);

    private ListBox listBox;
    private Button btnRefresh;
    private Button btnPick;
    private Button btnInject;
    private Label status;
    private List<PidEntry> entries = new List<PidEntry>();
    private int selectedPid = -1;
    private string selectedDesc = "";
    private bool busy;
    private bool suppressSel;
    private bool userPicked;
    private int lastAutoPid = -1;
    private int successPid = -1;

    public MainForm()
    {
        Text = "深海迷航小地图注入器 SNMap";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 400);
        BackColor = ThemeBg;

        Label title = new Label();
        title.Text = "SNMap · 深海迷航小地图";
        title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        title.ForeColor = ThemeAccentDark;
        title.Location = new Point(14, 10);
        title.AutoSize = true;
        Controls.Add(title);

        Label hint = new Label();
        hint.Text = "启动游戏后会自动注入。检测不到时可[选择窗口]手动选，再点[注入]。";
        hint.ForeColor = ThemeText;
        hint.Location = new Point(16, 44);
        hint.AutoSize = true;
        Controls.Add(hint);

        listBox = new ListBox();
        listBox.Location = new Point(14, 70);
        listBox.Size = new Size(452, 198);
        listBox.SelectedIndexChanged += OnListSel;
        listBox.DoubleClick += delegate { ManualInject(); };
        Controls.Add(listBox);

        btnRefresh = BlueBtn("刷新进程", 14, 276, 108, ThemeAccent);
        btnRefresh.Click += delegate { RefreshList(); };
        Controls.Add(btnRefresh);

        btnPick = BlueBtn("选择窗口...", 130, 276, 108, ThemeAccent);
        btnPick.Click += delegate { PickWindow(); };
        Controls.Add(btnPick);

        btnInject = BlueBtn("注  入", 358, 270, 108, ThemeAccentDark);
        btnInject.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        btnInject.Height = 42;
        btnInject.Click += delegate { ManualInject(); };
        Controls.Add(btnInject);

        status = new Label();
        status.Location = new Point(14, 322);
        status.Size = new Size(452, 72);
        status.ForeColor = ThemeText;
        status.Text = "等待游戏进程启动后将自动注入...";
        Controls.Add(status);

        RefreshList();
        StartTimer();
    }

    private Button BlueBtn(string text, int x, int y, int w, Color c)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(w, 30);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ThemeHover;
        b.FlatAppearance.MouseDownBackColor = ThemeAccentDark;
        b.BackColor = c;
        b.ForeColor = Color.White;
        return b;
    }

    private void SetStatus(string text, Color c)
    {
        status.Text = text;
        status.ForeColor = c;
    }

    private void StartTimer()
    {
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 1500;
        t.Tick += delegate { AutoTick(); };
        t.Start();
    }

    private Process FindNewestGame()
    {
        Process best = null;
        DateTime bt = DateTime.MinValue;
        foreach (string name in new string[] { "Subnautica", "Subnautica32" })
        {
            Process[] list = Process.GetProcessesByName(name);
            for (int i = 0; i < list.Length; i++)
            {
                DateTime st = DateTime.MinValue;
                try { st = list[i].StartTime; } catch (Exception) { }
                if (st >= bt) { bt = st; best = list[i]; }
            }
        }
        return best;
    }

    private void AutoTick()
    {
        if (busy) return;
        Process p = FindNewestGame();
        if (p == null)
        {
            if (!userPicked && selectedPid > 0)
            {
                selectedPid = -1;
                selectedDesc = "";
                suppressSel = true;
                listBox.Items.Clear();
                suppressSel = false;
                SetStatus("等待游戏进程启动后将自动注入...", ThemeText);
            }
            return;
        }
        if (!userPicked)
        {
            selectedPid = p.Id;
            selectedDesc = p.ProcessName + ".exe (自动检测)";
        }
        if (p.Id == successPid)
        {
            SetStatus("[成功] SNMap 已在游戏内加载 (PID " + p.Id + ")\r\nF9 = 全屏大地图(滚轮缩放/±切换图层)    F7 = 圆形小地图(200/300/500)", Color.Green);
            return;
        }
        if (p.Id == lastAutoPid) return;
        lastAutoPid = p.Id;
        DoInject();
    }

    private void FillList()
    {
        entries.Clear();
        foreach (string name in new string[] { "Subnautica", "Subnautica32" })
        {
            Process[] list = Process.GetProcessesByName(name);
            for (int i = 0; i < list.Length; i++)
            {
                PidEntry e = new PidEntry();
                e.Pid = list[i].Id;
                try { e.Start = list[i].StartTime; } catch (Exception) { e.Start = DateTime.MinValue; }
                e.Label = name + ".exe    PID " + e.Pid + "    启动于 " + e.Start.ToString("HH:mm:ss");
                entries.Add(e);
            }
        }
        entries.Sort(delegate(PidEntry a, PidEntry b) { return b.Start.CompareTo(a.Start); });
        suppressSel = true;
        listBox.BeginUpdate();
        listBox.Items.Clear();
        for (int i = 0; i < entries.Count; i++) listBox.Items.Add(entries[i].Label);
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Pid == selectedPid) { listBox.SelectedIndex = i; break; }
        }
        listBox.EndUpdate();
        suppressSel = false;
    }

    private void RefreshList()
    {
        FillList();
        if (entries.Count == 0 && selectedPid <= 0)
            SetStatus("未检测到游戏进程。启动游戏后会自动注入; 或点[选择窗口]手动选择。", ThemeText);
    }

    private void OnListSel(object sender, EventArgs e)
    {
        if (suppressSel) return;
        int idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= entries.Count) return;
        selectedPid = entries[idx].Pid;
        selectedDesc = entries[idx].Label;
        userPicked = true;
        lastAutoPid = selectedPid; // 手动选择后不再抢注入, 由按钮触发
        SetStatus("已选中: " + selectedDesc + "  ->  点[注入]", ThemeText);
    }

    private void PickWindow()
    {
        List<WinEntry> wins = new List<WinEntry>();
        int selfPid = Process.GetCurrentProcess().Id;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if ((int)pid == selfPid || pid == 0) return true;
            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            string title = sb.ToString();
            if (title.Trim().Length == 0) return true;
            wins.Add(new WinEntry(h, (int)pid, title));
            return true;
        }, IntPtr.Zero);

        Form dlg = new Form();
        dlg.Text = "选择游戏窗口";
        dlg.Size = new Size(560, 440);
        dlg.StartPosition = FormStartPosition.CenterParent;
        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
        dlg.MaximizeBox = false;
        dlg.BackColor = ThemeBg;

        ListBox lb = new ListBox();
        lb.Dock = DockStyle.Fill;
        for (int i = 0; i < wins.Count; i++)
            lb.Items.Add(wins[i].Title + "    [PID " + wins[i].Pid + "]");
        dlg.Controls.Add(lb);

        Button ok = BlueBtn("确定", 0, 0, 10, ThemeAccent);
        ok.Dock = DockStyle.Bottom;
        ok.Height = 38;
        ok.Click += delegate
        {
            if (lb.SelectedIndex >= 0) dlg.Tag = wins[lb.SelectedIndex];
            dlg.Close();
        };
        lb.DoubleClick += delegate { ok.PerformClick(); };
        dlg.Controls.Add(ok);

        dlg.ShowDialog(this);
        WinEntry sel = dlg.Tag as WinEntry;
        if (sel == null) return;

        suppressSel = true;
        listBox.ClearSelected();
        suppressSel = false;
        selectedPid = sel.Pid;
        selectedDesc = "窗口[" + sel.Title + "]";
        userPicked = true;
        lastAutoPid = selectedPid;
        SetStatus("已选中: " + selectedDesc + "  (PID " + selectedPid + ")  ->  点[注入]", ThemeText);
    }

    private void ManualInject()
    {
        lastAutoPid = -1;   // 允许之后继续自动注入新的 PID
        DoInject();
    }

    private void DoInject()
    {
        if (busy) return;
        if (selectedPid <= 0)
        {
            SetStatus("请先等待自动检测, 或点[选择窗口]手动选择。", Color.Red);
            return;
        }
        string here = AppDomain.CurrentDomain.BaseDirectory;
        string boot = Path.Combine(here, "SNMapBoot.dll");
        string log = Path.Combine(here, "SNMap.log");
        bool hasManaged = false;
        try { hasManaged = Directory.GetFiles(here, "SNMapManaged*.dll").Length > 0; } catch (Exception) { }
        if (!File.Exists(boot) || !hasManaged)
        {
            SetStatus("缺少 SNMapBoot.dll / SNMapManaged*.dll, 请保持它们与本程序同目录。", Color.Red);
            return;
        }

        try
        {
            bool wow64;
            Process p = Process.GetProcessById(selectedPid);
            IsWow64Process(p.Handle, out wow64);
            if (wow64)
            {
                SetStatus("该进程是 32 位, 本工具只支持 64 位游戏进程。", Color.Red);
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus("无法访问该进程: " + ex.Message + " (游戏可能已退出, 将自动重试新进程)", Color.Red);
            return;
        }

        busy = true;
        btnInject.Enabled = false;
        SetStatus("正在注入 PID " + selectedPid + " ...", ThemeText);
        Refresh();

        try
        {
            string mode = Injector.Run(selectedPid, boot);
            SetStatus(mode == "triggered"
                ? "检测到已注入, 触发热重载 (加载最新 SNMapManaged*)..."
                : "注入完成, 等待游戏内初始化 (约 4 秒)...", ThemeText);
        }
        catch (Exception ex)
        {
            SetStatus("注入失败: " + ex.Message, Color.Red);
            busy = false;
            btnInject.Enabled = true;
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            Thread.Sleep(4000);
            bool ok = false;
            try { ok = File.ReadAllText(log).Contains("installed (main thread ok)"); }
            catch (Exception) { }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    btnInject.Enabled = true;
                    if (ok)
                    {
                        successPid = selectedPid;
                        SetStatus("[成功] SNMap 已在游戏内加载!\r\nF9 = 全屏大地图(滚轮缩放, －/＋切换图层)    F7 = 圆形小地图(200/300/500)", Color.Green);
                    }
                    else
                    {
                        SetStatus("已注入, 但未确认初始化。若游戏内无反应: 重开游戏再试, 并把 SNMapBoot.log / SNMap.log 发给作者。", Color.DarkOrange);
                    }
                });
            }
            catch (Exception) { }
        });
    }

    private delegate bool EnumCb(IntPtr h, IntPtr l);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumCb cb, IntPtr l);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProc, out bool wow64);
}

internal static class Injector
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32W me);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32W me);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    const uint PROCESS_CREATE_THREAD = 0x0002, PROCESS_QUERY_INFORMATION = 0x0400,
               PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_READ = 0x0010;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_READWRITE = 0x04;
    const uint TH32CS_SNAPMODULE = 0x00000008;

    // 返回 "loaded"(首次注入) 或 "triggered"(已注入过, 触发热重载)
    public static string Run(int pid, string dllPath)
    {
        IntPtr h = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                               PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
            throw new Exception("OpenProcess 失败 (GetLastError=" + Marshal.GetLastWin32Error() + ")");
        try
        {
            IntPtr remoteBase = FindRemoteModule(pid, "snmapboot.dll");
            if (remoteBase != IntPtr.Zero)
                return TriggerReload(h, dllPath, remoteBase);

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
            return "loaded";
        }
        finally { CloseHandle(h); }
    }

    private static IntPtr FindRemoteModule(int pid, string lowerName)
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, (uint)pid);
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return IntPtr.Zero;
        try
        {
            MODULEENTRY32W me = new MODULEENTRY32W();
            me.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));
            if (Module32FirstW(snap, ref me))
            {
                do
                {
                    if (me.szModule != null && me.szModule.ToLowerInvariant() == lowerName)
                        return me.modBaseAddr;
                } while (Module32NextW(snap, ref me));
            }
        }
        finally { CloseHandle(snap); }
        return IntPtr.Zero;
    }

    private static string TriggerReload(IntPtr h, string dllPath, IntPtr remoteBase)
    {
        IntPtr local = LoadLibraryW(dllPath);
        if (local == IntPtr.Zero) throw new Exception("本地加载 SNMapBoot.dll 失败");
        try
        {
            IntPtr proc = GetProcAddress(local, "SNMapTrigger");
            if (proc == IntPtr.Zero) throw new Exception("SNMapBoot.dll 缺少 SNMapTrigger 导出");
            long rva = proc.ToInt64() - local.ToInt64();
            IntPtr target = (IntPtr)(remoteBase.ToInt64() + rva);
            IntPtr tid;
            IntPtr th = CreateRemoteThread(h, IntPtr.Zero, IntPtr.Zero, target, IntPtr.Zero, 0, out tid);
            if (th == IntPtr.Zero)
                throw new Exception("CreateRemoteThread(trigger) 失败 (GetLastError=" + Marshal.GetLastWin32Error() + ")");
            WaitForSingleObject(th, 10000);
            CloseHandle(th);
            return "triggered";
        }
        finally { FreeLibrary(local); }
    }
}
