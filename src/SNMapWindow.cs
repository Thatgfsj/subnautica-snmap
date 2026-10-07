// SNMapWindow - 深海迷航独立大地图窗口 v2.0
// 数据: 轮询游戏模块写的 SNMapState.bin (20Hz); 设置通过 SNMapSettings.ini 双向交换
// 渲染: mip 金字塔预缩放(参考 Xaero 式预合成), 每帧只做块拷贝
// 交互: 滚轮缩放 / 左键拖拽 / [－][＋]切换图层 / 跟随玩家 / 置顶 / F9 显示隐藏(由模块写标志)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SNMap;

static class Program
{
    public static bool AutoStart;   // --auto: 由注入流程拉起, 初始隐藏待命(F9 显示)

    [STAThread]
    static void Main()
    {
        // 单实例: 自动清掉多余的旧窗口进程
        Process cur = Process.GetCurrentProcess();
        foreach (Process p in Process.GetProcessesByName("SNMapWindow"))
        {
            if (p.Id != cur.Id) { try { p.Kill(); } catch (Exception) { } }
        }
        foreach (string a in Environment.GetCommandLineArgs())
        {
            if (a == "--auto") AutoStart = true;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MapForm());
    }
}

internal class MapLayer
{
    public string Name;
    public string Path;
    public float MinX = -2000f, MaxX = 2000f, MinZ = -2000f, MaxZ = 2000f;
    public bool Calibrated = true;
    public Image[] Mips;        // 预缩放金字塔(由大到小), 载入后释放原图
    public float[] MipPxPerM;
}

internal class Beacon
{
    public float X, Z;
    public int Color;
    public string Label;
    public string Key;    // 图标键(icons/<Key>.png); 空=画彩色点
    public int Kind;      // 0=信标 1=扫描室扫描信号
}

internal class CreaturePt
{
    public float X, Z;
    public string Label;
    public string Key;    // 图标键(生物 TechType); 空=画红三角
}

public class MapForm : Form
{
    private const int OffMagic = 0, OffVersion = 4, OffTick = 8;
    private const int OffPlayerValid = 16, OffX = 20, OffY = 24, OffZ = 28, OffHeading = 32;
    private const int OffShowWindow = 36, OffModVerLen = 40, OffModVer = 44;
    private const int OffBeaconCount = 80, OffBiomeLen = 84, OffBiome = 88;
    private const int OffBeacons = 128;
    private const int OffCreatureCount = 4268, OffCreatures = 4272;
    private const int Magic = Proto.Magic, ProtoVersion = Proto.Version;

    private static readonly Color ThemeBg = Color.FromArgb(232, 241, 252);
    private static readonly Color ThemeAccent = Color.FromArgb(25, 118, 210);
    private static readonly Color ThemeAccentDark = Color.FromArgb(13, 71, 161);
    private static readonly Color ThemeHover = Color.FromArgb(30, 136, 229);
    private static readonly Color ThemeText = Color.FromArgb(21, 60, 100);
    private static readonly Color MapBg = Color.FromArgb(10, 12, 18);

    private List<MapLayer> layers = new List<MapLayer>();
    private int layerIdx;
    private float zoom = 1f;
    private float viewCX, viewCZ;
    private bool follow = true;
    private bool dragging;
    private Point lastMouse;
    private bool hasGame;
    private float px, py, pz, heading;
    private string biome = "";
    private string modVer = "?";
    private List<Beacon> beacons = new List<Beacon>();
    private readonly List<CreaturePt> creatureWin = new List<CreaturePt>();
    private DateTime settingsStamp;
    private int noStateTicks;
    private int tickCount;
    private bool exiting;

    // 脏检查: 状态没变化就不重绘
    private float lastPx = float.MinValue, lastPz = float.MinValue, lastHeading = float.MinValue;
    private int lastBeaconKey = -1, lastCreatureKey = -1;
    private bool lastHasGame;
    private string lastBiome = "";

    private Button btnPrev, btnNext, btnExit, btnSettings;
    private CheckBox chkFollow, chkTop;
    private Label lblLayer, lblInfo;
    private Font mapFont, smallFont;
    private Keys toggleKey = Keys.F9;   // 取自 config.ini 的 ToggleMapKey

    // 设置窗口用的状态(与 SNMapSettings.ini 同步)
    private bool showCreatures = true;
    private bool showScanSignals = true;
    private int minimapPixels = 360;
    private readonly List<string> species = new List<string>();   // 模块见过的敌对物种(TechType 名)
    private SNMapSettingsForm settingsDlg;
    private byte[] stateReadBuf;                                  // 复用: 不再每 tick new 8KB
    private int wantShowByte = -1;                                // 上一次看到的"模块期望显示"字节
    private readonly List<Beacon> beaconFree = new List<Beacon>();       // 复用池(不参与绘制)
    private readonly List<CreaturePt> creatureFree = new List<CreaturePt>();

    private readonly Pen beaconPen = new Pen(Color.FromArgb(200, 10, 10, 10), 2f);
    private readonly Pen scalePen = new Pen(Color.White, 3f);
    private readonly SolidBrush arrowFill = new SolidBrush(Color.FromArgb(235, 255, 70, 70));
    private readonly Pen arrowPen = new Pen(Color.FromArgb(220, 10, 10, 10), 2f);
    private readonly SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
    private readonly SolidBrush mapBgBrush = new SolidBrush(MapBg);
    private readonly SolidBrush creatureBrush = new SolidBrush(Color.FromArgb(225, 255, 40, 40));
    private readonly SolidBrush alertBrush = new SolidBrush(Color.FromArgb(240, 235, 30, 30));
    private Font alertFont;                       // 没头像的生物用它画"!"
    private SolidBrush[] pingBrushes;

