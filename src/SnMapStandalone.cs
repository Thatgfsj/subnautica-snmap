// SNMap 游戏内模块 v2.0 (BepInEx-free, 注入器方式加载)
// 职责: 左上角坐标 HUD + 圆形小地图 + 共享内存状态写入(供独立大地图窗口 SNMapWindow.exe 使用)
// 朝向: MainCamera.camera 的 forward(经验修正+180); Player.main.transform 不随视角旋转
// 扫描室信号(PingType.Signal) 只显示在小地图(橙色), 不发给大地图窗口
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SNMap
{
    public static class Boot
    {
        public static void InstallMain()
        {
            try
            {
                Cfg.Init();
                GameObject old = GameObject.Find("SNMapRoot");
                if (old != null)
                {
                    Cfg.Log("existing install found - replacing (hot update)");
                    UnityEngine.Object.Destroy(old);
                }
                GameObject go = new GameObject("SNMapRoot");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<SnMapBehaviour>();
                Cfg.Log("installed (main thread ok)");
            }
            catch (Exception ex)
            {
                try { Cfg.Log("install FAILED: " + ex); } catch (Exception) { }
            }
        }
    }

    internal static class Cfg
    {
        public static string ToggleMapKey = "F9";
        public static string ToggleHudKey = "F7";
        public static int FontSize = 20;
        public static float WorldRange = 2000f;
        public static int MinimapPixels = 360;
        public static float[] MinimapSpans = new float[] { 200f, 300f, 500f, 1000f };
        public static string BaseDir = ".";

        public static void Init()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Boot).Assembly.Location);
                BaseDir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;
            }
            catch (Exception) { BaseDir = Directory.GetCurrentDirectory(); }

            string path = Path.Combine(BaseDir, "config.ini");
            if (!File.Exists(path))
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# SNMap 配置 (改完重新注入生效)");
                sb.AppendLine("# 热键用 Unity KeyCode 名, 如 F9 F10 Insert Home M");
                sb.AppendLine("ToggleMapKey=" + ToggleMapKey);
                sb.AppendLine("ToggleHudKey=" + ToggleHudKey);
                sb.AppendLine("FontSize=" + FontSize);
                sb.AppendLine("WorldRange=" + WorldRange.ToString("0", CultureInfo.InvariantCulture));
                sb.AppendLine("MinimapPixels=" + MinimapPixels);
                sb.AppendLine("# 小地图各档显示范围(米), 逗号分隔, F7 循环: 关->第1档->...");
                sb.AppendLine("MinimapSpans=200,300,500,1000");
                try { File.WriteAllText(path, sb.ToString()); } catch (Exception) { }
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "ToggleMapKey": ToggleMapKey = val; break;
                        case "ToggleHudKey": ToggleHudKey = val; break;
                        case "FontSize": { int n; if (int.TryParse(val, out n)) FontSize = n; break; }
                        case "WorldRange": { float f; if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) WorldRange = f; break; }
                        case "MinimapPixels": { int n; if (int.TryParse(val, out n)) MinimapPixels = n; break; }
                        case "MinimapSpans":
                            {
                                string[] parts = val.Split(new char[] { ',', ';' });
                                List<float> fs = new List<float>();
                                for (int k = 0; k < parts.Length; k++)
                                {
                                    float f;
                                    if (float.TryParse(parts[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f > 10f)
                                        fs.Add(f);
                                }
                                if (fs.Count > 0) MinimapSpans = fs.ToArray();
                                break;
                            }
                    }
                }
                Log("config loaded: key=" + ToggleMapKey + " minimap=" + MinimapPixels + "px");
            }
            catch (Exception ex)
            {
                Log("config read failed, using defaults: " + ex.Message);
            }
        }

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(BaseDir, "SNMap.log"),
                    DateTime.Now.ToString("HH:mm:ss ") + msg + "\r\n");
            }
            catch (Exception) { }
            try { Debug.Log("[SNMap] " + msg); } catch (Exception) { }
        }
    }

    internal class MapLayer
    {
        public string Name;
        public string Path;
        public Texture2D Tex;
        public int Width;
        public int Height;
        public float MinX = -2000f, MaxX = 2000f, MinZ = -2000f, MaxZ = 2000f;
        public bool Calibrated = true;
        public bool HasIniBounds;
    }

    public class SnMapBehaviour : MonoBehaviour
    {
        private Texture2D arrowTex;
        private Texture2D dotTex;
        private Texture2D ringTex;
        private Font uiFont;
        private bool uiFontCjk;
        private GUIStyle hudStyle;
        private GUIStyle smallStyle;
        private GUIStyle miniSignalStyle;
        private int minimapIdx = 1;
        private List<MapLayer> layers = new List<MapLayer>();
        private FieldInfo pingsDictField;
        private FieldInfo pingColorsField;

        private byte[] stateBuf;
        private byte settingsShowWindow;
        private int windowLayer;
        private DateTime lastSettingsWrite;
        private int frame;
        private bool showWindow = true;   // 窗口进程的期望状态(与设置文件 ShowWindow 同步)
        private bool showCreatures = true;

        private static readonly string[] Headings = new string[]
        {
            "N 北", "NE 东北", "E 东", "SE 东南", "S 南", "SW 西南", "W 西", "NW 西北"
        };

        private static readonly Color[] FallbackPingColors = new Color[]
        {
            new Color(1f, 1f, 1f),
            new Color(1f, 0.35f, 0.35f),
            new Color(1f, 0.7f, 0.2f),
            new Color(1f, 0.95f, 0.3f),
            new Color(0.4f, 1f, 0.45f),
            new Color(0.35f, 0.9f, 1f),
            new Color(0.45f, 0.55f, 1f),
            new Color(1f, 0.5f, 1f)
        };

        private void Awake()
        {
            LoadFont();
            BuildArrowTexture();
            BuildDotTexture();
            BuildRingTexture(Cfg.MinimapPixels);
            LoadLayers();
            LoadPingsApi();
            stateBuf = new byte[8192];
            if (layers.Count > 0) GetTex(layers[0]);

            Cfg.Log("SNMap 2.0 awake | layers=" + layers.Count +
                    " cjk=" + uiFontCjk + " pings=" + (pingsDictField != null) +
                    " bigKey=" + Cfg.ToggleMapKey + " miniKey=" + Cfg.ToggleHudKey);
        }

        private void Update()
        {
            KeyCode kc;
            if (Enum.TryParse(Cfg.ToggleMapKey, true, out kc) && Input.GetKeyDown(kc))
            {
                // 读-翻转-写 设置文件里的 ShowWindow(与地图窗口共用)
                byte cur = ReadSettingsByte(Proto.KeyShowWindow, 0);
                showWindow = cur == 0;
                WriteSettingsKey(Proto.KeyShowWindow, showWindow ? "1" : "0");
                settingsShowWindow = showWindow ? (byte)1 : (byte)0;
            }
            if (Enum.TryParse(Cfg.ToggleHudKey, true, out kc) && Input.GetKeyDown(kc))
            {
                minimapIdx = (minimapIdx + 1) % (Cfg.MinimapSpans.Length + 1);
            }

            frame++;
            ReadWindowSettings();
            WriteState();
        }

        // 窗口可实时改小地图大小/生物开关/图层(设置文件)
        private DateTime ReadWindowSettings()
        {
            try
            {
                string p = Path.Combine(Cfg.BaseDir, Proto.SettingsFileName);
                if (!File.Exists(p)) return DateTime.MinValue;
                DateTime wt = File.GetLastWriteTimeUtc(p);
                if (wt == lastSettingsWrite) return wt;
                lastSettingsWrite = wt;
                Dictionary<string, string> d = ReadSettingsFile(p);
                string s;
                if (d.TryGetValue(Proto.KeyMinimapPixels, out s))
                {
                    int px;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out px) &&
                        px >= 120 && px <= 800 && px != Cfg.MinimapPixels)
                    {
                        Cfg.MinimapPixels = px;
                        BuildRingTexture(px);
                        Cfg.Log("minimap size set to " + px + "px by window");
                    }
                }
                if (d.TryGetValue(Proto.KeyShowCreatures, out s)) showCreatures = s != "0";
                if (d.TryGetValue(Proto.KeyWindowLayer, out s))
                {
                    int wi;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out wi)) windowLayer = wi;
                }
                byte sw = ReadSettingsByte(Proto.KeyShowWindow, 0);
                settingsShowWindow = sw;
                showWindow = sw != 0;
                return wt;
            }
            catch (Exception) { return DateTime.MinValue; }
        }

        private Dictionary<string, string> ReadSettingsFile(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(path)) return d;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception) { }
            return d;
        }

        private byte ReadSettingsByte(string key, byte def)
        {
            string s = ReadSettingsKey(key);
            byte v;
            return (s != null && byte.TryParse(s, out v)) ? v : def;
        }

        private string ReadSettingsKey(string key)
        {
            try
            {
                string p = Path.Combine(Cfg.BaseDir, Proto.SettingsFileName);
                if (!File.Exists(p)) return null;
                foreach (string ln in File.ReadAllLines(p))
                {
                    string line = ln.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() == key) return line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception) { }
            return null;
        }

        private void WriteSettingsKey(string key, string val)
        {
            try
            {
                string p = Path.Combine(Cfg.BaseDir, Proto.SettingsFileName);
                Dictionary<string, string> d = ReadSettingsFile(p);
                d[key] = val;
                List<string> outLines = new List<string>();
                foreach (KeyValuePair<string, string> kv in d) outLines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(p, outLines.ToArray());
            }
            catch (Exception) { }
        }

        private void WriteState()
        {
            if (frame % 3 != 0) return; // 20Hz
            try
            {
                byte[] buf = stateBuf;
                Array.Clear(buf, 0, buf.Length);
                PutInt(buf, Proto.OffMagic, Proto.Magic);
                PutInt(buf, Proto.OffVersion, Proto.Version);
                PutLong(buf, Proto.OffTick, Environment.TickCount);
                buf[Proto.OffShowWindow] = settingsShowWindow;

                Player pl = Player.main;
                int valid = 0;
                float x = 0f, y = 0f, z = 0f;
                if (pl != null && pl.transform != null)
                {
                    valid = 1;
                    Vector3 p = pl.transform.position;
                    x = p.x; y = -p.y; z = p.z;
                    PutFloat(buf, Proto.OffX, x);
                    PutFloat(buf, Proto.OffY, y);
                    PutFloat(buf, Proto.OffZ, z);
                    PutFloat(buf, Proto.OffHeading, HeadingAngle());
                }
                PutInt(buf, Proto.OffPlayerValid, valid);

                if (valid == 1 && frame % 30 == 0)
                {
                    string biome = null;
                    try { biome = Proto.BiomeCnOrNull(pl.GetBiomeString()); } catch (Exception) { }
                    byte[] b = biome == null ? new byte[0] : Encoding.UTF8.GetBytes(biome);
                    if (b.Length > 63) Array.Resize(ref b, 63);
                    PutInt(buf, Proto.OffBiomeLen, b.Length);
                    PutBytes(buf, Proto.OffBiome, b);
                    WriteBeacons(pl, buf);
                }
                PutInt(buf, Proto.OffCreatureCount, creatureCount);

                string sp = Path.Combine(Cfg.BaseDir, Proto.StateFileName);
                using (FileStream fs = new FileStream(sp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Write(buf, 0, buf.Length);
                }
            }
            catch (Exception ex)
            {
                if (frame % 600 == 0) Cfg.Log("write state failed: " + ex.Message);
            }
        }

        private static void PutInt(byte[] b, int off, int v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
        }

        private static void PutFloat(byte[] b, int off, float v)
        {
            PutInt(b, off, BitConverter.ToInt32(BitConverter.GetBytes(v), 0));
        }

        private static void PutLong(byte[] b, int off, long v)
        {
            for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (8 * i));
        }

        private static void PutBytes(byte[] b, int off, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            Buffer.BlockCopy(data, 0, b, off, data.Length);
        }

        private void WriteBeacons(Player pl, byte[] buf)
        {
            List<PingInstance> list = GetPings();
            if (list == null) return;
            Vector3 pp = pl.transform.position;
            List<PingInstance> sorted = new List<PingInstance>();
            foreach (PingInstance pi in list)
            {
                // PingType.Signal = 扫描室扫描目标: 只显示在小地图, 不发给大地图窗口
                if (pi != null && pi.visible && pi.pingType != PingType.Signal) sorted.Add(pi);
            }
            sorted.Sort(delegate(PingInstance a, PingInstance b)
            {
                Vector3 pa = a.GetPosition(), pb = b.GetPosition();
                return (pa - pp).sqrMagnitude.CompareTo((pb - pp).sqrMagnitude);
            });
            int n = Mathf.Min(sorted.Count, Proto.MaxBeacons);
            PutInt(buf, Proto.OffBeaconCount, n);
            for (int i = 0; i < n; i++)
            {
                PingInstance pi = sorted[i];
                Vector3 q = pi.GetPosition();
                int off = Proto.OffBeacons + i * 128;
                PutFloat(buf, off, q.x);
                PutFloat(buf, off + 4, q.z);
                PutInt(buf, off + 8, pi.colorIndex >= 0 ? pi.colorIndex : 0);
                PutInt(buf, off + 12, 1);
                string lbl = pi.GetLabel();
                byte[] b = string.IsNullOrEmpty(lbl) ? new byte[0] : Encoding.UTF8.GetBytes(lbl);
                if (b.Length > 63) Array.Resize(ref b, 63);
                PutInt(buf, off + 16, b.Length);
                PutBytes(buf, off + 20, b);
            }

            // 攻击性生物(每 60 帧=1秒 扫一次, 取最近 24 只, 600m 内)
            if (!showCreatures)
            {
                PutInt(buf, Proto.OffCreatureCount, 0);
                creatureCount = 0;
                return;
            }
            if (frame % 60 != 0 && lastCreatureFrame != 0 && frame - lastCreatureFrame < 60) return;
            lastCreatureFrame = frame;
            try
            {
                Creature[] all = UnityEngine.Object.FindObjectsOfType<Creature>();
                List<Creature> aggr = new List<Creature>();
                foreach (Creature c in all)
                {
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    if (c.GetComponent<AggressiveWhenSeeTarget>() == null && c.GetComponent<AttackLastTarget>() == null) continue;
                    if ((c.transform.position - pp).sqrMagnitude > 360000f) continue;
                    aggr.Add(c);
                }
                aggr.Sort(delegate(Creature a, Creature b)
                {
                    return (a.transform.position - pp).sqrMagnitude.CompareTo((b.transform.position - pp).sqrMagnitude);
                });
                int cn = Mathf.Min(aggr.Count, Proto.MaxCreatures);
                PutInt(buf, Proto.OffCreatureCount, cn);
                creatureCount = cn;
                for (int i = 0; i < cn; i++)
                {
                    Vector3 q = aggr[i].transform.position;
                    int off = Proto.OffCreatures + i * 80;
                    PutFloat(buf, off, q.x);
                    PutFloat(buf, off + 4, q.z);
                    string nm = CreatureName(aggr[i]);
                    byte[] nb = string.IsNullOrEmpty(nm) ? new byte[0] : Encoding.UTF8.GetBytes(nm);
                    if (nb.Length > 66) Array.Resize(ref nb, 66);
                    PutInt(buf, off + 8, nb.Length);
                    PutBytes(buf, off + 12, nb);
                }
            }
            catch (Exception ex)
            {
                PutInt(buf, Proto.OffCreatureCount, 0);
                creatureCount = 0;
                if (frame % 1800 == 0) Cfg.Log("creature scan failed: " + ex.Message);
            }
        }

        private int creatureCount;
        private int lastCreatureFrame;

        private static string CreatureName(Creature c)
        {
            try
            {
                TechTag tt = c.GetComponent<TechTag>();
                if (tt != null) return Language.main.Get(tt.type.AsString());
            }
            catch (Exception) { }
            return c.gameObject.name.Replace("(Clone)", "");
        }

        // ---------------------------------------------------------------- OnGUI

        private void OnGUI()
        {
            Player pl = Player.main;
            if (pl == null || pl.transform == null) return;
            EnsureStyles();

            Vector3 pos = pl.transform.position;
            float depth = -pos.y;
            try { depth = pl.GetDepth(); } catch (Exception) { }
            string biome = null;
            try { biome = Proto.BiomeCnOrNull(pl.GetBiomeString()); } catch (Exception) { }

            string dir8 = Heading8();
            string line;
            if (uiFontCjk)
            {
                line = biome == null
                    ? string.Format("X {0:F0}   Z {1:F0}   深度 {2:F0}m   {3}", pos.x, pos.z, depth, dir8)
                    : string.Format("X {0:F0}   Z {1:F0}   深度 {2:F0}m   {3}   {4}", pos.x, pos.z, depth, dir8, biome);
            }
            else
            {
                line = biome == null
                    ? string.Format("X {0:F0}   Z {1:F0}   Depth {2:F0}m   {3}", pos.x, pos.z, depth, dir8)
                    : string.Format("X {0:F0}   Z {1:F0}   Depth {2:F0}m   {3}   {4}", pos.x, pos.z, depth, dir8, biome);
            }
            LabelShadowed(new Rect(16, 12, 1200, 60), line, hudStyle);

            if (minimapIdx > 0)
            {
                MapLayer ml = MinimapLayer();
                if (ml != null && GetTex(ml) != null)
                    DrawMinimap(pl, ml);
            }
        }

        // ------------------------------------------------------------- layers

        private void LoadLayers()
        {
            layers.Clear();
            string dir = null;
            try { dir = Path.GetDirectoryName(typeof(SnMapBehaviour).Assembly.Location); } catch (Exception) { }
            string mapsDir = dir != null ? Path.Combine(dir, "maps") : null;

            if (mapsDir != null && Directory.Exists(mapsDir))
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
                        L.HasIniBounds = true;
                        L.Calibrated = true;
                    }
                    else
                    {
                        L.MinX = -Cfg.WorldRange; L.MaxX = Cfg.WorldRange;
                        L.MinZ = -Cfg.WorldRange; L.MaxZ = Cfg.WorldRange;
                    }
                    layers.Add(L);
                }
            }

            if (layers.Count == 0)
            {
                string[] legacy = new string[]
                {
                    dir != null ? Path.Combine(dir, "map.png") : null,
                    dir != null ? Path.Combine(dir, "map.jpg") : null
                };
                for (int i = 0; i < legacy.Length; i++)
                {
                    if (legacy[i] != null && File.Exists(legacy[i]))
                    {
                        MapLayer L = new MapLayer();
                        L.Path = legacy[i];
                        L.Name = uiFontCjk ? "主地图" : "map";
                        layers.Add(L);
                        break;
                    }
                }
            }
        }

        private MapLayer MinimapLayer()
        {
            // 优先跟随大地图窗口选中的图层; 未标定则回退到最近一个已标定层
            int wIdx = windowLayer;
            if (wIdx >= 0 && wIdx < layers.Count && layers[wIdx].Calibrated) return layers[wIdx];

            for (int i = 0; i < layers.Count; i++)
                if (layers[i].Calibrated) return layers[i];
            return layers.Count > 0 ? layers[0] : null;
        }

        private Texture2D GetTex(MapLayer L)
        {
            if (L == null) return null;
            if (L.Tex != null) return L.Tex;
            try
            {
                if (!File.Exists(L.Path)) return null;
                byte[] raw = File.ReadAllBytes(L.Path);
                Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                if (!tex.LoadImage(raw))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                L.Tex = tex;
                L.Width = tex.width;
                L.Height = tex.height;
                if (!L.HasIniBounds)
                {
                    float ar = L.Height > 0 ? (float)L.Width / L.Height : 1f;
                    L.Calibrated = ar > 0.9f && ar < 1.1f;
                }
                for (int i = 0; i < layers.Count; i++)
                {
                    if (layers[i] != L && layers[i].Tex != null)
                    {
                        UnityEngine.Object.Destroy(layers[i].Tex);
                        layers[i].Tex = null;
                    }
                }
                return tex;
            }
            catch (Exception ex)
            {
                Cfg.Log("load layer failed: " + L.Name + " (" + ex.Message + ")");
                return null;
            }
        }

        // ------------------------------------------------------------- minimap

        private void DrawMinimap(Player pl, MapLayer L)
        {
            float D = Mathf.Min(Cfg.MinimapPixels, Screen.height - 80f);
            Rect sq = new Rect(16f, 48f, D, D);
            float spanWorld = Cfg.MinimapSpans[Mathf.Clamp(minimapIdx - 1, 0, Cfg.MinimapSpans.Length - 1)];
            float su = spanWorld / (L.MaxX - L.MinX);
            float sv = spanWorld / (L.MaxZ - L.MinZ);

            Vector3 p = pl.transform.position;
            float uc = (p.x - L.MinX) / (L.MaxX - L.MinX);
            float vc = (p.z - L.MinZ) / (L.MaxZ - L.MinZ);
            float u0 = Mathf.Clamp(uc - su * 0.5f, 0f, 1f - su);
            float v0 = Mathf.Clamp(vc - sv * 0.5f, 0f, 1f - sv);

            // 圆形小地图: 逐列切片绘制, 圆外完全透明(露出游戏画面)
            float cx = D * 0.5f, cy = D * 0.5f, R = D * 0.5f;
            int cols = (int)D;
            for (int c = 0; c < cols; c++)
            {
                float dx = c + 0.5f - cx;
                float halfSq = R * R - dx * dx;
                if (halfSq <= 0.25f) continue;
                float half = Mathf.Sqrt(halfSq);
                if (half < 1f) half = 1f;
                float yy = cy - half;
                float hh = half * 2f;
                float u = u0 + (c / D) * su;
                float vTop = v0 + (yy / D) * sv;
                float vBot = v0 + ((yy + hh) / D) * sv;
                Rect uvr = new Rect(u, 1f - vBot, su / D, vBot - vTop);
                Rect sr = new Rect(sq.x + c, sq.y + yy, 1.05f, hh);
                GUI.DrawTextureWithTexCoords(sr, L.Tex, uvr, false);
            }
            if (ringTex != null) GUI.DrawTexture(sq, ringTex);

            Color[] colors = GetPingColors();
            List<PingInstance> list = GetPings();
            if (list != null)
            {
                float halfX = spanWorld * 0.5f, halfZ = spanWorld * 0.5f;
                for (int i = 0; i < list.Count; i++)
                {
                    PingInstance pi = list[i];
                    if (pi == null || !pi.visible) continue;
                    Vector3 q = pi.GetPosition();
                    float dx = q.x - p.x, dz = q.z - p.z;
                    if (Mathf.Abs(dx) > halfX || Mathf.Abs(dz) > halfZ) continue;
                    float sx = sq.x + (0.5f + dx / spanWorld) * D;
                    float sy = sq.y + (0.5f + dz / spanWorld) * D;
                    if (Vector2.Distance(new Vector2(sx, sy), sq.center) > D * 0.5f - 13f) continue;
                    bool isScan = pi.pingType == PingType.Signal;   // 扫描室扫描目标
                    if (isScan) GUI.color = new Color(1f, 0.62f, 0.1f);
                    else
                    {
                        int ci = pi.colorIndex >= 0 ? pi.colorIndex : 0;
                        GUI.color = colors[ci % colors.Length];
                    }
                    GUI.DrawTexture(new Rect(sx - 5f, sy - 5f, 10f, 10f), dotTex);
                    GUI.color = Color.white;
                    if (isScan)
                    {
                        string lbl = pi.GetLabel();
                        if (!string.IsNullOrEmpty(lbl))
                            LabelShadowed(new Rect(sx + 7f, sy - 7f, 200f, 18f), lbl, miniSignalStyle);
                    }
                }
            }

            float ang = HeadingAngle();
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, sq.center);
            GUI.DrawTexture(new Rect(sq.center.x - 9f, sq.center.y - 9f, 18f, 18f), arrowTex);
            GUI.matrix = old;

            LabelShadowed(new Rect(sq.center.x - 20f, sq.y + 4f, 40f, 20f), "N", smallStyle);
            LabelShadowed(new Rect(sq.x, sq.yMax + 2f, 340f, 22f),
                string.Format(uiFontCjk ? "{0}  范围 {1:F0}m  [{2}切换]" : "{0}  range {1:F0}m  [{2}]",
                    L.Name, spanWorld, Cfg.ToggleHudKey),
                smallStyle);
        }

        // ------------------------------------------------------------- helpers

        private void LabelShadowed(Rect rect, string text, GUIStyle style)
        {
            Color oc = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.95f);
            GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, style);
            style.normal.textColor = oc;
            GUI.Label(rect, text, style);
        }

        // 朝向: 用渲染相机 (Player.main.transform 不随视角旋转!)
        // 实测 MainCamera.camera 的 forward 与实际视线方向相反(经验修正 +180)
        private static float HeadingAngle()
        {
            Vector3 f = Vector3.zero;
            try
            {
                Camera cam = MainCamera.camera;
                if (cam != null && cam.transform != null) f = cam.transform.forward;
            }
            catch (Exception) { }
            if (f.sqrMagnitude < 0.000001f) return 0f;
            float a = Mathf.Atan2(f.x, -f.z) * Mathf.Rad2Deg + 180f;
            if (a < 0f) a += 360f;
            if (a >= 360f) a -= 360f;
            return a;
        }

        private static string Heading8()
        {
            return Headings[Mathf.RoundToInt(HeadingAngle() / 45f) % 8];
        }

        private void LoadFont()
        {
            string[] names = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" };
            for (int i = 0; i < names.Length; i++)
            {
                uiFont = Font.CreateDynamicFontFromOSFont(names[i], Cfg.FontSize);
                if (uiFont != null)
                {
                    uiFontCjk = i <= 2;
                    return;
                }
            }
        }

        private List<PingInstance> GetPings()
        {
            try
            {
                if (pingsDictField == null) return null;
                IDictionary dict = pingsDictField.GetValue(null) as IDictionary;
                if (dict == null) return null;
                List<PingInstance> list = new List<PingInstance>();
                foreach (object o in dict.Values)
                {
                    PingInstance pi = o as PingInstance;
                    if (pi != null) list.Add(pi);
                }
                return list;
            }
            catch (Exception) { return null; }
        }

        private Color[] GetPingColors()
        {
            if (pingColorsField != null)
            {
                try
                {
                    Color[] opts = pingColorsField.GetValue(null) as Color[];
                    if (opts != null && opts.Length > 0) return opts;
                }
                catch (Exception) { }
            }
            return FallbackPingColors;
        }

        private void LoadPingsApi()
        {
            try
            {
                pingsDictField = typeof(PingManager).GetField("pings", BindingFlags.NonPublic | BindingFlags.Static);
                pingColorsField = typeof(PingManager).GetField("colorOptions", BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception) { }
        }

        // 黑-银-黑 金属描边圆环, 圆内外均透明
        private void BuildRingTexture(int D)
        {
            try
            {
                Color32[] px = new Color32[D * D];
                float c = (D - 1) * 0.5f;
                float R = D * 0.5f;
                Color32 trans = new Color32(0, 0, 0, 0);
                Color32 blackOut = new Color32(5, 5, 8, 255);
                Color32 blackIn = new Color32(16, 16, 20, 255);
                for (int y = 0; y < D; y++)
                {
                    for (int x = 0; x < D; x++)
                    {
                        float dx = x - c, dy = y - c;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        Color32 col = trans;
                        if (d <= R + 0.5f && d >= R - 9.5f)
                        {
                            if (d >= R - 3f) col = blackOut;
                            else if (d >= R - 6f)
                            {
                                byte s = (byte)(208 - 52 * y / (D - 1));
                                col = new Color32(s, (byte)(s + 4), (byte)(s + 14), 255);
                            }
                            else col = blackIn;
                            if (d > R - 0.5f)
                                col.a = (byte)(255f * Mathf.Clamp(R + 0.5f - d, 0f, 1f));
                            if (d < R - 8.5f)
                                col.a = (byte)(255f * Mathf.Clamp(d - (R - 9.5f), 0f, 1f));
                        }
                        px[y * D + x] = col;
                    }
                }
                if (ringTex != null) UnityEngine.Object.Destroy(ringTex);
                ringTex = new Texture2D(D, D, TextureFormat.RGBA32, false);
                ringTex.SetPixels32(px);
                ringTex.Apply();
            }
            catch (Exception ex)
            {
                Cfg.Log("ring texture failed: " + ex.Message);
            }
        }

        // 与大地图窗口相同的风筝形箭头(顶点朝上), 红底黑边
        private void BuildArrowTexture()
        {
            int w = 26, h = 26;
            float[] xs = new float[] { 13f, 22f, 13f, 4f };
            float[] ys = new float[] { 0f, 23f, 16f, 23f };
            bool[] solid = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                float scanY = y + 0.5f;
                float minX = 1e9f, maxX = -1e9f;
                bool any = false;
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) % 4;
                    float y0 = ys[i], y1 = ys[j];
                    if ((y0 <= scanY && y1 > scanY) || (y1 <= scanY && y0 > scanY))
                    {
                        float t = (scanY - y0) / (y1 - y0);
                        float x = xs[i] + (xs[j] - xs[i]) * t;
                        any = true;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
                if (!any) continue;
                for (int x = 0; x < w; x++)
                {
                    float xm = x + 0.5f;
                    if (xm >= minX - 0.5f && xm <= maxX + 0.5f) solid[y * w + x] = true;
                }
            }

            Color32[] px = new Color32[w * h];
            Color32 main = new Color32(255, 70, 70, 235);
            Color32 edge = new Color32(10, 10, 10, 220);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (solid[idx]) { px[idx] = main; continue; }
                    bool near = false;
                    for (int dy2 = -1; dy2 <= 1 && !near; dy2++)
                        for (int dx2 = -1; dx2 <= 1 && !near; dx2++)
                        {
                            int nx = x + dx2, ny = y + dy2;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && solid[ny * w + nx]) near = true;
                        }
                    if (near) px[idx] = edge;
                }
            }
            arrowTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            arrowTex.SetPixels32(px);
            arrowTex.Apply();
        }

        private void BuildDotTexture()
        {
            int w = 16, h = 16;
            Color32[] px = new Color32[w * h];
            Color32 main = new Color32(255, 255, 255, 235);
            Color32 edge = new Color32(10, 10, 10, 220);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float d = Mathf.Sqrt((x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f));
                    px[y * w + x] = d <= 5.2f ? main : (d <= 6.6f ? edge : new Color32(0, 0, 0, 0));
                }
            }
            dotTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            dotTex.SetPixels32(px);
            dotTex.Apply();
        }

        private void EnsureStyles()
        {
            if (hudStyle != null) return;
            hudStyle = new GUIStyle(GUI.skin.label);
            if (uiFont != null) hudStyle.font = uiFont;
            hudStyle.fontSize = Cfg.FontSize;
            hudStyle.normal.textColor = Color.white;
            hudStyle.alignment = TextAnchor.UpperLeft;
            hudStyle.wordWrap = false;

            smallStyle = new GUIStyle(hudStyle);
            smallStyle.fontSize = Mathf.Max(12, Cfg.FontSize - 6);

            miniSignalStyle = new GUIStyle(smallStyle);
            miniSignalStyle.fontSize = Mathf.Max(11, Cfg.FontSize - 8);
        }
    }
}
