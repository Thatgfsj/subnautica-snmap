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
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MapForm());
    }
}

internal class MapLayer
{
    public string Name;
    public string Path;
    public Image Img;
    public float MinX = -2000f, MaxX = 2000f, MinZ = -2000f, MaxZ = 2000f;
    public bool Calibrated = true;
}

public class MapForm : Form
{
    private const string MmfName = "SNMapState_1";
    private const int OffPlayerValid = 16, OffX = 20, OffY = 24, OffZ = 28, OffHeading = 32;
    private const int OffShowWindow = 36, OffBeaconCount = 40, OffBiomeLen = 44, OffBiome = 48;
    private const int OffBeacons = 128, OffWindowLayer = 4224, OffWorldRange = 4228;

    private static readonly Color ThemeBg = Color.FromArgb(232, 241, 252);
    private static readonly Color ThemeAccent = Color.FromArgb(25, 118, 210);
    private static readonly Color ThemeAccentDark = Color.FromArgb(13, 71, 161);
    private static readonly Color ThemeHover = Color.FromArgb(30, 136, 229);
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
    private MemoryMappedFile mmf;
    private MemoryMappedViewAccessor acc;
    private bool mmfTried;
    private bool lastShowFlag;
    private bool firstTick = true;
    private float worldRange = 2000f;
    private bool exiting;

    private Button btnPrev, btnNext, btnExit;
    private CheckBox chkFollow, chkTop;
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

        lblInfo = new Label();
        lblInfo.Location = new Point(200, 44);
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

    private Image LayerImage(MapLayer L)
    {
        if (L.Img != null) return L.Img;
        try
        {
            if (!File.Exists(L.Path)) return null;
            L.Img = Image.FromFile(L.Path);
        }
        catch (Exception) { L.Img = null; }
        return L.Img;
    }

    private void SwitchLayer(int delta)
    {
        if (layers.Count == 0) return;
        MapLayer old = Layer();
        layerIdx = (layerIdx + delta + layers.Count) % layers.Count;
        MapLayer nw = Layer();
        if (old != null && old != nw && old.Img != null)
        {
            old.Img.Dispose();
            old.Img = null;
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
        }
        lblInfo.Text = hasGame
            ? string.Format("X {0:F0}   Z {1:F0}   深度 {2:F0}m   {3}   {4}", px, pz, py, Heading8(heading), biome)
            : "等待游戏数据 (启动游戏并注入 SNMap 后自动连接)";
        Invalidate();
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

    private float BaseScale(MapLayer L)
    {
        Rectangle r = MapArea();
        float w = L.MaxX - L.MinX, h = L.MaxZ - L.MinZ;
        if (w <= 0 || h <= 0) return 0.1f;
        return Math.Min(r.Width / w, r.Height / h) * zoom;
    }

    private Rectangle MapArea()
    {
        return new Rectangle(0, 74, ClientSize.Width, ClientSize.Height - 74);
    }

    private float ScaleAt(MapLayer L)
    {
        return BaseScale(L);
    }

    private PointF WorldToScreen(MapLayer L, float wx, float wz)
    {
        Rectangle r = MapArea();
        float s = ScaleAt(L);
        return new PointF(r.Width / 2f + (wx - viewCX) * s, r.Height / 2f + (wz - viewCZ) * s);
    }

    private PointF ScreenToWorldPt(MapLayer L, PointF sp)
    {
        Rectangle r = MapArea();
        float s = ScaleAt(L);
        return new PointF(viewCX + (sp.X - r.Width / 2f) / s, viewCZ + (sp.Y - r.Height / 2f) / s);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        Rectangle r = MapArea();
        g.FillRectangle(new SolidBrush(MapBg), r);

        MapLayer L = Layer();
        if (L == null)
        {
            g.DrawString("maps 文件夹里没有地图图片", mapFont, Brushes.White, 16, 84);
            return;
        }
        Image img = LayerImage(L);
        if (img == null)
        {
            g.DrawString("图层加载失败: " + L.Name, mapFont, Brushes.White, 16, 84);
            return;
        }

        float s = ScaleAt(L);
        PointF tl = WorldToScreen(L, L.MinX, L.MinZ);
        RectangleF dest = new RectangleF(tl.X, tl.Y, (L.MaxX - L.MinX) * s, (L.MaxZ - L.MinZ) * s);
        g.DrawImage(img, dest);

        if (L.Calibrated)
        {
            // 信标
            foreach (Beacon bk in beacons)
            {
                PointF sp = WorldToScreen(L, bk.X, bk.Z);
                if (!r.Contains(Point.Round(sp))) continue;
                Color c = PingColors[((bk.Color % PingColors.Length) + PingColors.Length) % PingColors.Length];
                g.FillEllipse(new SolidBrush(c), sp.X - 6f, sp.Y - 6f, 12f, 12f);
                g.DrawEllipse(new Pen(Color.FromArgb(200, 10, 10, 10), 2f), sp.X - 6f, sp.Y - 6f, 12f, 12f);
                if (!string.IsNullOrEmpty(bk.Label))
                {
                    DrawShadowText(g, bk.Label, mapFont, sp.X + 9f, sp.Y - 10f);
                }
            }

            // 玩家箭头
            if (hasGame)
            {
                PointF pp = WorldToScreen(L, px, pz);
                var state = g.Save();
                g.TranslateTransform(pp.X, pp.Y);
                g.RotateTransform(heading);
                PointF[] pts = new PointF[]
                {
                    new PointF(0f, -13f), new PointF(9f, 11f), new PointF(0f, 6f), new PointF(-9f, 11f)
                };
                g.FillPolygon(new SolidBrush(Color.FromArgb(235, 255, 70, 70)), pts);
                g.DrawPolygon(new Pen(Color.FromArgb(220, 10, 10, 10), 2f), pts);
                g.Restore(state);
            }

            // 比例尺
            float mPerPx = (L.MaxX - L.MinX) / Math.Max(dest.Width, 1f);
            float barMeters = 200f;
            float barPx = barMeters / Math.Max(mPerPx, 0.0001f);
            while (barPx > 260f) { barPx /= 2f; barMeters /= 2f; }
            while (barPx < 60f) { barPx *= 2f; barMeters *= 2f; }
            g.DrawLine(new Pen(Color.White, 3f), 16f, r.Bottom - 20f, 16f + barPx, r.Bottom - 20f);
            g.DrawLine(new Pen(Color.White, 3f), 16f, r.Bottom - 24f, 16f, r.Bottom - 16f);
            g.DrawLine(new Pen(Color.White, 3f), 16f + barPx, r.Bottom - 24f, 16f + barPx, r.Bottom - 16f);
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
            g.FillRectangle(new SolidBrush(Color.FromArgb(160, 0, 0, 0)), r.Width / 2f - sz.Width / 2f - 10f, r.Top + 14f, sz.Width + 20f, sz.Height + 8f);
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
        Invalidate();
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
            float s = ScaleAt(L);
            viewCX -= (e.X - lastMouse.X) / s;
            viewCZ -= (e.Y - lastMouse.Y) / s;
            lastMouse = e.Location;
            if (follow)
            {
                follow = false;
                chkFollow.Checked = false;
            }
            Invalidate();
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