    private static readonly Color[] PingColors = new Color[]
    {
        Color.White, Color.FromArgb(255, 90, 90), Color.FromArgb(255, 178, 51),
        Color.FromArgb(242, 242, 77), Color.FromArgb(102, 255, 115),
        Color.FromArgb(89, 229, 255), Color.FromArgb(115, 140, 255),
        Color.FromArgb(255, 128, 255)
    };

    private static readonly string[] Headings = new string[]
    {
        "N 北", "NE 东北", "E 东", "SE 东南", "S 南", "SW 西南", "W 西", "NW 西北"
    };

    public MapForm()
    {
        Text = "深海迷航大地图 SNMap";
        ClientSize = new Size(940, 920);
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;   // 默认全屏(可还原)
        MaximizeBox = true;
        BackColor = ThemeBg;
        DoubleBuffered = true;

        mapFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        smallFont = new Font("Microsoft YaHei UI", 9f);

        btnPrev = BlueBtn("－", 8, 8, 40, 30);
        btnNext = BlueBtn("＋", 306, 8, 40, 30);
        btnExit = BlueBtn("退出", ClientSize.Width - 78, 8, 70, 30);
        btnExit.Anchor = AnchorStyles.Top | AnchorStyles.Right;   // 最大化后仍贴右上角
        // 原先只创建了按钮, 忘了挂 Click, 所以点了没反应
        btnExit.Click += delegate
        {
            exiting = true;                          // 让 FormClosing 不再拦截
            WriteSettingsKey("ShowWindow", "0");     // 与模块内的显示状态保持一致
            Close();
        };
        lblLayer = new Label();
        lblLayer.Location = new Point(56, 13);
        lblLayer.Size = new Size(242, 24);
        lblLayer.ForeColor = ThemeAccentDark;
        lblLayer.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        lblLayer.TextAlign = ContentAlignment.MiddleCenter;

        chkFollow = new CheckBox();
        chkFollow.Text = "跟随";
        chkFollow.Checked = true;
        chkFollow.Location = new Point(8, 44);
        chkFollow.AutoSize = true;
        chkFollow.CheckedChanged += delegate { follow = chkFollow.Checked; };
        Controls.Add(chkFollow);

        chkTop = new CheckBox();
        chkTop.Text = "置顶";
        chkTop.Checked = true;
        chkTop.Location = new Point(78, 44);
        chkTop.AutoSize = true;
        chkTop.CheckedChanged += delegate { TopMost = chkTop.Checked; };
        Controls.Add(chkTop);

        // 小地图大小 / 敌对生物总开关 / 扫描目标开关 / 按物种显示隐藏, 全部收进【设置】窗口
        btnSettings = BlueBtn("设置", 140, 41, 60, 26);
        btnSettings.Click += delegate { OpenSettings(); };
        Controls.Add(btnSettings);

        lblInfo = new Label();
        lblInfo.Location = new Point(212, 46);
        lblInfo.AutoSize = true;
        lblInfo.ForeColor = ThemeAccentDark;
        lblInfo.Font = smallFont;
        Controls.Add(lblInfo);

        Controls.Add(btnPrev);
        Controls.Add(btnNext);
        Controls.Add(btnExit);
        Controls.Add(btnSettings);
        Controls.Add(lblLayer);

        LoadLayers();
        if (layers.Count > 0)
        {
            MapLayer l0 = Layer();
            lblLayer.Text = l0.Name + "  (" + (layerIdx + 1) + "/" + layers.Count + ")" + (l0.Calibrated ? "" : "  [仅浏览]");
        }

        FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            if (!exiting)
            {
                e.Cancel = true;
                Hide();
                WriteSettingsKey("ShowWindow", "0");
            }
        };

        // "置顶"默认勾选, 但原来只赋了 chkTop.Checked 没落到窗口属性上(事件是赋值之后才挂的), 所以窗口其实不在最前
        TopMost = chkTop.Checked;

        toggleKey = LoadToggleKey();
        KeyPreview = true;
        KeyDown += new KeyEventHandler(OnFormKeyDown);

        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 33;
        t.Tick += delegate { Tick(); };
        t.Start();

