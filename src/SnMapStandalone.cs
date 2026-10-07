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
        public const string Version = "2.6";   // 模块版本: 日志 + 状态文件(窗口显示"模块vX.X")
        public static string ToggleMapKey = "F9";
        public static string ToggleHudKey = "F7";
        public static int FontSize = 20;
        public static float WorldRange = 2000f;
        public static int MinimapPixels = 360;
        public static float[] MinimapSpans = new float[] { 200f, 300f, 500f, 1000f };
        public static List<TechType> CreatureWhitelist;   // null=显示全部攻击性生物
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
                sb.AppendLine("# 小地图各档显示范围(米), 逗号分隔, F7 循环: 200->300->500->1000->关->200");
                sb.AppendLine("MinimapSpans=200,300,500,1000");
                sb.AppendLine("# 攻击性生物白名单(大地图红三角只显示这些), TechType 名逗号分隔, 删掉本行=全部显示");
                sb.AppendLine("# 大型: BoneShark Sandshark Stalker Crabsnake CrabSquid Warper Shocker SpineEel");
                sb.AppendLine("#       ReaperLeviathan GhostLeviathan GhostLeviatanVoid SeaDragon");
                sb.AppendLine("# 小型: Crash Biter Blighter CaveCrawler Mesmer LavaLizard LavaLarva Bleeder");
                sb.AppendLine("CreatureWhitelist=BoneShark,Sandshark,Stalker,Crabsnake,CrabSquid,Warper,Shocker,ReaperLeviathan,GhostLeviathan,GhostLeviatanVoid,SeaDragon");
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
                        case "CreatureWhitelist":
                            {
                                List<TechType> ts = new List<TechType>();
                                string[] cw = val.Split(new char[] { ',', ';' });
                                for (int k = 0; k < cw.Length; k++)
                                {
                                    try
                                    {
                                        TechType tt = (TechType)Enum.Parse(typeof(TechType), cw[k].Trim(), true);
                                        ts.Add(tt);
                                    }
                                    catch (Exception) { }
                                }
                                CreatureWhitelist = ts.Count > 0 ? ts : null;
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
        private Texture2D whiteTex;
        private Texture2D dotTex;
        private Texture2D ringTex;
        private Font uiFont;
        private bool uiFontCjk;
        private GUIStyle hudStyle;
        private GUIStyle smallStyle;
        private GUIStyle miniSignalStyle;
        private int minimapIdx = 1;   // 0=关闭档, 1..N 对应 MinimapSpans; F7: 200->300->500->1000->关->200
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
        private bool showScanSignals = true;                 // 扫描室扫描目标是否显示(小地图橙点)
        private List<string> creatureShow;                   // 设置窗口勾选"要显示"的物种(TechType 名); null 且 !showAll 时回落到 config.ini 白名单
        private bool creatureShowAll;                        // CreatureShow="*"
        private readonly List<string> seenSpecies = new List<string>();   // 见过的敌对物种, 供设置窗口列出"全部敌对生物"
        private KeyCode keyMap, keyHud;                      // 解析一次的热键
        private bool keyMapOk, keyHudOk;
        private byte[] modVerBytes;                          // 版本串字节(避免每次写状态都分配)
        private string biomeCnCache;                         // HUD 中文群系(节流, 不必每帧问游戏)
        private int biomeFrame = -1000;

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
            BuildWhiteTexture();
            BuildDotTexture();
            BuildRingTexture(Cfg.MinimapPixels);
            LoadLayers();
            LoadPingsApi();
            stateBuf = new byte[8192];
            modVerBytes = Encoding.UTF8.GetBytes(Cfg.Version);
            if (modVerBytes.Length > 32) Array.Resize(ref modVerBytes, 32);

            // 热键只在这里解析一次: 原来每帧两次 Enum.TryParse(反射+字符串比较)
            keyMapOk = Enum.TryParse(Cfg.ToggleMapKey, true, out keyMap);
            keyHudOk = Enum.TryParse(Cfg.ToggleHudKey, true, out keyHud);
            if (!keyMapOk) Cfg.Log("bad ToggleMapKey: " + Cfg.ToggleMapKey);
            if (!keyHudOk) Cfg.Log("bad ToggleHudKey: " + Cfg.ToggleHudKey);

            if (layers.Count > 0) GetTex(layers[0]);

            Cfg.Log("SNMap " + Cfg.Version + " awake | layers=" + layers.Count +
                    " cjk=" + uiFontCjk + " pings=" + (pingsDictField != null) +
                    " bigKey=" + Cfg.ToggleMapKey + " miniKey=" + Cfg.ToggleHudKey);
        }

        private void Update()
        {
            if (keyMapOk && Input.GetKeyDown(keyMap))
            {
                // 读-翻转-写 设置文件里的 ShowWindow(与地图窗口共用)
                byte cur = ReadSettingsByte(Proto.KeyShowWindow, 0);
                showWindow = cur == 0;
                WriteSettingsKey(Proto.KeyShowWindow, showWindow ? "1" : "0");
                settingsShowWindow = showWindow ? (byte)1 : (byte)0;
            }
            if (keyHudOk && Input.GetKeyDown(keyHud))
            {
                minimapIdx = (minimapIdx + 1) % (Cfg.MinimapSpans.Length + 1);   // +1 = 末尾关闭档
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
                if (d.TryGetValue(Proto.KeyShowScanSignals, out s)) showScanSignals = s != "0";
                if (d.TryGetValue(Proto.KeyCreatureShow, out s))
                {
                    // 设置窗口里按物种勾选的结果: 逗号分隔的 TechType 名; "*"=全部显示
                    string t = s.Trim();
                    if (t == "*") { creatureShowAll = true; creatureShow = null; }
                    else
                    {
                        creatureShowAll = false;
                        List<string> names = new List<string>();
                        string[] parts = t.Split(new char[] { ',', ';' });
                        for (int k = 0; k < parts.Length; k++)
                        {
                            string nm = parts[k].Trim();
                            if (nm.Length > 0) names.Add(nm);
                        }
                        creatureShow = names;   // 空列表 = 一个都不显示
                    }
                }
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

                // 模块版本串写进状态文件(窗口显示"模块vX.X"), 上限 32 字节(字节数组在 Awake 里备好)
                PutInt(buf, Proto.OffModVerLen, modVerBytes.Length);
                PutBytes(buf, Proto.OffModVer, modVerBytes);

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

                // 见过的敌对物种清单(设置窗口右侧"全部敌对生物"用), 每次写状态都带上,
                // 否则 19/20 次写入会在 Array.Clear 之后变成空清单
                int sc = Mathf.Min(seenSpecies.Count, Proto.MaxSpecies);
                PutInt(buf, Proto.OffSpeciesCount, sc);
                for (int i = 0; i < sc; i++)
                {
                    byte[] nb = Encoding.UTF8.GetBytes(seenSpecies[i]);
                    if (nb.Length > Proto.SpeciesStride - 4) Array.Resize(ref nb, Proto.SpeciesStride - 4);
                    int so = Proto.OffSpecies + i * Proto.SpeciesStride;
                    PutInt(buf, so, nb.Length);
                    PutBytes(buf, so + 4, nb);
                }

                string sp = Path.Combine(Cfg.BaseDir, Proto.StateFileName);
                // 就地覆盖, 不截断: 窗口每 33ms 读一次, 用 FileMode.Create 会先把文件截成 0 字节,
                // 读的那一头就会拿到半截文件 -> 生物"忽有忽无/坐标乱跳"。文件长度保持恒定后,
                // 撕裂读到的也只是相邻两帧的数据, 无害。
                using (FileStream fs = new FileStream(sp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                    if (fs.Length != buf.Length) fs.SetLength(buf.Length);
                    fs.Position = 0;
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

            // 攻击性生物(每 20 帧≈0.33秒 扫一次, 取最近 24 只, 600m 内)
            // 原来 60 帧(1秒)一次, 生物位置最多滞后 1 秒, 看起来就是"不刷新"
            if (!showCreatures)
            {
                PutInt(buf, Proto.OffCreatureCount, 0);
                creatureCount = 0;
                return;
            }
            if (frame % 20 != 0 && lastCreatureFrame != 0 && frame - lastCreatureFrame < 20) return;
            lastCreatureFrame = frame;
            try
            {
                Creature[] all = UnityEngine.Object.FindObjectsOfType<Creature>();
                List<Creature> aggr = new List<Creature>();
                foreach (Creature c in all)
                {
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    if (c.GetComponent<AggressiveWhenSeeTarget>() == null && c.GetComponent<AttackLastTarget>() == null) continue;
                    TechTag tg = c.GetComponent<TechTag>();
                    // 物种清单: 不管当前勾没勾显示, 见过就记下来(设置窗口要能列出全部敌对生物)
                    if (tg != null)
                    {
                        string sp = tg.type.ToString();
                        if (sp.Length > 0 && seenSpecies.Count < Proto.MaxSpecies && !seenSpecies.Contains(sp))
                            seenSpecies.Add(sp);
                    }
                    if (!CreatureVisible(tg)) continue;
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

        // 按设置窗口的勾选判断某物种是否显示:
        //   CreatureShow 键存在 -> 只显示列表里的(空 = 一个都不显示); "*" = 全部显示
        //   没有该键 -> 回落到 config.ini 的 CreatureWhitelist(空=全部)
        private bool CreatureVisible(TechTag tg)
        {
            if (creatureShowAll) return true;
            if (creatureShow != null) return tg != null && creatureShow.Contains(tg.type.ToString());
            if (Cfg.CreatureWhitelist == null) return true;
            return tg != null && Cfg.CreatureWhitelist.Contains(tg.type);
        }

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
            // 只在 Repaint 事件里画: Unity 每帧至少还会发一次 Layout 事件, 不守这一下等于所有绘制都做两遍
            // (小地图是按列切片画的, 300~800 次 DrawTexture 翻倍就是上千次/帧)
            if (Event.current.type != EventType.Repaint) return;

            Player pl = Player.main;
            if (pl == null || pl.transform == null) return;
            EnsureStyles();

            Vector3 pos = pl.transform.position;
            float depth = -pos.y;
            try { depth = pl.GetDepth(); } catch (Exception) { }
            // 群系字符串每次都从游戏取会分配字符串 + 查字典, 而它变得很慢: 4 Hz 刷新一次够了
            if (frame - biomeFrame >= 15)
            {
                biomeFrame = frame;
                try { biomeCnCache = Proto.BiomeCnOrNull(pl.GetBiomeString()); } catch (Exception) { biomeCnCache = null; }
            }
            string biome = biomeCnCache;

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

            if (minimapIdx > 0 && Cfg.MinimapSpans.Length > 0)
            {
                MapLayer ml = MinimapLayer();
                if (ml != null && GetTex(ml) != null)
                    DrawMinimap(pl, ml);
            }
            else
            {
                // 关闭档: 小地图整体不画(信号点/标签也一起藏), 只留一行提示, 免得以为工具坏了
                LabelShadowed(new Rect(16, 48, 400, 22),
                    uiFontCjk ? "小地图: 关  [F7 开启]" : "minimap: off  [F7]", smallStyle);
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

        // 窗口导出的小预览文件名(和大地图窗口约定)
        public static string MiniPreviewName(int layerIdx) { return "SNMapMini_" + layerIdx + ".jpg"; }

        // 小地图只需要一个圆, 但地图原图是 3240²/8192² —— 整张解码进游戏进程就是 40~256MB,
        // 每次切图层还要销毁重来一次(主线程卡顿 + 内存峰值)。
        // 所以优先加载【窗口导出的小预览】(窗口本来就要加载整张地图, 顺手导出 ≤3072 的 JPEG);
        // 没有预览(比如窗口没开过)才退回加载原图。
        private Texture2D GetTex(MapLayer L)
        {
            if (L == null) return null;
            if (L.Tex != null) return L.Tex;
            try
            {
                if (windowLayer >= 0 && windowLayer < layers.Count && layers[windowLayer] == L)
                {
                    string mini = Path.Combine(Cfg.BaseDir, MiniPreviewName(windowLayer));
                    // 预览比原图旧说明地图被换过 -> 老老实实读原图, 别显示过期的图
                    if (File.Exists(mini) && File.GetLastWriteTimeUtc(mini) >= File.GetLastWriteTimeUtc(L.Path))
                    {
                        Texture2D mt = LoadTexture(mini);
                        if (mt != null)
                        {
                            L.Tex = mt;
                            L.Width = mt.width;
                            L.Height = mt.height;
                            if (!L.HasIniBounds)
                            {
                                float ar = L.Height > 0 ? (float)L.Width / L.Height : 1f;
                                L.Calibrated = ar > 0.9f && ar < 1.1f;
                            }
                            ReleaseOtherTextures(L);
                            return mt;
                        }
                    }
                }

                if (!File.Exists(L.Path)) return null;
                Texture2D tex = LoadTexture(L.Path);
                if (tex == null) return null;
                L.Tex = tex;
                L.Width = tex.width;
                L.Height = tex.height;
                if (!L.HasIniBounds)
                {
                    float ar = L.Height > 0 ? (float)L.Width / L.Height : 1f;
                    L.Calibrated = ar > 0.9f && ar < 1.1f;
                }
                ReleaseOtherTextures(L);
                return tex;
            }
            catch (Exception ex)
            {
                Cfg.Log("load layer failed: " + L.Name + " (" + ex.Message + ")");
                return null;
            }
        }

        private static Texture2D LoadTexture(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!tex.LoadImage(raw))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            return tex;
        }

        private void ReleaseOtherTextures(MapLayer keep)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] != keep && layers[i].Tex != null)
                {
                    UnityEngine.Object.Destroy(layers[i].Tex);
                    layers[i].Tex = null;
                }
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

            // 全工具统一坐标系: +X=东=屏幕右, +Z=北=屏幕上; 地图图片上方=北(=MaxZ), 图片行号 r=(1-v)*H
            // 历史 bug: 这里曾把 v 轴上下写反, 于是小地图画的是"南北镜像"的位置(人在南半边, 图上显示北半边),
            //          叠加层(信号点)又用另一套约定, 两者还对不上。下面统一成一套。
            Vector3 p = pl.transform.position;
            float uc = (p.x - L.MinX) / (L.MaxX - L.MinX);
            float vc = (p.z - L.MinZ) / (L.MaxZ - L.MinZ);   // v: 自南边界起算的北向比例
            float u0 = Mathf.Clamp(uc - su * 0.5f, 0f, 1f - su);
            float v0 = Mathf.Clamp(vc - sv * 0.5f, 0f, 1f - sv);   // 窗口南边界(v)
            float winX0 = L.MinX + u0 * (L.MaxX - L.MinX);         // 窗口西边界(世界 x)
            float winZ0 = L.MinZ + v0 * (L.MaxZ - L.MinZ);         // 窗口南边界(世界 z)

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
                // 屏幕上方=北=v 大。贴图 texcoords 的 y 从图片底部量起, 所以 rect 的 y 取下方那条边
                float vLow = v0 + sv * (1f - (yy + hh) / D);
                float vHigh = v0 + sv * (1f - yy / D);
                Rect uvr = new Rect(u, vLow, su / D, vHigh - vLow);
                Rect sr = new Rect(sq.x + c, sq.y + yy, 1.05f, hh);
                GUI.DrawTextureWithTexCoords(sr, L.Tex, uvr, false);
            }
            if (ringTex != null) GUI.DrawTexture(sq, ringTex);

            Color[] colors = GetPingColors();
            List<PingInstance> list = GetPings();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    PingInstance pi = list[i];
                    if (pi == null || !pi.visible) continue;
                    Vector3 q = pi.GetPosition();
                    // 与地图底图用同一套世界→屏幕换算(窗口可能因贴边被 clamp, 所以按窗口原点算, 不按玩家算)
                    if (q.x < winX0 || q.x > winX0 + spanWorld || q.z < winZ0 || q.z > winZ0 + spanWorld) continue;
                    float sx = sq.x + (q.x - winX0) / spanWorld * D;
                    float sy = sq.y + (1f - (q.z - winZ0) / spanWorld) * D;
                    if (Vector2.Distance(new Vector2(sx, sy), sq.center) > D * 0.5f - 13f) continue;
                    bool isScan = pi.pingType == PingType.Signal;   // 扫描室扫描目标
                    if (isScan && !showScanSignals) continue;       // 设置窗口里可关掉
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

            // 玩家箭头: 位置按同一套换算(贴图层边缘被 clamp 时会离开圆心, 这样才对得上底图);
            // 方向不用 GUI 旋转矩阵(受 GUI 矩阵/缩放影响不可控), 自己在屏幕空间算三角形逐行填充
            float psx = sq.x + (p.x - winX0) / spanWorld * D;
            float psy = sq.y + (1f - (p.z - winZ0) / spanWorld) * D;
            DrawPlayerArrow(psx, psy, HeadingAngle());

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
        // 返回罗盘方位角: 0=北(+Z), 90=东(+X), 顺时针。atan2(东分量, 北分量) 才是方位角。
        // 旧代码写成 atan2(f.x, -f.z) + 180 (= -方位角) 是东西镜像的, 与地图对齐后必须改回。
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
            float a = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
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
        private void BuildWhiteTexture()
        {
            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
        }

        // 玩家箭头: 在屏幕空间自己算三角形 + 逐扫描线填充。
        // 不用 GUIUtility.RotateAroundPivot —— 旋转结果会受游戏 GUI 矩阵/缩放影响, 方向不可控(旧版实测偏移很大)。
        // bearingDeg: 0=北=屏幕上方, 顺时针为正。
        private void DrawPlayerArrow(float cx, float cy, float bearingDeg)
        {
            if (whiteTex == null) return;
            float rad = bearingDeg * Mathf.Deg2Rad;
            float dx = Mathf.Sin(rad), dy = -Mathf.Cos(rad);   // 屏幕方向(右=+x, 下=+y)
            float px = -dy, py = dx;                            // 方向的右手垂直
            float L = 9f, B = 5.5f, W = 6.5f;                   // 尖端前伸 / 尾部后缩 / 半宽(像素)
            float tx = cx + dx * L, ty = cy + dy * L;
            float ax = cx - dx * B + px * W, ay = cy - dy * B + py * W;
            float bx = cx - dx * B - px * W, by = cy - dy * B - py * W;
            float gx = (tx + ax + bx) / 3f, gy = (ty + ay + by) / 3f;
            const float k = 1.35f;                             // 先画放大一圈的暗色描边, 再画红色填充
            FillTriangle(gx + (tx - gx) * k, gy + (ty - gy) * k,
                         gx + (ax - gx) * k, gy + (ay - gy) * k,
                         gx + (bx - gx) * k, gy + (by - gy) * k,
                         new Color(0.04f, 0.04f, 0.04f, 0.9f));
            FillTriangle(tx, ty, ax, ay, bx, by, new Color(1f, 0.27f, 0.27f, 0.95f));
        }

        private void FillTriangle(float x0, float y0, float x1, float y1, float x2, float y2, Color col)
        {
            if (whiteTex == null) return;
            float yMin = Mathf.Min(y0, Mathf.Min(y1, y2));
            float yMax = Mathf.Max(y0, Mathf.Max(y1, y2));
            int r0 = Mathf.FloorToInt(yMin), r1 = Mathf.CeilToInt(yMax);
            Color old = GUI.color;
            GUI.color = col;
            for (int r = r0; r <= r1; r++)
            {
                float yc = r + 0.5f;
                float xMin = float.MaxValue, xMax = float.MinValue;
                EdgeX(x0, y0, x1, y1, yc, ref xMin, ref xMax);
                EdgeX(x1, y1, x2, y2, yc, ref xMin, ref xMax);
                EdgeX(x2, y2, x0, y0, yc, ref xMin, ref xMax);
                if (xMin > xMax) continue;
                GUI.DrawTexture(new Rect(xMin, r, Mathf.Max(1f, xMax - xMin), 1f), whiteTex);
            }
            GUI.color = old;
        }

        private static void EdgeX(float ax, float ay, float bx, float by, float yc, ref float xMin, ref float xMax)
        {
            if ((ay <= yc && by > yc) || (by <= yc && ay > yc))
            {
                float t = (yc - ay) / (by - ay);
                float x = ax + (bx - ax) * t;
                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
            }
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
