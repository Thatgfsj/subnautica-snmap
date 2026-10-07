// SNInjectorGui - 深海迷航小地图注入器 (图形界面版)
// 运行后: 自动检测 Subnautica 进程 -> 选中(或点[选择窗口]手动选) -> 点[注入]
// 注入成功后游戏内: F9=全屏大地图  F7=圆形小地图(200/300/500循环)
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

    public MainForm()
    {
        Text = "深海迷航小地图注入器";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(470, 384);

        Label hint = new Label();
        hint.Text = "① 启动游戏   ② 选中游戏进程(检测不到就点[选择窗口])   ③ 点[注入]";
        hint.Location = new Point(12, 12);
        hint.AutoSize = true;
        Controls.Add(hint);

        listBox = new ListBox();
        listBox.Location = new Point(12, 36);
        listBox.Size = new Size(446, 196);
        listBox.SelectedIndexChanged += OnListSel;
        listBox.DoubleClick += delegate { DoInject(); };
        Controls.Add(listBox);

        btnRefresh = NewBtn("刷新进程", 12, 244, 110);
        btnRefresh.Click += delegate { RefreshList(); };
        Controls.Add(btnRefresh);

        btnPick = NewBtn("选择窗口...", 130, 244, 110);
        btnPick.Click += delegate { PickWindow(); };
        Controls.Add(btnPick);

        btnInject = NewBtn("注  入", 348, 238, 110);
        btnInject.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        btnInject.BackColor = Color.FromArgb(225, 240, 255);
        btnInject.Height = 42;
        btnInject.Click += delegate { DoInject(); };
        Controls.Add(btnInject);

        status = new Label();
        status.Location = new Point(12, 292);
        status.Size = new Size(446, 84);
        status.ForeColor = Color.DimGray;
        status.Text = "正在检测游戏进程...";
        Controls.Add(status);

        RefreshList();
        StartTimer();
    }

    private Button NewBtn(string text, int x, int y, int w)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(w, 30);
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
        t.Interval = 3000;
        t.Tick += delegate { if (!busy) RefreshList(); };
        t.Start();
    }

    private void AddProcs(string name)
    {
        Process[] list = Process.GetProcessesByName(name);
        for (int i = 0; i < list.Length; i++)
        {
            Process p = list[i];
            PidEntry e = new PidEntry();
            e.Pid = p.Id;
            try { e.Start = p.StartTime; } catch (Exception) { e.Start = DateTime.MinValue; }
            e.Label = name + ".exe    PID " + p.Id + "    启动于 " + e.Start.ToString("HH:mm:ss");
            entries.Add(e);
        }
    }

    private void RefreshList()
    {
        entries.Clear();
        AddProcs("Subnautica");
        AddProcs("Subnautica32");
        entries.Sort(delegate(PidEntry a, PidEntry b) { return b.Start.CompareTo(a.Start); });

        suppressSel = true;
        listBox.BeginUpdate();
        listBox.Items.Clear();
        for (int i = 0; i < entries.Count; i++) listBox.Items.Add(entries[i].Label);
        listBox.EndUpdate();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Pid == selectedPid) { listBox.SelectedIndex = i; break; }
        }
        suppressSel = false;

        if (selectedPid > 0)
            SetStatus("已选中: " + selectedDesc + "  (PID " + selectedPid + ")  ->  点[注入]", Color.Black);
        else if (entries.Count > 0)
            SetStatus("检测到游戏进程, 已自动选中最新启动的。确认后点[注入]。", Color.Black);
        else
            SetStatus("未检测到 Subnautica 进程。请先启动游戏(会自动刷新); 若已在运行请点[选择窗口]手动选。", Color.DimGray);
    }

    private void OnListSel(object sender, EventArgs e)
    {
        if (suppressSel) return;
        int idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= entries.Count) return;
        selectedPid = entries[idx].Pid;
        selectedDesc = entries[idx].Label;
        SetStatus("已选中: " + selectedDesc + "  ->  点[注入]", Color.Black);
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
        dlg.Text = "选择游戏窗口 (点中游戏画面所在的那个)";
        dlg.Size = new Size(560, 440);
        dlg.StartPosition = FormStartPosition.CenterParent;
        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
        dlg.MaximizeBox = false;

        ListBox lb = new ListBox();
        lb.Dock = DockStyle.Fill;
        for (int i = 0; i < wins.Count; i++)
            lb.Items.Add(wins[i].Title + "    [PID " + wins[i].Pid + "]");
        dlg.Controls.Add(lb);

        Button ok = new Button();
        ok.Text = "确定";
        ok.Dock = DockStyle.Bottom;
        ok.Height = 36;
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
        SetStatus("已选中: " + selectedDesc + "  (PID " + selectedPid + ")  ->  点[注入]", Color.Black);
    }

    private void DoInject()
    {
        if (busy) return;
        if (selectedPid <= 0)
        {
            SetStatus("请先在列表选中游戏进程, 或点[选择窗口]手动选择。", Color.Red);
            return;
        }
        string here = AppDomain.CurrentDomain.BaseDirectory;
        string boot = Path.Combine(here, "SNMapBoot.dll");
        string managed = Path.Combine(here, "SNMapManaged.dll");
        string map = Path.Combine(here, "map.png");
        string log = Path.Combine(here, "SNMap.log");
        if (!File.Exists(boot) || !File.Exists(managed) || !File.Exists(map))
        {
            SetStatus("缺少 SNMapBoot.dll / SNMapManaged.dll / map.png, 请保持它们与本程序同目录。", Color.Red);
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
            SetStatus("无法访问该进程: " + ex.Message + " (游戏可能已退出, 请刷新)", Color.Red);
            return;
        }

        busy = true;
        btnInject.Enabled = false;
        SetStatus("正在注入 PID " + selectedPid + " ...", Color.Black);
        Refresh();

        try
        {
            Injector.Run(selectedPid, boot);
        }
        catch (Exception ex)
        {
            SetStatus("注入失败: " + ex.Message, Color.Red);
            busy = false;
            btnInject.Enabled = true;
            return;
        }

        SetStatus("注入完成, 等待游戏内初始化 (约 4 秒)...", Color.Black);
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
                    if (ok)
                        SetStatus("[成功] SNMap 已在游戏内加载!\r\n游戏内: F9 = 全屏大地图    F7 = 圆形小地图(200/300/500循环)", Color.Green);
                    else
                        SetStatus("已注入, 但未确认初始化。若游戏内无反应: 重开游戏后再试, 并把 SNMapBoot.log / SNMap.log 发给作者。", Color.DarkOrange);
                    busy = false;
                    btnInject.Enabled = true;
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

    const uint PROCESS_CREATE_THREAD = 0x0002, PROCESS_QUERY_INFORMATION = 0x0400,
               PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_READ = 0x0010;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_READWRITE = 0x04;

    public static void Run(int pid, string dllPath)
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