        // --auto(引导层注入后拉起) = 待命隐藏, F9 才显示: 必须主动写回 0,
        // 否则会被上次遗留的 ShowWindow=1 顶成"注入就弹图"(用户 21:25 遇到的就是这种)
        // 手动双击启动 = 玩家想立刻看图: 写回 1
        WriteSettingsKey("ShowWindow", Program.AutoStart ? "0" : "1");
    }

    private Button BlueBtn(string text, int x, int y, int w, int h)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(w, h);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ThemeHover;
        b.FlatAppearance.MouseDownBackColor = ThemeAccentDark;
        b.BackColor = ThemeAccent;
        b.ForeColor = Color.White;
        return b;
    }

    // ------------------------------------------------------------- layers

    private MapLayer Layer()
    {
        if (layers.Count == 0) return null;
        return layers[ClampI(layerIdx, 0, layers.Count - 1)];
    }

    private static int ClampI(int v, int a, int b)
    {
        return v < a ? a : (v > b ? b : v);
    }

    private void LoadLayers()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string mapsDir = Path.Combine(dir, "maps");
        float defRange = 2000f;
        try
        {
            string cfg = Path.Combine(dir, "config.ini");
            if (File.Exists(cfg))
            {
                foreach (string ln in File.ReadAllLines(cfg))
                {
                    string l = ln.Trim();
                    if (l.StartsWith("WorldRange=", StringComparison.OrdinalIgnoreCase))
                    {
                        float.TryParse(l.Substring(11).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out defRange);
                    }
                }
            }
        }
        catch (Exception) { }

        if (Directory.Exists(mapsDir))
        {
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(mapsDir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") files.Add(f);
            }
            files.Sort(StringComparer.Ordinal);
            Dictionary<string, float[]> ini = Proto.ParseMapsIni(Path.Combine(mapsDir, "maps.ini"));
            for (int i = 0; i < files.Count; i++)
            {
                MapLayer L = new MapLayer();
                L.Path = files[i];
                L.Name = Path.GetFileNameWithoutExtension(files[i]);
                int us = L.Name.IndexOf('_');
                if (us == 2 || us == 3)
                {
                    int num;
                    if (int.TryParse(L.Name.Substring(0, us), out num)) L.Name = L.Name.Substring(us + 1);
                }
                float[] b;
                if (ini.TryGetValue(Path.GetFileName(files[i]), out b) && b.Length >= 4)
                {
                    L.MinX = b[0]; L.MaxX = b[1]; L.MinZ = b[2]; L.MaxZ = b[3];
                    L.Calibrated = true;
                }
                else
                {
                    L.MinX = -defRange; L.MaxX = defRange; L.MinZ = -defRange; L.MaxZ = defRange;
                    try
                    {
                        using (Image probe = Image.FromFile(files[i]))
                        {
                            float ar = (float)probe.Width / probe.Height;
                            L.Calibrated = ar > 0.9f && ar < 1.1f;
                        }
                    }
                    catch (Exception) { L.Calibrated = false; }
                }
                layers.Add(L);
            }
        }
        if (layers.Count == 0)
        {
            string legacy = Path.Combine(dir, "map.png");
            if (File.Exists(legacy))
            {
                MapLayer L = new MapLayer();
                L.Path = legacy;
                L.Name = "主地图";
                layers.Add(L);
            }
        }
        viewCX = 0; viewCZ = 0;
        // 启动就把当前图层写回设置文件: 模块的小地图跟随这个索引(也决定它去加载哪个 SNMapMini_*.jpg 预览)
        WriteSettingsKey(Proto.KeyWindowLayer, layerIdx.ToString());
        WinLog("start auto=" + Program.AutoStart + " layers=" + layers.Count + " layer=" + layerIdx +
               " minimap=" + minimapPixels + " creatures=" + showCreatures + " scan=" + showScanSignals +
               " from=" + AppDomain.CurrentDomain.BaseDirectory);
    }

    // mip 金字塔: 原图 -> 2048 -> 1024 -> ... 一次性预缩放, 之后每帧只做块拷贝
    // 2048 起点: 整图视野只要 ~4.7m/px, 4096 起点白占 60MB 内存(这台机器内存不稳, 宁可省)
    private const int MipStartCap = 2048;
    // 给游戏内模块导出的小预览边长(模块拿它当小地图纹理, 免得在游戏进程里解码 40~256MB 的原图)
    private const int MiniPreviewSide = 3072;

    private void EnsureMips(MapLayer L)
    {
        if (L.Mips != null || !File.Exists(L.Path)) return;
        try
        {
            using (Image orig = Image.FromFile(L.Path))
            {
                ExportMiniPreview(L, orig);
                int maxDim = Math.Max(orig.Width, orig.Height);
                int startW = Math.Max(16, orig.Width * Math.Min(maxDim, MipStartCap) / maxDim);
                int startH = Math.Max(16, orig.Height * Math.Min(maxDim, MipStartCap) / maxDim);
                List<Image> mips = new List<Image>();
                List<float> scales = new List<float>();
                Image prev = new Bitmap(orig, startW, startH);
                mips.Add(prev);
                scales.Add((L.MaxX - L.MinX) / prev.Width);
                while (mips.Count < 5 && prev.Width / 2 >= 128 && prev.Height / 2 >= 128)
                {
                    Image nxt = new Bitmap(prev.Width / 2, prev.Height / 2);
                    using (Graphics g = Graphics.FromImage(nxt))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(prev, new Rectangle(0, 0, nxt.Width, nxt.Height));
                    }
                    mips.Add(nxt);
                    scales.Add((L.MaxX - L.MinX) / nxt.Width);
                    prev = nxt;
                }
                L.Mips = mips.ToArray();
                L.MipPxPerM = scales.ToArray();
            }
        }
        catch (Exception)
        {
            L.Mips = null;
        }
    }

    // 给游戏内模块导出小预览(同一张图的缩小版): 模块的小地图只要一个圆,
    // 没必要让它在游戏进程里解码 3240²/8192² 的原图(40~256MB, 切图层还要重来)
    private void ExportMiniPreview(MapLayer L, Image orig)
    {
        try
        {
            string outp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SNMapMini_" + layerIdx + ".jpg");
            if (File.Exists(outp) && File.GetLastWriteTimeUtc(outp) >= File.GetLastWriteTimeUtc(L.Path)) return;
            int md = Math.Max(orig.Width, orig.Height);
            int side = Math.Min(MiniPreviewSide, md);
            int w = Math.Max(1, orig.Width * side / md);
            int h = Math.Max(1, orig.Height * side / md);
            using (Bitmap bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(orig, new Rectangle(0, 0, w, h));
                }
                ImageCodecInfo jpg = null;
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                {
                    if (c.FormatID == ImageFormat.Jpeg.Guid) { jpg = c; break; }
                }
                if (jpg != null)
                {
                    using (EncoderParameters ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        bmp.Save(outp, jpg, ep);
                    }
                }
                else bmp.Save(outp, ImageFormat.Jpeg);
                WinLog("preview exported " + Path.GetFileName(outp) + " " + w + "x" + h);
            }
        }
        catch (Exception) { }
    }

    private void SwitchLayer(int delta)
    {
        if (layers.Count == 0) return;
        MapLayer old = Layer();
        layerIdx = (layerIdx + delta + layers.Count) % layers.Count;
        MapLayer nw = Layer();
        if (old != null && old != nw && old.Mips != null)
        {
            foreach (Image m in old.Mips) m.Dispose();
            old.Mips = null;
            old.MipPxPerM = null;
        }
        lblLayer.Text = nw.Name + "  (" + (layerIdx + 1) + "/" + layers.Count + ")" + (nw.Calibrated ? "" : "  [仅浏览]");
        WriteSettingsKey("WindowLayer", layerIdx.ToString());
        Invalidate();
    }

    // ------------------------------------------------------------- settings file

    private string SettingsPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Proto.SettingsFileName);
    }

    private void WriteSettingsKey(string key, string val)
    {
        // 重试几次: 模块每 100ms 也会读这个文件, 撞上共享冲突时静默失败过一次,
        // 玩家看到的就是"某个设置怎么都不生效"
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string p = SettingsPath();
                Dictionary<string, string> d = Proto.ParseIniStrings(p);
                d[key] = val;
                List<string> outLines = new List<string>();
                foreach (KeyValuePair<string, string> kv in d) outLines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(p, outLines.ToArray());
                settingsStamp = DateTime.MinValue;
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2) WinLog("write settings failed: " + key + "=" + val + " (" + ex.Message + ")");
                System.Threading.Thread.Sleep(15);
            }
        }
    }

    // 窗口自己的日志(模块那份是 SNMap.log, 这份是 SNMapWindow.log):
    // 之前"按 F9 打不开大地图"就是因为窗口侧完全没有可查的记录
    private static void WinLog(string msg)
    {
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SNMapWindow.log");
            if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Delete(p);
            File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine);
        }
        catch (Exception) { }
    }

    // 设置窗口(非模态, 打开时可继续看图; 再次点【设置】会把窗口提到前面)
    private void OpenSettings()
    {
        if (settingsDlg == null || settingsDlg.IsDisposed)
        {
            settingsDlg = new SNMapSettingsForm(this);
            settingsDlg.Show(this);
        }
        else
        {
            if (!settingsDlg.Visible) settingsDlg.Show();
            settingsDlg.BringToFront();
            settingsDlg.Activate();
        }
    }

    // 供设置窗口读写(值都落在 SNMapSettings.ini 上, 模块 3 帧就轮询一次, 实时生效)
    public int MinimapPixels
    {
        get { return minimapPixels; }
        set
        {
            minimapPixels = ClampI(value, 120, 800);
            WriteSettingsKey(Proto.KeyMinimapPixels, minimapPixels.ToString());
        }
    }

    public bool ShowCreaturesSetting
    {
        get { return showCreatures; }
        set
        {
            showCreatures = value;
            WriteSettingsKey(Proto.KeyShowCreatures, value ? "1" : "0");
        }
    }

    public bool ShowScanSignalsSetting
    {
        get { return showScanSignals; }
        set
        {
            showScanSignals = value;
            WriteSettingsKey(Proto.KeyShowScanSignals, value ? "1" : "0");
        }
    }

    // 物种清单 = 内置全量表 ∪ 模块报回来的(按 TechType 名去重, 名字带中文注释的用中文显示)
    public List<string> SpeciesNames()
    {
        List<string> res = new List<string>();
        for (int i = 0; i < BuiltinSpecies.Length; i++) res.Add(BuiltinSpecies[i][0]);
        for (int i = 0; i < species.Count; i++)
        {
            if (!res.Contains(species[i])) res.Add(species[i]);
        }
        return res;
    }

    public string SpeciesLabel(string techType)
    {
        for (int i = 0; i < BuiltinSpecies.Length; i++)
        {
            if (BuiltinSpecies[i][0] == techType) return techType + "  " + BuiltinSpecies[i][1];
        }
        return techType;
    }

    // 当前勾选"要显示"的物种; 没有该设置时回落到 config.ini 白名单(与模块逻辑一致)
    public List<string> CurrentShownSpecies()
    {
        List<string> res = new List<string>();
        try
        {
            Dictionary<string, string> d = Proto.ParseIniStrings(SettingsPath());
            string s;
            if (d.TryGetValue(Proto.KeyCreatureShow, out s))
            {
                string t = s.Trim();
                if (t == "*")
                {
                    res.AddRange(SpeciesNames());
                    return res;
                }
                string[] parts = t.Split(new char[] { ',', ';' });
                for (int i = 0; i < parts.Length; i++)
                {
                    string nm = parts[i].Trim();
                    if (nm.Length > 0 && !res.Contains(nm)) res.Add(nm);
                }
                return res;
            }
        }
        catch (Exception) { }
        // 回落: config.ini 的 CreatureWhitelist / 模块默认白名单
        List<string> dw = new List<string>();
        try
        {
            string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            Dictionary<string, string> cd = Proto.ParseIniStrings(cfg);
            string cs;
            if (cd.TryGetValue("CreatureWhitelist", out cs))
            {
                string[] parts = cs.Split(new char[] { ',', ';' });
                for (int i = 0; i < parts.Length; i++)
                {
                    string nm = parts[i].Trim();
                    if (nm.Length > 0 && !dw.Contains(nm)) dw.Add(nm);
                }
                return dw;
            }
        }
        catch (Exception) { }
        for (int i = 0; i < BuiltinSpecies.Length; i++)
        {
            if (BuiltinSpecies[i].Length > 2 && BuiltinSpecies[i][2] == "1") dw.Add(BuiltinSpecies[i][0]);
        }
        return dw;
    }

    public void WriteShownSpecies(List<string> shown)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < shown.Count; i++)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(shown[i]);
        }
        WriteSettingsKey(Proto.KeyCreatureShow, sb.ToString());
    }

    // 内置敌对生物全量表: [TechType 名, 中文, 默认是否显示("1"=大型默认开)]
    private static readonly string[][] BuiltinSpecies = new string[][]
    {
        new string[] { "BoneShark", "骨鲨", "1" },
        new string[] { "Sandshark", "沙鲨", "1" },
        new string[] { "Stalker", "潜行者", "1" },
        new string[] { "Crabsnake", "蟹蛇", "1" },
        new string[] { "CrabSquid", "蟹鱿", "1" },
        new string[] { "Warper", "织命者", "1" },
        new string[] { "Shocker", "电鳗", "1" },
        new string[] { "SpineEel", "暗礁鳗", "1" },
        new string[] { "ReaperLeviathan", "死神利维坦", "1" },
        new string[] { "GhostLeviathan", "幽灵利维坦", "1" },
        new string[] { "GhostLeviatanVoid", "虚空幽灵利维坦", "1" },
        new string[] { "SeaDragon", "海龙利维坦", "1" },
        new string[] { "Crash", "自爆鱼", "0" },
        new string[] { "Biter", "咬咬鱼", "0" },
        new string[] { "Blighter", "盲鳗", "0" },
        new string[] { "CaveCrawler", "洞穴爬行者", "0" },
        new string[] { "Mesmer", "催眠鱼", "0" },
        new string[] { "LavaLizard", "熔岩蜥蜴", "0" },
        new string[] { "LavaLarva", "熔岩幼虫", "0" },
        new string[] { "Bleeder", "水蛭", "0" }
    };

    // ------------------------------------------------------------- state read + tick

    private void Tick()
    {
        ReadStateFile();

        tickCount++;
        if (tickCount % 3 == 0) PollSettings();

        if (follow && hasGame)
        {
            viewCX = px;
            viewCZ = pz;
            ClampView();
        }
        lblInfo.Text = hasGame
            ? string.Format("模块v{0} | X {1:F0}   Z {2:F0}   深度 {3:F0}m   {4}   {5}", modVer, px, pz, py, Heading8(heading), biome)
            : (modVer.StartsWith("协议") ? modVer : "等待游戏数据 (启动游戏并注入 SNMap 后自动连接)");

        if (Visible)
        {
            // 注意: 旧代码只比较"数量"(beacons.Count / creatureWin.Count), 于是只要
            // 生物数量没变(比如同样 3 只在游动), 状态一直在变也永远不重绘 -> 红三角看着是钉死的。
            // 现在用坐标签名, 生物一动就重绘。
            int bSig = BeaconSig(), cSig = CreatureSig();
            bool changed = hasGame != lastHasGame
                || biome != lastBiome
                || Math.Abs(px - lastPx) > 0.01f
                || Math.Abs(pz - lastPz) > 0.01f
                || Math.Abs(heading - lastHeading) > 0.2f
                || bSig != lastBeaconKey
                || cSig != lastCreatureKey;
            if (changed)
            {
                lastPx = px; lastPz = pz; lastHeading = heading;
                lastHasGame = hasGame; lastBiome = biome;
                lastBeaconKey = bSig; lastCreatureKey = cSig;
                Invalidate(MapArea());
            }
        }
    }

    private void ReadStateFile()
    {
        hasGame = false;
        beacons.Clear();
        creatureWin.Clear();
        try
        {
            string sp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Proto.StateFileName);
            if (!File.Exists(sp)) throw new Exception();
            if (stateReadBuf == null) stateReadBuf = new byte[Proto.StateSize];
            byte[] buf = stateReadBuf;
            // 显式 FileShare.ReadWrite: File.ReadAllBytes 用的是 FileShare.Read, 会把模块那头的
            // 20Hz 写入挡掉(写失败就丢帧, 表现还是"不刷新")
            using (FileStream fs = new FileStream(sp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int total = (int)Math.Min(fs.Length, buf.Length);
                int got = 0;
                while (got < total)
                {
                    int r = fs.Read(buf, got, total - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got < 128) throw new Exception();
            }
            if (GetInt(buf, OffMagic) != Magic) throw new Exception();
            int ver = GetInt(buf, OffVersion);
            if (ver != ProtoVersion)
            {
                modVer = "协议不匹配(" + ver + "), 请重新注入";
                return;
            }
            modVer = GetString(buf, OffModVerLen, OffModVer, 32);
            if (GetInt(buf, OffPlayerValid) != 1) return;
            hasGame = true;
            px = GetFloat(buf, OffX);
            py = GetFloat(buf, OffY);
            pz = GetFloat(buf, OffZ);
            heading = GetFloat(buf, OffHeading);
            biome = GetString(buf, OffBiomeLen, OffBiome, 64);

            // 显示/隐藏的决定权交给状态文件里模块写的那一字节:
            //   设置文件那条路依赖"文件时间戳变了没有", 而模块(F9)和窗口(设置项)都会写它,
            //   时间戳精度/互相覆盖会让 F9 的意图丢掉 —— 就会出现"按 F9 打不开大地图"。
            //   状态文件是模块 20Hz 写的心跳, 窗口 30Hz 读, 稳。
            byte sw = buf[OffShowWindow];
            // 只认"新鲜"的状态文件(游戏真在跑): 游戏退出后文件会留着, 里面的旧字节会把窗口
            // 一会儿藏一会儿显(启动自检时就撞上了这个)
            bool fresh = (DateTime.UtcNow - File.GetLastWriteTimeUtc(sp)).TotalSeconds < 3.0;
            if (fresh && sw != wantShowByte)
            {
                wantShowByte = sw;
                if (sw != 0 && !Visible) { WinLog("show: module F9 -> 1"); ShowToFront(); }
                else if (sw == 0 && Visible) { WinLog("hide: module F9 -> 0"); Hide(); }
            }

            int n = ClampI(GetInt(buf, OffBeaconCount), 0, Proto.MaxBeacons);
            for (int i = 0; i < n; i++)
            {
                int off = OffBeacons + i * 128;
                Beacon bk;
                if (i < beaconFree.Count) bk = beaconFree[i];
                else { bk = new Beacon(); beaconFree.Add(bk); }
                bk.X = GetFloat(buf, off);
                bk.Z = GetFloat(buf, off + 4);
                bk.Color = GetInt(buf, off + 8);
                bk.Label = GetString(buf, off + 16, off + 20, 63);
                bk.Key = GetString(buf, off + 84, off + 88, 32);
                bk.Kind = buf[off + 120];
                beacons.Add(bk);
            }
            int cn = ClampI(GetInt(buf, OffCreatureCount), 0, Proto.MaxCreatures);
            for (int i = 0; i < cn; i++)
            {
                int off = OffCreatures + i * 80;
                CreaturePt c;
                if (i < creatureFree.Count) c = creatureFree[i];
                else { c = new CreaturePt(); creatureFree.Add(c); }
                c.X = GetFloat(buf, off);
                c.Z = GetFloat(buf, off + 4);
                c.Label = GetString(buf, off + 8, off + 12, 32);
                c.Key = GetString(buf, off + 44, off + 48, 32);
                creatureWin.Add(c);
            }
            // 模块见过的敌对物种清单(设置窗口右侧列表用)
            int sn = ClampI(GetInt(buf, Proto.OffSpeciesCount), 0, Proto.MaxSpecies);
            for (int i = 0; i < sn; i++)
            {
                int so = Proto.OffSpecies + i * Proto.SpeciesStride;
                string nm = GetString(buf, so, so + 4, Proto.SpeciesStride - 4);
                if (nm.Length > 0 && !species.Contains(nm)) species.Add(nm);
            }
            noStateTicks = 0;
        }
        catch (Exception)
        {
            noStateTicks++;
            // 游戏退出后状态文件消失: --auto 待命的隐藏窗口 10 秒后自动退出
            if (noStateTicks > 300 && !Visible && Program.AutoStart)
            {
                exiting = true;
                Close();
            }
        }
    }

    private void PollSettings()
    {
        if (tickCount % 3 != 0) return;
        try
        {
            string p = SettingsPath();
            if (!File.Exists(p)) return;
            DateTime wt = File.GetLastWriteTimeUtc(p);
            if (wt == settingsStamp) return;
            settingsStamp = wt;
            Dictionary<string, string> d = Proto.ParseIniStrings(p);
            string s;
            if (d.TryGetValue("ShowWindow", out s))
            {
                // 没有 state 文件(游戏没跑)时才由设置文件决定显示; 游戏在跑时以上面那个字节为准
                bool wantShow = s == "1";
                if (wantShow && !Visible) { WinLog("show: settings ShowWindow=1"); ShowToFront(); }
                if (!wantShow && Visible) { WinLog("hide: settings ShowWindow=0"); Hide(); }
            }
            if (d.TryGetValue(Proto.KeyMinimapPixels, out s))
            {
                int v;
                if (int.TryParse(s, out v) && v >= 120 && v <= 800) minimapPixels = v;
            }
            if (d.TryGetValue(Proto.KeyShowCreatures, out s)) showCreatures = s != "0";
            if (d.TryGetValue(Proto.KeyShowScanSignals, out s)) showScanSignals = s != "0";
        }
        catch (Exception) { }
    }

    // --auto(注入后由引导层拉起)时保持隐藏待命: 拦掉 Application.Run 的首次显示,
    // 否则会先把最大化的窗口闪一下再被轮询藏掉。F9 要显示时走 ShowToFront() 放行。
    private bool allowShow;

    protected override void SetVisibleCore(bool value)
    {
        if (value && !allowShow && Program.AutoStart)
        {
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    // F9 显示: 必须真的抬到最前。原来只调 SW_SHOWNA(只显示不抬升), 会被游戏窗口压在后面。
    // 关键取舍: 用 SWP_NOACTIVATE 抬升但不抢焦点 —— 一旦抢了游戏的键盘焦点, 游戏就收不到下一次 F9, 反而关不掉地图。
    private void ShowToFront()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        TopMost = chkTop.Checked;   // 让"置顶"勾选真正生效(默认勾选)
        try
        {
            SetWindowPos(Handle, TopMost ? HWND_TOPMOST : HWND_TOP, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
        }
        catch (Exception) { }
    }

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

    // 窗口自己拿到 F9 说明焦点在窗口上(游戏收不到这个键): 直接写设置文件隐藏自己, 保证"点过地图后按 F9 也能关"
    private void OnFormKeyDown(object s, KeyEventArgs e)
    {
        if (e.KeyCode != toggleKey) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        WriteSettingsKey("ShowWindow", "0");
    }

    private Keys LoadToggleKey()
    {
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            Dictionary<string, string> d = Proto.ParseIniStrings(p);
            string s;
            if (d.TryGetValue("ToggleMapKey", out s))
            {
                Keys k;
                if (Enum.TryParse<Keys>(s, true, out k)) return k;
            }
        }
        catch (Exception) { }
        return Keys.F9;
    }

    private static string Heading8(float deg)
    {
        // 旧写法 ClampI(round(deg/45),0,359)/45%8 又除了一次 45, 结果永远显示 "N 北"
        return Headings[((int)Math.Round(deg / 45f) % 8 + 8) % 8];
    }

    private static int GetInt(byte[] b, int off)
    {
        return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
    }

    private static float GetFloat(byte[] b, int off)
    {
        return BitConverter.ToSingle(b, off);
    }

    private static string GetString(byte[] b, int lenOff, int strOff, int max)
    {
        int len = GetInt(b, lenOff);
        if (len <= 0 || len > max) return "";
        return Encoding.UTF8.GetString(b, strOff, len);
    }

    // ------------------------------------------------------------- painting

    private Rectangle MapArea()
    {
        return new Rectangle(0, 74, ClientSize.Width, ClientSize.Height - 74);
    }

    private float FitScale(MapLayer L)
    {
        Rectangle r = MapArea();
        float w = L.MaxX - L.MinX, h = L.MaxZ - L.MinZ;
        if (w <= 0f || h <= 0f) return 0.1f;
        return Math.Min(r.Width / w, r.Height / h);
    }

    // 全工具统一坐标系: +X=东=屏幕右, +Z=北=屏幕上(地图图片上方=北=MaxZ)
    // 历史 bug: 这里把 +Z 当成屏幕下方, 于是底图采到的是"南北镜像"的区域, 而且叠加层与底图各用一套约定。
    private PointF WorldToScreen(MapLayer L, float wx, float wz)
    {
        Rectangle r = MapArea();
        float s = FitScale(L) * zoom;
        return new PointF(r.X + r.Width / 2f + (wx - viewCX) * s, r.Y + r.Height / 2f - (wz - viewCZ) * s);
    }

    private PointF ScreenToWorldPt(MapLayer L, PointF sp)
    {
        Rectangle r = MapArea();
        float s = FitScale(L) * zoom;
        return new PointF(viewCX + (sp.X - (r.X + r.Width / 2f)) / s, viewCZ - (sp.Y - (r.Y + r.Height / 2f)) / s);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        Rectangle r = MapArea();
        g.FillRectangle(mapBgBrush, r);

        MapLayer L = Layer();
        if (L == null)
        {
            g.DrawString("maps 文件夹里没有地图图片", mapFont, Brushes.White, 16, 84);
            return;
        }
        EnsureMips(L);
        if (L.Mips == null)
        {
            g.DrawString("图层加载失败: " + L.Name, mapFont, Brushes.White, 16, 84);
            return;
        }

        // 从 mip 金字塔选一级: MipPxPerM 存的是"米/像素", 取"不比屏幕更粗"里最粗的一级。
        // 旧条件拿 米/像素 和 屏幕像素/米 比大小(量纲都不对), 放大后反而挑中最糊的那级 -> 高倍缩放一片糊。
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float vs = FitScale(L) * zoom;
        float targetMPerPx = 1f / Math.Max(vs, 0.0001f);   // 屏幕上一像素代表多少米
        int mi = 0;                                       // 默认用最细一级(比屏幕还细时就是它)
        for (int i = 0; i < L.Mips.Length; i++)
        {
            if (L.MipPxPerM[i] <= targetMPerPx) mi = i; else break;
        }
        float cs = L.MipPxPerM[mi];
        // 目标矩形上边缘对应世界 z 最大(北)一侧 -> 取图片中该 z 所在的行
        RectangleF src = new RectangleF(
            (viewCX - r.Width / 2f / vs - L.MinX) / cs,
            (L.MaxZ - (viewCZ + r.Height / 2f / vs)) / cs,
            r.Width / vs / cs,
            r.Height / vs / cs);
        g.DrawImage(L.Mips[mi], r, src, GraphicsUnit.Pixel);

        if (L.Calibrated)
        {
            foreach (Beacon bk in beacons)
            {
                PointF sp = WorldToScreen(L, bk.X, bk.Z);
                if (sp.X < r.X - 40f || sp.Y < r.Y - 40f || sp.X > r.Right + 40f || sp.Y > r.Bottom + 40f) continue;
                Image bic = GetIcon(bk.Key);
                if (bic != null)
                {
                    // 扫描室扫描的物品: 直接画游戏里的物品图标
                    Rectangle ir = new Rectangle((int)(sp.X - 10f), (int)(sp.Y - 10f), 20, 20);
                    g.FillRectangle(shadowBrush, ir);
                    g.DrawImage(bic, ir);
                }
                else
                {
                    g.FillEllipse(bk.Kind != 0 ? creatureBrush : PingBrush(bk.Color), sp.X - 6f, sp.Y - 6f, 12f, 12f);
                    g.DrawEllipse(beaconPen, sp.X - 6f, sp.Y - 6f, 12f, 12f);
                }
                if (!string.IsNullOrEmpty(bk.Label))
                    DrawShadowText(g, bk.Label, bic != null ? smallFont : mapFont, sp.X + 11f, sp.Y - 10f);
            }

            if (hasGame)
            {
                PointF pp = WorldToScreen(L, px, pz);
                GraphicsState state = g.Save();
                g.TranslateTransform(pp.X, pp.Y);
                // 箭头图形本身指向正上方(北); GDI+ 正角度=顺时针, 正好等于罗盘方位角的正方向
                g.RotateTransform(heading);
                PointF[] pts = new PointF[]
                {
                    new PointF(0f, -13f), new PointF(9f, 11f), new PointF(0f, 6f), new PointF(-9f, 11f)
                };
                g.FillPolygon(arrowFill, pts);
                g.DrawPolygon(arrowPen, pts);
                g.Restore(state);
            }

            if (showCreatures)
            {
                foreach (CreaturePt c in creatureWin)
                {
                    PointF cp = WorldToScreen(L, c.X, c.Z);
                    if (cp.X < r.X - 20f || cp.Y < r.Y - 20f || cp.X > r.Right + 20f || cp.Y > r.Bottom + 20f) continue;
                    Image cic = GetIcon(c.Key);
                    if (cic != null)
                    {
                        // 生物头像(内置 icons/<TechType>.png)
                        Rectangle ir = new Rectangle((int)(cp.X - 12f), (int)(cp.Y - 12f), 24, 24);
                        g.FillRectangle(shadowBrush, ir);
                        g.DrawImage(cic, ir);
                    }
                    else
                    {
                        // 没有头像的生物(例如幽灵利维坦, 游戏里就没这张 UI 图): 画黑色描边的红色感叹号
                        DrawAlertMark(g, cp.X, cp.Y);
                    }
                    if (!string.IsNullOrEmpty(c.Label))
                        DrawShadowText(g, c.Label, smallFont, cp.X + 13f, cp.Y - 8f);
                }
            }

            float mPerPx = 1f / Math.Max(vs, 0.0001f);
            float barMeters = 200f;
            float barPx = barMeters / mPerPx;
            while (barPx > 260f) { barPx /= 2f; barMeters /= 2f; }
            while (barPx < 60f) { barPx *= 2f; barMeters *= 2f; }
            g.DrawLine(scalePen, 16f, r.Bottom - 20f, 16f + barPx, r.Bottom - 20f);
            g.DrawLine(scalePen, 16f, r.Bottom - 24f, 16f, r.Bottom - 16f);
            g.DrawLine(scalePen, 16f + barPx, r.Bottom - 24f, 16f + barPx, r.Bottom - 16f);
            DrawShadowText(g, barMeters.ToString("0") + " m", smallFont, 20f + barPx, r.Bottom - 30f);
        }
        else
        {
            DrawShadowText(g, "该图层未标定世界坐标, 仅浏览 (玩家/信标不显示)", mapFont, 16f, r.Top + 10f);
        }

        if (!hasGame)
        {
            string msg = modVer.StartsWith("协议") ? modVer : "等待游戏数据 — 请启动游戏并运行 SNInjector.exe 注入";
            SizeF sz = g.MeasureString(msg, mapFont);
            g.FillRectangle(shadowBrush, r.Width / 2f - sz.Width / 2f - 10f, r.Top + 14f, sz.Width + 20f, sz.Height + 8f);
            g.DrawString(msg, mapFont, Brushes.White, r.Width / 2f - sz.Width / 2f, r.Top + 18f);
        }
    }

    // 位置签名(0.125m 量化), 用来判断"这份数据变了没有、要不要重绘"
    private int BeaconSig()
    {
        int h = beacons.Count * 397;
        for (int i = 0; i < beacons.Count; i++)
        {
            Beacon b = beacons[i];
            h = h * 31 + (int)(b.X * 8f) * 7 + (int)(b.Z * 8f) * 13 + b.Color;
        }
        return h;
    }

    private int CreatureSig()
    {
        int h = creatureWin.Count * 397;
        for (int i = 0; i < creatureWin.Count; i++)
        {
            CreaturePt c = creatureWin[i];
            h = h * 31 + (int)(c.X * 8f) * 7 + (int)(c.Z * 8f) * 13;
        }
        return h;
    }

    // 内置图标(icons/<TechType>.png)按需加载 + 缓存; 没有图标就退回原来的点/三角
    private readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>();

    private Image GetIcon(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        Image img;
        if (iconCache.TryGetValue(key, out img)) return img;
        if (iconCache.Count >= 256) return null;
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("icons", key + ".png"));
            img = File.Exists(p) ? Image.FromFile(p) : null;
        }
        catch (Exception) { img = null; }
        iconCache[key] = img;
        return img;
    }

    // 黑色描边的红色感叹号: 给"游戏里没有头像"的生物当标记(幽灵利维坦等)
    private void DrawAlertMark(Graphics g, float cx, float cy)
    {
        if (alertFont == null) alertFont = new Font("Arial", 24f, FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF m = g.MeasureString("!", alertFont);
        float x = cx - m.Width / 2f, y = cy - m.Height / 2f;
        for (int dx = -2; dx <= 2; dx += 2)
            for (int dy = -2; dy <= 2; dy += 2)
                if (dx != 0 || dy != 0) g.DrawString("!", alertFont, Brushes.Black, x + dx, y + dy);
        g.DrawString("!", alertFont, alertBrush, x, y);
    }

    private SolidBrush PingBrush(int idx)
    {
        int i = ((idx % PingColors.Length) + PingColors.Length) % PingColors.Length;
        if (pingBrushes == null || pingBrushes.Length != PingColors.Length)
        {
            pingBrushes = new SolidBrush[PingColors.Length];
            for (int k = 0; k < PingColors.Length; k++) pingBrushes[k] = new SolidBrush(PingColors[k]);
        }
        return pingBrushes[i];
    }

    private void DrawShadowText(Graphics g, string text, Font f, float x, float y)
    {
        g.DrawString(text, f, shadowBrush, x + 1.5f, y + 1.5f);
        g.DrawString(text, f, Brushes.White, x, y);
    }

    // ------------------------------------------------------------- input

    private void ClampView()
    {
        MapLayer L = Layer();
        if (L == null || !L.Calibrated) return;
        Rectangle r = MapArea();
        float s = FitScale(L) * zoom;
        float halfW = r.Width / 2f / s, halfH = r.Height / 2f / s;
        float w = L.MaxX - L.MinX, h = L.MaxZ - L.MinZ;
        viewCX = w <= halfW * 2f ? (L.MinX + L.MaxX) / 2f
            : Math.Max(L.MinX + halfW, Math.Min(L.MaxX - halfW, viewCX));
        viewCZ = h <= halfH * 2f ? (L.MinZ + L.MaxZ) / 2f
            : Math.Max(L.MinZ + halfH, Math.Min(L.MaxZ - halfH, viewCZ));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        MapLayer L = Layer();
        if (L == null || !L.Calibrated) return;
        PointF sp = new PointF(e.X, e.Y);   // ScreenToWorldPt 现在按客户区坐标(与 WorldToScreen 同一套)
        PointF before = ScreenToWorldPt(L, sp);
        zoom = Math.Max(1f, Math.Min(32f, zoom * (e.Delta > 0 ? 1.2f : 1 / 1.2f)));
        PointF after = ScreenToWorldPt(L, sp);
        viewCX += before.X - after.X;
        viewCZ += before.Y - after.Y;
        ClampView();
        Invalidate(MapArea());
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            dragging = true;
            lastMouse = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && e.Button == MouseButtons.Left)
        {
            MapLayer L = Layer();
            if (L == null || !L.Calibrated) return;
            float s = FitScale(L) * zoom;
            viewCX -= (e.X - lastMouse.X) / s;
            viewCZ += (e.Y - lastMouse.Y) / s;   // +Z 是屏幕上方, 往下拖 = 视野往北移(内容跟着鼠标走)
            lastMouse = e.Location;
            if (follow)
            {
                follow = false;
                chkFollow.Checked = false;
            }
            ClampView();
            Invalidate(MapArea());
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) dragging = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (hasGame)
        {
            follow = true;
            chkFollow.Checked = true;
            Invalidate(MapArea());
        }
    }
}
