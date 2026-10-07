// SNMapWindow - 深海迷航独立大地图窗口 (解压即用, 配合注入模块通过共享内存通信)
// 数据: 游戏内注入模块写入共享内存 SNMapState_1 (玩家/朝向/信标/生物群系/F9标志)
// 交互: 滚轮缩放(原生事件) / 左键拖拽平移 / [－][＋]切换图层 / 跟随 / 置顶 / F9 显示隐藏
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        // 单实例: 自动清掉多余的旧窗口进程(之前版本可能堆积多个)
        Process cur = Process.GetCurrentProcess();
        foreach (Process p in Process.GetProcessesByName("SNMapWindow"))
        {
            if (p.Id != cur.Id) { try { p.Kill(); } catch (Exception) { } }
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
    public float[] MipPxPerM;   // 每级: 像素/米
}

internal class CreaturePt
{
    public float X, Z;
    public string Label;
}

public class MapForm : Form
{
    private const string MmfName = "SNMapState_1";
    private const int OffPlayerValid = 16, OffX = 20, OffY = 24, OffZ = 28, OffHeading = 32;
    private const int OffShowWindow = 36, OffBeaconCount = 40, OffBiomeLen = 44, OffBiome = 48;
    private const int OffBeacons = 128, OffWindowLayer = 4224, OffWorldRange = 4228;
    private const int OffMinimapPixels = 4232, OffShowCreatures = 4236, OffCreatureCount = 4240, OffCreatures = 4248;

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
    private List<Beacon> beacons = new List<Beacon>();
    private readonly List<CreaturePt> creatureWin = new List<CreaturePt>();
    private readonly SolidBrush creatureBrush = new SolidBrush(Color.FromArgb(225, 255, 40, 40));
    private MemoryMappedFile mmf;
    private MemoryMappedViewAccessor acc;
    // mip 金字塔缓存见 MapLayer.Mips
    private bool mmfTried;
    private bool lastShowFlag;
    private bool firstTick = true;

    // 图层纹理采用 mip 金字塔缓存(见 MapLayer.Mips)

    // 脏检查: 状态没变化就不重绘
    private float lastPx = float.MinValue, lastPz = float.MinValue, lastHeading = float.MinValue;
    private int lastBeaconKey = -1;
    private bool lastHasGame;
    private string lastBiome = "";
    private readonly List<Beacon> beaconBuf = new List<Beacon>();
    private float worldRange = 2000f;
    private bool exiting;

    private Button btnPrev, btnNext, btnExit;
    private CheckBox chkFollow, chkTop, chkCreatures;
    private TextBox txtMini;
    private Button btnApply;
    private Label lblLayer, lblInfo;
    private Font mapFont, smallFont;

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

    private class Beacon
    {
        public float X, Z;
        public int Color;
        public string Label;
    }

    public MapForm()
    {
        Text = "深海迷航大地图 SNMap";
        ClientSize = new Size(940, 920);
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemeBg;
        DoubleBuffered = true;

        mapFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        smallFont = new Font("Microsoft YaHei UI", 9f);

        btnPrev = BlueBtn("－", 8, 8, 40, 30);
        btnNext = BlueBtn("＋", 8 + 40 + 250 + 8, 8, 40, 30);
        btnExit = BlueBtn("退出", ClientSize.Width - 78, 8, 70, 30);
        lblLayer = new Label();
        lblLayer.Location = new Point(56, 13);
        lblLayer.Size = new Size(250, 24);
        lblLayer.ForeColor = ThemeAccentDark;
        lblLayer.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        lblLayer.TextAlign = ContentAlignment.MiddleCenter;

        chkFollow = new CheckBox();
        chkFollow.Text = "跟随玩家";
        chkFollow.Checked = true;
        chkFollow.Location = new Point(8, 44);
        chkFollow.AutoSize = true;
        chkFollow.CheckedChanged += delegate { follow = chkFollow.Checked; };
        Controls.Add(chkFollow);

        chkTop = new CheckBox();
        chkTop.Text = "窗口置顶";
        chkTop.Checked = true;
        chkTop.Location = new Point(100, 44);
        chkTop.AutoSize = true;
        chkTop.CheckedChanged += delegate { TopMost = chkTop.Checked; };
        Controls.Add(chkTop);

        Label lmini = new Label();
        lmini.Text = "小地图:";
        lmini.Location = new Point(200, 47);
        lmini.AutoSize = true;
        lmini.ForeColor = ThemeText;
        Controls.Add(lmini);

        txtMini = new TextBox();
        txtMini.Location = new Point(258, 43);
        txtMini.Size = new Size(48, 24);
        Controls.Add(txtMini);

        btnApply = BlueBtn("应用", 310, 43, 48, 30);
        btnApply.Click += delegate { ApplyMiniSize(); };
        Controls.Add(btnApply);

        chkCreatures = new CheckBox();
        chkCreatures.Text = "攻击性生物";
        chkCreatures.Checked = true;
        chkCreatures.Location = new Point(366, 45);
        chkCreatures.AutoSize = true;
        chkCreatures.CheckedChanged += delegate { WriteByteAt(OffShowCreatures, (byte)(chkCreatures.Checked ? 1 : 0)); };
        Controls.Add(chkCreatures);

        lblInfo = new Label();
        lblInfo.Location = new Point(470, 47);
        lblInfo.AutoSize = true;
        lblInfo.ForeColor = ThemeAccentDark;
        lblInfo.Font = smallFont;
        Controls.Add(lblInfo);

        Controls.Add(btnPrev);
        Controls.Add(btnNext);
        Controls.Add(btnExit);
        Controls.Add(lblLayer);

        btnPrev.Click += delegate { SwitchLayer(-1); };
        btnNext.Click += delegate { SwitchLayer(1); };
        btnExit.Click += delegate { exiting = true; Close(); };

        LoadLayers();
        if (layers.Count > 0) lblLayer.Text = Layer().Name + "  (" + (layerIdx + 1) + "/" + layers.Count + ")";

        FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            if (!exiting)
            {
                e.Cancel = true;
                Hide();
                WriteShowFlag(false);
            }
        };

        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 33;
        t.Tick += delegate { Tick(); };
        t.Start();
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
        return layers[Mathf_Clamp(layerIdx, 0, layers.Count - 1)];
    }

    private static int Mathf_Clamp(int v, int a, int b)
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
        worldRange = defRange;

        if (Directory.Exists(mapsDir))
        {
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(mapsDir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") files.Add(f);
            }
            files.Sort(StringComparer.Ordinal);
            Dictionary<string, float[]> ini = ParseMapsIni(Path.Combine(mapsDir, "maps.ini"));
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
                    // 非方形且未标定 -> 仅浏览
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
    }

    private static Dictionary<string, float[]> ParseMapsIni(string path)
    {
        Dictionary<string, float[]> res = new Dictionary<string, float[]>();
        try
        {
            if (!File.Exists(path)) return res;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string name = line.Substring(0, eq).Trim();
                string[] parts = line.Substring(eq + 1).Split(new char[] { ',' });
                if (parts.Length < 4) continue;
                float[] b = new float[4];
                bool ok = true;
                for (int k = 0; k < 4; k++)
                {
                    if (!float.TryParse(parts[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b[k])) { ok = false; break; }
                }
                if (ok) res[name] = b;
            }
        }
        catch (Exception) { }
        return res;
    }

    // 生成 mip 金字塔: 4096 -> 2048 -> ... -> 256, 一次性预缩放, 之后每帧只做块拷贝
    private void EnsureMips(MapLayer L)
    {
        if (L.Mips != null || !File.Exists(L.Path)) return;
        try
        {
            using (Image orig = Image.FromFile(L.Path))
            {
                int maxDim = Math.Max(orig.Width, orig.Height);
                int startW = Math.Max(16, orig.Width * Math.Min(maxDim, 4096) / maxDim);
                int startH = Math.Max(16, orig.Height * Math.Min(maxDim, 4096) / maxDim);
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
        WriteLayerIdx();
        Invalidate();
    }

    // ------------------------------------------------------------- shared memory

    private void EnsureMmf()
    {
        if (acc != null) return;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(MmfName);
            acc = mmf.CreateViewAccessor();
        }
        catch (Exception)
        {
            acc = null;
        }
    }

    private void WriteShowFlag(bool on)
    {
        try
        {
            EnsureMmf();
            if (acc != null) acc.Write(OffShowWindow, (byte)(on ? 1 : 0));
        }
        catch (Exception) { }
    }

    private void WriteLayerIdx()
    {
        try
        {
            EnsureMmf();
            if (acc != null) acc.Write(OffWindowLayer, layerIdx);
        }
        catch (Exception) { }
    }

    private void WriteByteAt(int off, byte v)
    {
        try
        {
            EnsureMmf();
            if (acc != null) acc.Write(off, v);
        }
        catch (Exception) { }
    }

    private void WriteIntAt(int off, int v)
    {
        try
        {
            EnsureMmf();
            if (acc != null) acc.Write(off, v);
        }
        catch (Exception) { }
    }

    private void ApplyMiniSize()
    {
        int v;
        if (!int.TryParse(txtMini.Text.Trim(), out v))
        {
            MessageBox.Show(this, "请输入数字, 例如 360", "SNMap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        v = Math.Max(120, Math.Min(800, v));
        txtMini.Text = v.ToString();
        WriteIntAt(OffMinimapPixels, v);
        SaveConfigMini(v);
    }

    private void SaveConfigMini(int v)
    {
        try
        {
            string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            if (!File.Exists(cfg))
            {
                File.WriteAllLines(cfg, new string[] { "MinimapPixels=" + v });
                return;
            }
            string[] lines = File.ReadAllLines(cfg);
            bool found = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("MinimapPixels=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "MinimapPixels=" + v;
                    found = true;
                }
            }
            if (!found)
            {
                Array.Resize(ref lines, lines.Length + 1);
                lines[lines.Length - 1] = "MinimapPixels=" + v;
            }
            File.WriteAllLines(cfg, lines);
        }
        catch (Exception) { }
    }

    private void Tick()
    {
        if (!mmfTried || acc != null) EnsureMmf();
        bool show = lastShowFlag;
        hasGame = false;
        beacons = new List<Beacon>();
        if (acc != null)
        {
            try
            {
                show = acc.ReadByte(OffShowWindow) != 0;
                if (acc.ReadInt32(OffPlayerValid) == 1)
                {
                    hasGame = true;
                    px = acc.ReadSingle(OffX);
                    py = acc.ReadSingle(OffY);
                    pz = acc.ReadSingle(OffZ);
                    heading = acc.ReadSingle(OffHeading);
                    int bl = acc.ReadInt32(OffBiomeLen);
                    if (bl > 0 && bl < 64)
                    {
                        byte[] b = new byte[bl];
                        acc.ReadArray(OffBiome, b, 0, bl);
                        biome = Encoding.UTF8.GetString(b);
                    }
                    else biome = "";
                    int n = acc.ReadInt32(OffBeaconCount);
                    if (n > 32) n = 32;
                    for (int i = 0; i < n; i++)
                    {
                        int off = OffBeacons + i * 128;
                        Beacon bk = new Beacon();
                        bk.X = acc.ReadSingle(off);
                        bk.Z = acc.ReadSingle(off + 4);
                        bk.Color = acc.ReadInt32(off + 8);
                        int ll = acc.ReadInt32(off + 16);
                        if (ll > 0 && ll < 64)
                        {
                            byte[] b = new byte[ll];
                            acc.ReadArray(off + 20, b, 0, ll);
                            bk.Label = Encoding.UTF8.GetString(b);
                        }
                        beacons.Add(bk);
                    }

                    int cn = acc.ReadInt32(OffCreatureCount);
                    if (cn > 24) cn = 24;
                    creatureWin.Clear();
                    for (int i = 0; i < cn; i++)
                    {
                        int off = OffCreatures + i * 80;
                        CreaturePt c = new CreaturePt();
                        c.X = acc.ReadSingle(off);
                        c.Z = acc.ReadSingle(off + 4);
                        int ll = acc.ReadInt32(off + 8);
                        if (ll > 0 && ll < 67)
                        {
                            byte[] b = new byte[ll];
                            acc.ReadArray(off + 12, b, 0, ll);
                            c.Label = Encoding.UTF8.GetString(b);
                        }
                        creatureWin.Add(c);
                    }
                }
                mmfTried = true;
            }
            catch (Exception)
            {
                acc = null;
                mmfTried = false;
            }
        }
        else
        {
            mmfTried = false;
        }

        if (firstTick)
        {
            // 启动首拍: 只要游戏已注入就主动置显示标志并保持可见; 未注入则停留等待页
            firstTick = false;
            if (acc != null)
            {
                WriteShowFlag(true);
                show = true;
                try
                {
                    int px = acc.ReadInt32(OffMinimapPixels);
                    if (px >= 120 && px <= 800) txtMini.Text = px.ToString();
                    chkCreatures.Checked = acc.ReadByte(OffShowCreatures) != 0;
                }
                catch (Exception) { }
            }
        }
        else if (acc != null && show != Visible)
        {
            // 只有在游戏已注入(共享内存存在)时才跟随 F9 标志显隐
            if (show) ShowNoActivate();
            else Hide();
        }
        // acc == null: 游戏未注入, 保持窗口可见显示等待提示

        if (follow && hasGame)
        {
            viewCX = px;
            viewCZ = pz;
            ClampView();
        }
        lblInfo.Text = hasGame
            ? string.Format("X {0:F0}   Z {1:F0}   深度 {2:F0}m   {3}   {4}", px, pz, py, Heading8(heading), biome)
            : "等待游戏数据 (启动游戏并注入 SNMap 后自动连接)";

        // 脏检查: 状态没变化就不重绘(跟随移动/转向/信标增减才算变化)
        if (Visible)
        {
            bool changed = hasGame != lastHasGame
                || biome != lastBiome
                || Math.Abs(px - lastPx) > 0.01f
                || Math.Abs(pz - lastPz) > 0.01f
                || Math.Abs(heading - lastHeading) > 0.2f
                || beaconBuf.Count != lastBeaconKey;
            if (changed)
            {
                lastPx = px; lastPz = pz; lastHeading = heading;
                lastHasGame = hasGame; lastBiome = biome; lastBeaconKey = beaconBuf.Count;
                Invalidate(MapArea());
            }
        }
        beacons = beaconBuf;
    }

    // 按当前缩放档位预缩放图层到缓存位图(切层/跨档/改尺寸才重建一次)
    private float FitScale(MapLayer L)
    {
        Rectangle r = MapArea();
        float w = L.MaxX - L.MinX, h = L.MaxZ - L.MinZ;
        if (w <= 0f || h <= 0f) return 0.1f;
        return Math.Min(r.Width / w, r.Height / h);
    }

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

    private void ShowNoActivate()
    {
        Show();
        try
        {
            ShowWindow(Handle, 8); // SW_SHOWNOACTIVATE
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static string Heading8(float deg)
    {
        return Headings[((int)Math.Round(deg / 45f)) % 8];
    }

    // ------------------------------------------------------------- painting

    private Rectangle MapArea()
    {
        return new Rectangle(0, 74, ClientSize.Width, ClientSize.Height - 74);
    }

    private readonly Pen beaconPen = new Pen(Color.FromArgb(200, 10, 10, 10), 2f);
    private SolidBrush[] pingBrushes;
    private readonly Pen scalePen = new Pen(Color.White, 3f);
    private readonly SolidBrush arrowFill = new SolidBrush(Color.FromArgb(235, 255, 70, 70));
    private readonly Pen arrowPen = new Pen(Color.FromArgb(220, 10, 10, 10), 2f);
    private readonly SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
    private readonly SolidBrush mapBgBrush = new SolidBrush(MapBg);

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

    private PointF WorldToScreen(MapLayer L, float wx, float wz)
    {
        Rectangle r = MapArea();
        float s = FitScale(L) * zoom;
        return new PointF(r.Width / 2f + (wx - viewCX) * s, r.Height / 2f + (wz - viewCZ) * s);
    }

    private PointF ScreenToWorldPt(MapLayer L, PointF sp)
    {
        Rectangle r = MapArea();
        float s = FitScale(L) * zoom;
        return new PointF(viewCX + (sp.X - r.Width / 2f) / s, viewCZ + (sp.Y - r.Height / 2f) / s);
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

        // 从 mip 金字塔选一级(分辨率最接近屏幕), 抠出当前视野做块拷贝
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float vs = FitScale(L) * zoom;
        int mi = L.Mips.Length - 1;
        for (int i = 0; i < L.Mips.Length; i++)
        {
            if (L.MipPxPerM[i] >= vs) { mi = i; break; }
        }
        float cs = L.MipPxPerM[mi];
        RectangleF src = new RectangleF(
            (viewCX - r.Width / 2f / vs - L.MinX) / cs,
            (viewCZ - r.Height / 2f / vs - L.MinZ) / cs,
            r.Width / vs / cs,
            r.Height / vs / cs);
        g.DrawImage(L.Mips[mi], r, src, GraphicsUnit.Pixel);

        if (L.Calibrated)
        {
            // 信标
            foreach (Beacon bk in beacons)
            {
                PointF sp = WorldToScreen(L, bk.X, bk.Z);
                if (sp.X < r.X - 40f || sp.Y < r.Y - 40f || sp.X > r.Right + 40f || sp.Y > r.Bottom + 40f) continue;
                g.FillEllipse(PingBrush(bk.Color), sp.X - 6f, sp.Y - 6f, 12f, 12f);
                g.DrawEllipse(beaconPen, sp.X - 6f, sp.Y - 6f, 12f, 12f);
                if (!string.IsNullOrEmpty(bk.Label))
                {
                    DrawShadowText(g, bk.Label, mapFont, sp.X + 9f, sp.Y - 10f);
                }
            }

            // 玩家箭头
            if (hasGame)
            {
                PointF pp = WorldToScreen(L, px, pz);
                GraphicsState state = g.Save();
                g.TranslateTransform(pp.X, pp.Y);
                // GDI+ 正角度=顺时针, 与游戏内 IMGUI(逆时针)相反, 故取负号保证两边一致
                g.RotateTransform(-heading);
                PointF[] pts = new PointF[]
                {
                    new PointF(0f, -13f), new PointF(9f, 11f), new PointF(0f, 6f), new PointF(-9f, 11f)
                };
                g.FillPolygon(arrowFill, pts);
                g.DrawPolygon(arrowPen, pts);
                g.Restore(state);
            }

            // 攻击性生物(红色警示三角)
            if (chkCreatures != null && chkCreatures.Checked)
            {
                foreach (CreaturePt c in creatureWin)
                {
                    PointF cp = WorldToScreen(L, c.X, c.Z);
                    if (cp.X < r.X - 20f || cp.Y < r.Y - 20f || cp.X > r.Right + 20f || cp.Y > r.Bottom + 20f) continue;
                    PointF[] tri = new PointF[]
                    {
                        new PointF(cp.X, cp.Y - 9f), new PointF(cp.X + 8f, cp.Y + 7f), new PointF(cp.X - 8f, cp.Y + 7f)
                    };
                    g.FillPolygon(creatureBrush, tri);
                    if (!string.IsNullOrEmpty(c.Label))
                        DrawShadowText(g, c.Label, smallFont, cp.X + 10f, cp.Y - 8f);
                }
            }

            // 比例尺
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
            string msg = "等待游戏数据 — 请启动游戏并运行 SNInjector.exe 注入";
            SizeF sz = g.MeasureString(msg, mapFont);
            g.FillRectangle(shadowBrush, r.Width / 2f - sz.Width / 2f - 10f, r.Top + 14f, sz.Width + 20f, sz.Height + 8f);
            g.DrawString(msg, mapFont, Brushes.White, r.Width / 2f - sz.Width / 2f, r.Top + 18f);
        }
    }

    private void DrawShadowText(Graphics g, string text, Font f, float x, float y)
    {
        g.DrawString(text, f, new SolidBrush(Color.FromArgb(200, 0, 0, 0)), x + 1.5f, y + 1.5f);
        g.DrawString(text, f, Brushes.White, x, y);
    }

    // ------------------------------------------------------------- input

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        MapLayer L = Layer();
        if (L == null || !L.Calibrated) return;
        PointF sp = new PointF(e.X, e.Y - MapArea().Y);
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
            viewCZ -= (e.Y - lastMouse.Y) / s;
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
        }
    }
}
