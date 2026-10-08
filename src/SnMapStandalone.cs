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
        public const string Version = "2.7m";  // 模块版本: 日志 + 状态文件; m=性能优化(注入后卡顿)
        public static string ToggleMapKey = "F9";
        public static string ToggleHudKey = "F7";
        public static int FontSize = 20;
        public static float WorldRange = 2000f;
        public static int MinimapPixels = 360;
        public static float[] MinimapSpans = new float[] { 200f, 300f, 500f, 1000f };
        public static List<TechType> CreatureWhitelist;   // null=显示全部攻击性生物
        public static bool DumpIcons;                      // config.ini: 一次性把游戏图标导出成 icons/<TechType>.png
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
                sb.AppendLine("# 一次性把游戏内图标导出到 icons 文件夹(1=开, 导出完自动停; 平时保持 0)");
                sb.AppendLine("DumpIcons=0");
                try { File.WriteAllText(path, sb.ToString()); } catch (Exception) { }
                return;
            }

            try
            {
                string[] lines = Proto.ReadAllLinesShared(path);
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
                        case "DumpIcons":
                            {
                                string dv = val.Trim();
                                DumpIcons = dv == "1" || dv.ToLower() == "true";
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
        private GUIStyle alertBlack, alertRed;   // 没头像的生物: 黑描边红感叹号
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
        private string hudLineCache;                         // HUD 那行文字(10Hz 拼一次)
        private int hudLineFrame = -1000;
        private readonly Dictionary<string, string> labelToTech = new Dictionary<string, string>();
        private bool labelMapBuilt;
        private readonly Dictionary<string, Texture2D> iconCache = new Dictionary<string, Texture2D>();

        // ---------------- v2.7m 性能相关(注入后卡顿) ----------------
        private string statePathCache, settingsPathCache;      // 路径缓存: 别再每帧 Path.Combine
        private FileStream stateFs;                            // 状态文件常开句柄(不再每秒新建 20 个文件)
        private readonly byte[] speciesBlock = new byte[Proto.MaxSpecies * Proto.SpeciesStride];
        private int speciesBlockCount = -1;                    // 物种清单没变就不重新编码
        private readonly List<PingInstance> pingsCache = new List<PingInstance>();
        private int pingsCacheFrame = -100000;                 // 小地图每帧都要 ping 列表 -> 0.2 秒缓存一次
        private readonly List<Creature> aggrCache = new List<Creature>();
        private MapRoomFunctionality[] roomsCache;
        private int roomsCacheFrame = -100000;                 // 扫描室很少变, 10 秒重扫一次
        private MethodInfo roomNodesM;                         // MapRoomFunctionality.GetNodes()
        private bool roomNodesTried;
        // 自检: 每 5 秒把各段耗时打到 SNMap.log(v2.7m 起)
        private float perfStateMs, perfBeaconMs, perfCreatureMs, perfDrawMs;
        private int perfFrames;

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
            if (Cfg.DumpIcons && !dumpDone) DumpIconsTick();
            WriteState();
        }

        // 窗口可实时改小地图大小/生物开关/图层(设置文件)
        private DateTime ReadWindowSettings()
        {
            // v2.7m: 原来每帧都 File.Exists + GetLastWriteTimeUtc(每秒 120 次文件系统调用),
            // 改成 ~10Hz 检查一次, 设置窗口的响应速度感觉不出差别。
            if (frame % 6 != 0) return lastSettingsWrite;
            try
            {
                string p = SettingsPath();
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
                string[] lines = Proto.ReadAllLinesShared(path);
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
                foreach (string ln in Proto.ReadAllLinesShared(p))
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
            // 读-改-写整个文件, 而窗口也在写同一个文件(而且两边都是读-改-写):
            // 用"先写 .tmp 再原子替换" + 写完回读校验, 键丢了就重试,
            // 否则一次撞车就会把对方的键抹掉(实测: ShowCreatures 被抹掉后设置窗口里怎么点都不生效)。
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    string p = Path.Combine(Cfg.BaseDir, Proto.SettingsFileName);
                    Dictionary<string, string> d = ReadSettingsFile(p);
                    d[key] = val;
                    List<string> outLines = new List<string>();
                    foreach (KeyValuePair<string, string> kv in d) outLines.Add(kv.Key + "=" + kv.Value);
                    Proto.WriteAllLinesAtomic(p, outLines.ToArray());
                    string back = ReadSettingsKey(key);
                    if (back == val) return;
                }
                catch (Exception) { }
                System.Threading.Thread.Sleep(20);
            }
        }

        private void WriteState()
        {
            if (frame % 3 != 0) return; // 20Hz
            float tStart = Time.realtimeSinceStartup;
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
                    float tb = Time.realtimeSinceStartup;
                    WriteBeacons(pl, buf);
                    float db = (Time.realtimeSinceStartup - tb) * 1000f;
                    if (db > perfBeaconMs) perfBeaconMs = db;
                }
                PutInt(buf, Proto.OffCreatureCount, creatureCount);
                // 位置每帧实时刷新(生物引用是 1 秒前扫出来的集合, 但坐标必须跟手, 否则标记会一顿一顿地跳)
                for (int i = 0; i < trackedCreatures.Count; i++)
                {
                    Creature c = trackedCreatures[i];
                    if (c == null) continue;
                    Vector3 cq = c.transform.position;
                    PutFloat(creatureRec, i * 80, Q(cq.x));
                    PutFloat(creatureRec, i * 80 + 4, Q(cq.z));
                }
                Buffer.BlockCopy(creatureRec, 0, buf, Proto.OffCreatures, creatureRec.Length);   // 每帧原样写回

                // 见过的敌对物种清单(设置窗口右侧"全部敌对生物"用), 每次写状态都带上,
                // 否则 19/20 次写入会在 Array.Clear 之后变成空清单。
                // v2.7m: 以前每写一次(20Hz)就把所有物种名重新 UTF8 编码一遍 = 每秒几百次小对象分配,
                // 现在只在清单长度变化时编码一次, 之后整块内存拷贝。
                int sc = Mathf.Min(seenSpecies.Count, Proto.MaxSpecies);
                if (sc != speciesBlockCount)
                {
                    speciesBlockCount = sc;
                    Array.Clear(speciesBlock, 0, speciesBlock.Length);
                    for (int i = 0; i < sc; i++)
                    {
                        byte[] nb = Encoding.UTF8.GetBytes(seenSpecies[i]);
                        if (nb.Length > Proto.SpeciesStride - 4) Array.Resize(ref nb, Proto.SpeciesStride - 4);
                        int so = i * Proto.SpeciesStride;
                        PutInt(speciesBlock, so, nb.Length);
                        PutBytes(speciesBlock, so + 4, nb);
                    }
                }
                PutInt(buf, Proto.OffSpeciesCount, sc);
                if (sc > 0) Buffer.BlockCopy(speciesBlock, 0, buf, Proto.OffSpecies, sc * Proto.SpeciesStride);

                string sp = StatePath();
                // v2.7m: 改回"常开句柄 + 定长就地覆写"。
                // 之前为了避免撕裂读用了"写 .tmp 再 File.Replace", 但那是每秒 20 次
                // 新建文件 + 原子替换 + Flush —— 在开了实时杀毒/索引的机器上, 每秒 20 个新文件
                // 会让文件系统过滤驱动反复介入, 表现就是游戏周期性卡顿。
                // 定长就地覆盖不会截断文件(窗口不会读到 0 字节), 撕裂读最多混进相邻一帧的数据,
                // 这个已经由"生物记录持久化 + 小地图像素吸附"兜住了。
                try
                {
                    if (stateFs == null)
                    {
                        stateFs = new FileStream(sp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                        if (stateFs.Length != buf.Length) stateFs.SetLength(buf.Length);
                    }
                    stateFs.Position = 0;
                    stateFs.Write(buf, 0, buf.Length);
                }
                catch (Exception)
                {
                    // 句柄坏了(文件被删/被独占) -> 丢掉句柄下次重建
                    try { if (stateFs != null) stateFs.Dispose(); } catch (Exception) { }
                    stateFs = null;
                }

                // 自检: 每 5 秒(20Hz 下 100 次)打一行各段最坏耗时, 便于下次直接看数据
                float dtState = (Time.realtimeSinceStartup - tStart) * 1000f;
                if (dtState > perfStateMs) perfStateMs = dtState;
                perfFrames++;
                if (perfFrames >= 100)
                {
                    Cfg.Log("perf: state=" + perfStateMs.ToString("F1") + "ms(含写信标/生物) beacon=" +
                            perfBeaconMs.ToString("F1") + "ms draw=" + perfDrawMs.ToString("F1") + "ms");
                    perfStateMs = 0f; perfBeaconMs = 0f; perfCreatureMs = 0f; perfDrawMs = 0f; perfFrames = 0;
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

        // 免分配地写 float。
        // BitConverter.GetBytes(float) 每次都会 new 一个 byte[4], 而一次状态写入要写上百个 float
        // (玩家 4 + 生物最多 48 + 信标/物品点最多 64), 20Hz 下就是每秒两千多个小对象 ->
        // Unity 的 GC 是 Boehm 不分代、一次全停, 分配一多就变成周期性卡顿。
        // 这里用 Buffer.BlockCopy(float[]->byte[]) 走原生内存拷贝, 零分配。
        private static readonly float[] fScratch = new float[1];
        private static readonly byte[] bScratch = new byte[4];

        private static void PutFloat(byte[] b, int off, float v)
        {
            fScratch[0] = v;
            Buffer.BlockCopy(fScratch, 0, bScratch, 0, 4);
            b[off] = bScratch[0]; b[off + 1] = bScratch[1]; b[off + 2] = bScratch[2]; b[off + 3] = bScratch[3];
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

        // 路径缓存(WriteState 每秒调用 20 次, Path.Combine 每次都分配字符串)
        private string StatePath()
        {
            if (statePathCache == null) statePathCache = Path.Combine(Cfg.BaseDir, Proto.StateFileName);
            return statePathCache;
        }

        private string SettingsPath()
        {
            if (settingsPathCache == null) settingsPathCache = Path.Combine(Cfg.BaseDir, Proto.SettingsFileName);
            return settingsPathCache;
        }

        private void WriteBeacons(Player pl, byte[] buf)
        {
            List<PingInstance> list = GetPings();
            if (list == null) return;
            Vector3 pp = pl.transform.position;
            List<PingInstance> sorted = new List<PingInstance>();
            foreach (PingInstance pi in list)
            {
                if (pi == null || !pi.visible) continue;
                // PingType.Signal = 扫描室扫描目标; v4 起也发给大地图(带物品图标), 但设置里可以关
                if (pi.pingType == PingType.Signal && !showScanSignals) continue;
                sorted.Add(pi);
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
                bool isScan = pi.pingType == PingType.Signal;
                int off = Proto.OffBeacons + i * 128;
                PutFloat(buf, off, Q(q.x));
                PutFloat(buf, off + 4, Q(q.z));
                PutInt(buf, off + 8, pi.colorIndex >= 0 ? pi.colorIndex : 0);
                PutInt(buf, off + 12, 1);
                string lbl = pi.GetLabel();
                byte[] b = string.IsNullOrEmpty(lbl) ? new byte[0] : Encoding.UTF8.GetBytes(lbl);
                if (b.Length > 63) Array.Resize(ref b, 63);
                PutInt(buf, off + 16, b.Length);
                PutBytes(buf, off + 20, b);
                // 图标键: 扫描信号只有本地化名字(石灰岩块...), 反查成 TechType(Limestone) 才能找到内置图标
                string key = isScan ? IconKeyForLabel(lbl) : null;
                byte[] kb = string.IsNullOrEmpty(key) ? new byte[0] : Encoding.UTF8.GetBytes(key);
                if (kb.Length > 32) Array.Resize(ref kb, 32);
                PutInt(buf, off + 84, kb.Length);
                PutBytes(buf, off + 88, kb);
                buf[off + 120] = isScan ? (byte)1 : (byte)0;
            }

            // 扫描室物品点: 排在真实 ping 之后, 占剩下的槽位(kind=2, 带物品图标键)
            CollectScannerNodes(pp);
            int total = n;
            for (int i = 0; i < scanNodePos.Count && total < Proto.MaxBeacons; i++, total++)
            {
                int off = Proto.OffBeacons + total * 128;
                Vector3 q = scanNodePos[i];
                PutFloat(buf, off, Q(q.x));
                PutFloat(buf, off + 4, Q(q.z));
                PutInt(buf, off + 8, 0);
                PutInt(buf, off + 12, 1);
                string nm = scanNodeName[i];
                byte[] b = string.IsNullOrEmpty(nm) ? new byte[0] : Encoding.UTF8.GetBytes(nm);
                if (b.Length > 63) Array.Resize(ref b, 63);
                PutInt(buf, off + 16, b.Length);
                PutBytes(buf, off + 20, b);
                byte[] kb2 = Encoding.UTF8.GetBytes(scanNodeKey[i]);
                if (kb2.Length > 32) Array.Resize(ref kb2, 32);
                PutInt(buf, off + 84, kb2.Length);
                PutBytes(buf, off + 88, kb2);
                buf[off + 120] = 2;
            }
            PutInt(buf, Proto.OffBeaconCount, total);

            // 攻击性生物(每 20 帧≈0.33秒 扫一次, 取最近 24 只, 600m 内)
            // 原来 60 帧(1秒)一次, 生物位置最多滞后 1 秒, 看起来就是"不刷新"
            if (!showCreatures)
            {
                PutInt(buf, Proto.OffCreatureCount, 0);
                creatureCount = 0;
                trackedCreatures.Clear();
                trackedKeys.Clear();
                return;
            }
            // v2.7m: 生物"有哪些"每 2 秒重扫一次就够(坐标是每帧实时取的, 显示依旧跟手),
            // 原来 ~1 秒一次; FindObjectsOfType<Creature>() 要把场景对象都走一遍, 是这里最大的一笔开销。
            if (frame % 40 != 0 && lastCreatureFrame != 0 && frame - lastCreatureFrame < 40) return;
            lastCreatureFrame = frame;
            float tCre = Time.realtimeSinceStartup;
            try
            {
                Creature[] all = UnityEngine.Object.FindObjectsOfType<Creature>();
                List<Creature> aggr = aggrCache;      // 复用, 不再每次扫描 new 一个 List
                aggr.Clear();
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
                trackedCreatures.Clear();
                trackedKeys.Clear();
                for (int i = 0; i < Proto.MaxCreatures; i++) PutInt(creatureRec, i * 80 + 8, 0);   // 先清旧的标签长度
                for (int i = 0; i < cn; i++)
                {
                    Vector3 q = aggr[i].transform.position;
                    int off = i * 80;
                    PutFloat(creatureRec, off, Q(q.x));
                    PutFloat(creatureRec, off + 4, Q(q.z));
                    string nm = CreatureName(aggr[i]);
                    byte[] nb = string.IsNullOrEmpty(nm) ? new byte[0] : Encoding.UTF8.GetBytes(nm);
                    if (nb.Length > 32) Array.Resize(ref nb, 32);
                    PutInt(creatureRec, off + 8, nb.Length);
                    PutBytes(creatureRec, off + 12, nb);
                    // 图标键 = TechType 名(对应内置的 icons/<key>.png), 大地图窗口拿它画生物头像
                    TechTag tgi = aggr[i].GetComponent<TechTag>();
                    string key = tgi != null ? tgi.type.ToString() : null;
                    byte[] kb = string.IsNullOrEmpty(key) ? new byte[0] : Encoding.UTF8.GetBytes(key);
                    if (kb.Length > 32) Array.Resize(ref kb, 32);
                    PutInt(creatureRec, off + 44, kb.Length);
                    PutBytes(creatureRec, off + 48, kb);
                    // 小地图/大地图绘制用: 保留生物引用, 位置每帧实时取(见 WriteState 里的刷新)
                    trackedCreatures.Add(aggr[i]);
                    trackedKeys.Add(key);
                }
                float dc = (Time.realtimeSinceStartup - tCre) * 1000f;
                if (dc > perfCreatureMs) perfCreatureMs = dc;
            }
            catch (Exception ex)
            {
                // v2.7m: 出异常不再把数量清零(那会让地图上的生物整批闪一下);
                // 保留上一次的结果, 位置刷新照旧, 最多是"集合"晚一点更新。
                if (frame % 1800 == 0) Cfg.Log("creature scan failed: " + ex.Message);
            }
        }

        private int creatureCount;
        private int lastCreatureFrame;
        // 上一次扫描到的生物记录 + 给绘制用的生物引用。
        // 状态文件每帧都会 Array.Clear, 而"有哪些生物"只在每 60 帧(≈1秒)扫一次 ->
        // 记录要持久保存每帧原样写回; 而且位置必须每帧从 transform 实时取,
        // 否则地图在平滑滚动、标记却 1 秒才动一次, 看起来就是"标记一直在抖"。
        private readonly byte[] creatureRec = new byte[Proto.MaxCreatures * 80];
        private readonly List<Creature> trackedCreatures = new List<Creature>();
        private readonly List<string> trackedKeys = new List<string>();

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

        // ---- 扫描室(Scanner Room)物品点 ----
        // 游戏里那些"某某物品在哪"的点不走 PingManager(PingType 里根本没有资源类型),
        // 真正数据在 MapRoomFunctionality / ResourceTrackerDatabase 里, 而且 ResourceInfo 是 internal,
        // 所以下面用反射取节点。全部 try/catch 兜住: 取不到就当作没有, 不影响其它功能。
        private readonly List<Vector3> scanNodePos = new List<Vector3>();
        private readonly List<string> scanNodeKey = new List<string>();
        private readonly List<string> scanNodeName = new List<string>();
        private int lastScannerFrame;
        private int scanErrLogged;
        private bool scannerApiReady;
        private Type riType;                    // ResourceTrackerDatabase/ResourceInfo
        private MethodInfo getNodesM;           // static ICollection<ResourceInfo> GetNodes(TechType)
        private FieldInfo riTechF, riPosF;
        private int scanRawTotal;               // 诊断: 数据库返回的原始节点数(过滤前)

        private void CollectScannerNodes(Vector3 pp)
        {
            scanNodePos.Clear();
            scanNodeKey.Clear();
            scanNodeName.Clear();
            if (frame % 30 != 0 && lastScannerFrame != 0 && frame - lastScannerFrame < 30) return;
            lastScannerFrame = frame;
            scanRawTotal = 0;                     // v2.7m: 这个计数以前从不清零, 日志里的数字一直累加, 没法看
            try
            {
                // v2.7m: 扫描室很少变(而且 FindObjectsOfType 每次都要遍历场景对象), 10 秒重扫一次;
                // 数组里全是已销毁对象的假 null 时立刻重扫。
                bool needRescan = roomsCache == null || frame - roomsCacheFrame > 600;
                if (!needRescan)
                {
                    bool any = false;
                    for (int i = 0; i < roomsCache.Length; i++) { if (roomsCache[i] != null) { any = true; break; } }
                    if (!any) needRescan = true;
                }
                if (needRescan)
                {
                    roomsCache = UnityEngine.Object.FindObjectsOfType<MapRoomFunctionality>();
                    roomsCacheFrame = frame;
                }
                MapRoomFunctionality[] rooms = roomsCache;
                if (rooms == null || rooms.Length == 0)
                {
                    if (frame % 600 == 0) Cfg.Log("scanner: 场景里没找到扫描室");
                    return;
                }

                if (!scannerApiReady)
                {
                    scannerApiReady = true;
                    MethodInfo[] ms = typeof(ResourceTrackerDatabase).GetMethods(BindingFlags.Public | BindingFlags.Static);
                    for (int i = 0; i < ms.Length; i++)
                    {
                        if (ms[i].Name != "GetNodes") continue;
                        // 只认"单参数 GetNodes(TechType)"这一版: 它直接返回该类型的全部节点(带返回值),
                        // 而 4 参数那版实测在 (房间位置, 300m) 下返回空集合, 语义靠不住。
                        if (ms[i].GetParameters().Length == 1 && ms[i].ReturnType != typeof(void)) { getNodesM = ms[i]; break; }
                    }
                    if (getNodesM != null)
                    {
                        Type ret = getNodesM.ReturnType;
                        Type[] ga = ret.IsGenericType ? ret.GetGenericArguments() : null;
                        if (ga != null && ga.Length == 1)
                        {
                            riType = ga[0];
                            riTechF = riType.GetField("techType");
                            riPosF = riType.GetField("position");
                        }
                    }
                    // v2.7m: 首选扫描室自己的节点表(MapRoomFunctionality.GetNodes()) —— 里面只有
                    // 它范围内扫到的那些点(几十个); 全局数据库那版要遍历"全世界该类型的所有节点"
                    // (实测上千个, 每个还要两次反射取值), 是注入后卡顿的主要来源之一。
                    if (!roomNodesTried)
                    {
                        roomNodesTried = true;
                        try
                        {
                            MethodInfo m = typeof(MapRoomFunctionality).GetMethod("GetNodes");
                            if (m != null && m.GetParameters().Length == 0) roomNodesM = m;
                        }
                        catch (Exception) { }
                    }
                    Cfg.Log("scanner api: GetNodes(TechType)=" + (getNodesM != null) + " room.GetNodes()=" + (roomNodesM != null) +
                            " resourceInfo=" + (riType != null));
                }
                if (riType == null || riPosF == null) return;

                // 每个扫描室只显示它"当前正在扫描的那一个物品"(用户要求):
                // 一个房间同一时间只扫一种东西, 多个房间各扫各的 -> 逐个房间取它 GetActiveTechType() 对应的节点。
                // 之前是"把房间里所有可扫描类型全铺出来", 所以小地图上会有一大堆不相干的点。
                int activeRooms = 0;
                int perRoom = 16;
                bool diag = frame % 300 == 0;      // 每 5 秒打一次逐房间诊断
                for (int r = 0; r < rooms.Length && scanNodePos.Count < 48; r++)
                {
                    MapRoomFunctionality room = rooms[r];
                    if (room == null) continue;
                    Vector3 rp;
                    TechType act;
                    float range;
                    try
                    {
                        rp = room.transform.position;
                        act = room.GetActiveTechType();
                        range = room.GetScanRange();
                    }
                    catch (Exception ex)
                    {
                        if (scanErrLogged < 3) { scanErrLogged++; Cfg.Log("房间读取失败: " + ex.Message); }
                        continue;
                    }
                    int roomDist = Mathf.RoundToInt(Mathf.Sqrt((rp - pp).sqrMagnitude));
                    // 不再按"离玩家多远"筛房间: 远处基地扫出来的东西同样有价值(大地图可以平移过去看),
                    // 小地图那边本来就会按自己的显示窗口裁切 -> 放开, 每个正在扫描的房间都收。
                    if (act == TechType.None)
                    {
                        if (diag) Cfg.Log("  room#" + r + " 距离=" + roomDist + "m 空闲(没在扫描)");
                        continue;
                    }

                    activeRooms++;
                    string key = act.ToString();
                    string cn = null;
                    try { cn = Language.main.Get(act.AsString()); } catch (Exception) { }
                    if (string.IsNullOrEmpty(cn)) cn = key;

                    System.Collections.IEnumerable all = null;
                    // 优先用房间自己的列表(小、已经按房间范围筛过); 拿不到才退回全局数据库
                    if (roomNodesM != null)
                    {
                        try { all = roomNodesM.Invoke(room, null) as System.Collections.IEnumerable; }
                        catch (Exception) { }
                    }
                    if (all == null && getNodesM != null)
                    {
                        try { all = getNodesM.Invoke(null, new object[] { act }) as System.Collections.IEnumerable; }
                        catch (Exception ex) { if (scanErrLogged < 3) { scanErrLogged++; Cfg.Log("GetNodes invoke fail: " + ex.Message); } }
                    }
                    if (all == null) continue;

                    float lim = range + 60f;
                    bool fromRoom = roomNodesM != null;
                    int taken = 0;
                    foreach (object info in all)
                    {
                        if (info == null || taken >= perRoom || scanNodePos.Count >= 48) break;
                        scanRawTotal++;
                        if (!fromRoom)
                        {
                            TechType nt = (TechType)riTechF.GetValue(info);
                            if (nt != act) continue;                           // 全局库里要挑类型
                        }
                        Vector3 pos = (Vector3)riPosF.GetValue(info);
                        if (fromRoom)
                        {
                            // 房间的表就是它扫描范围内的点, 但保险起见还是按范围夹一下
                            if ((pos - rp).sqrMagnitude > lim * lim) continue;
                        }
                        scanNodePos.Add(pos);
                        scanNodeKey.Add(key);
                        scanNodeName.Add(cn);
                        taken++;
                    }
                    if (diag) Cfg.Log("  room#" + r + " 距离=" + roomDist + "m 正在扫=" + key +
                                      " range=" + Mathf.RoundToInt(range) + " 取到=" + taken);
                }
                if (frame % 300 == 0)
                    Cfg.Log("scanner: rooms=" + rooms.Length + " 在扫描=" + activeRooms +
                            " 原始节点=" + scanRawTotal + " 采用=" + scanNodePos.Count +
                            " 物品=" + (scanNodeKey.Count > 0 ? scanNodeKey[0] : "-"));
            }
            catch (Exception ex)
            {
                if (scanErrLogged < 3) { scanErrLogged++; Cfg.Log("scanner nodes failed: " + ex.Message); }
            }
        }

        // 坐标降精度到 1 位小数(用户要求): 标记点不需要亚分米精度, 量化后写进状态文件的数值稳定,
        // 大地图那头的"按坐标签名判断要不要重绘"也不会因为普朗克级抖动而反复重画。
        private static float Q(float v)
        {
            return Mathf.Round(v * 10f) / 10f;
        }

        // 建立"本地化物品名 -> TechType 名"反查表(扫描室的 ping 只有中文名, 没有 TechType)。
        // 只在第一次需要时建一次, 之后走字典。
        private string IconKeyForLabel(string lbl)
        {
            if (string.IsNullOrEmpty(lbl)) return null;
            if (!labelMapBuilt)
            {
                labelMapBuilt = true;
                try
                {
                    Array vals = Enum.GetValues(typeof(TechType));
                    for (int i = 0; i < vals.Length; i++)
                    {
                        TechType tt = (TechType)vals.GetValue(i);
                        string cn = null;
                        try { cn = Language.main.Get(tt.AsString()); } catch (Exception) { }
                        if (!string.IsNullOrEmpty(cn) && !labelToTech.ContainsKey(cn)) labelToTech[cn] = tt.ToString();
                    }
                    Cfg.Log("icon label map built: " + labelToTech.Count + " 条");
                }
                catch (Exception ex) { Cfg.Log("icon label map failed: " + ex.Message); }
            }
            string k;
            return labelToTech.TryGetValue(lbl, out k) ? k : null;
        }

        // ================= 一次性图标导出(config.ini: DumpIcons=1) =================
        // 目的: 把游戏里的 UI 图标/生物头像导成 icons/<TechType>.png, 之后内置进发布包, 运行时就不用再抠图。
        // 离线抽不到的(矿石类 UI 图标在图集里, 但图集贴图不跟着 bundle 走)就靠这里补齐。
        // 每帧只处理几个, 避免主线程卡顿; 已存在的文件跳过(不覆盖离线抽到的那批正确图标)。
        private int dumpIdx;
        private bool dumpDone;
        private int dumpWrote, dumpSkip, dumpFail, dumpNoSprite, dumpNoTex;
        private static string lastDumpErr;
        private static MethodInfo pdascanGet, pdaencyGet;

        private void DumpIconsTick()
        {
            if (dumpDone) return;
            try
            {
                Array vals = Enum.GetValues(typeof(TechType));
                if (dumpIdx == 0)
                {
                    Cfg.Log("icon dump: start, TechType=" + vals.Length +
                            " spriteManager=" + (SpriteManager.hasInitialized ? "ready" : "not-ready"));
                    try
                    {
                        string d = Path.Combine(Cfg.BaseDir, "icons");
                        if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                    }
                    catch (Exception) { }
                }
                if (!SpriteManager.hasInitialized && dumpIdx > 0) return;

                int burst = 0;
                while (dumpIdx < vals.Length && burst < 3)
                {
                    TechType tt = (TechType)vals.GetValue(dumpIdx);
                    dumpIdx++;
                    burst++;
                    string name = tt.ToString();
                    if (name == "None") continue;
                    string path = Path.Combine(Cfg.BaseDir, Path.Combine("icons", name + ".png"));
                    if (File.Exists(path)) { dumpSkip++; continue; }
                    Sprite sp = IconSprite(tt);
                    if (sp == null) { dumpFail++; dumpNoSprite++; continue; }
                    Texture2D tex = SpriteToReadable(sp);
                    if (tex == null) { dumpFail++; dumpNoTex++; continue; }
                    try
                    {
                        byte[] png = tex.EncodeToPNG();
                        if (png != null && png.Length > 0) { File.WriteAllBytes(path, png); dumpWrote++; }
                        else dumpFail++;
                    }
                    catch (Exception) { dumpFail++; }
                    finally { UnityEngine.Object.Destroy(tex); }
                }

                if (dumpIdx >= vals.Length)
                {
                    dumpDone = true;
                    Cfg.Log("icon dump: done wrote=" + dumpWrote + " skipped=" + dumpSkip + " failed=" + dumpFail +
                            " (无sprite=" + dumpNoSprite + " 有sprite但取不到纹理=" + dumpNoTex + ")" +
                            " lastErr=" + lastDumpErr);
                }
            }
            catch (Exception ex)
            {
                dumpDone = true;
                Cfg.Log("icon dump failed: " + ex.Message);
            }
        }

        // 优先用图鉴头像(生物), 没有就走 SpriteManager(物品/工具/植物)
        private Sprite IconSprite(TechType tt)
        {
            Sprite s = EncyclopediaSprite(tt);
            if (s != null) return s;
            try
            {
                if (!SpriteManager.hasInitialized) return null;
                Sprite it = SpriteManager.Get(tt);
                if (it != null && it != SpriteManager.defaultSprite) return it;
            }
            catch (Exception) { }
            return null;
        }

        // PDAScanner.GetEntryData(tt).encyclopedia -> PDAEncyclopedia.GetEntryData(key).popup
        // (两个 EntryData 都是 internal, 所以走反射)
        private Sprite EncyclopediaSprite(TechType tt)
        {
            try
            {
                if (pdascanGet == null)
                {
                    pdascanGet = typeof(PDAScanner).GetMethod("GetEntryData");
                    pdaencyGet = typeof(PDAEncyclopedia).GetMethod("GetEntryData");
                }
                if (pdascanGet == null || pdaencyGet == null) return null;
                object scan = pdascanGet.Invoke(null, new object[] { tt });
                if (scan == null) return null;
                FieldInfo f = scan.GetType().GetField("encyclopedia");
                if (f == null) return null;
                string key = (string)f.GetValue(scan);
                if (string.IsNullOrEmpty(key)) return null;
                object[] args = new object[] { key, null };
                object ok = pdaencyGet.Invoke(null, args);
                if (!(ok is bool) || !(bool)ok || args[1] == null) return null;
                FieldInfo pf = args[1].GetType().GetField("popup");
                if (pf == null) return null;
                return pf.GetValue(args[1]) as Sprite;
            }
            catch (Exception) { return null; }
        }

        // Sprite -> 可读 Texture2D: 图集纹理通常不可读, 用 RenderTexture 抠子矩形 + 读回 + 上下翻转。
        // 注意: 不要用 sprite.uv(非打包 sprite 可能是空数组, 会 IndexOutOfRange -> 全军覆没),
        // 直接用 textureRect(像素, 原点左下) 自己算 UV。
        private static Texture2D SpriteToReadable(Sprite sp)
        {
            if (sp == null) return null;
            Texture2D src = null;
            try { src = sp.texture; } catch (Exception) { }
            if (src == null) { lastDumpErr = "no texture"; return null; }
            Rect tr;
            try { tr = sp.textureRect; } catch (Exception e) { lastDumpErr = "textureRect: " + e.Message; return null; }
            int w = Mathf.RoundToInt(tr.width), h = Mathf.RoundToInt(tr.height);
            if (w <= 0 || h <= 0) { lastDumpErr = "size " + w + "x" + h; return null; }
            if (w > 512 || h > 512) { lastDumpErr = "too big " + w + "x" + h; return null; }
            // 可读纹理直接抠(最快路径)
            if (src.isReadable)
            {
                try
                {
                    Color[] px = src.GetPixels(Mathf.RoundToInt(tr.x), Mathf.RoundToInt(tr.y), w, h);
                    Texture2D t0 = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    t0.SetPixels(px);
                    t0.Apply();
                    return t0;
                }
                catch (Exception e) { lastDumpErr = "readable path: " + e.Message; }
            }
            RenderTexture rt = null;
            RenderTexture prev = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(w, h, 0);
                float tw = src.width, th = src.height;
                Graphics.Blit(src, rt, new Vector2(tr.width / tw, tr.height / th), new Vector2(tr.x / tw, tr.y / th));
                RenderTexture.active = rt;
                Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t.Apply();
                Color32[] px = t.GetPixels32();
                Color32[] flip = new Color32[px.Length];
                for (int y = 0; y < h; y++) Array.Copy(px, y * w, flip, (h - 1 - y) * w, w);
                t.SetPixels32(flip);
                t.Apply();
                return t;
            }
            catch (Exception e) { lastDumpErr = "blit path: " + e.Message; return null; }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        // 内置图标(icons/<TechType>.png)按需加载 + 缓存; 缺图标就返回 null, 调用方退回原来的点/三角
        private Texture2D GetIconTex(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Texture2D t;
            if (iconCache.TryGetValue(key, out t)) return t;
            if (iconCache.Count >= 128) return null;     // 别把纹理无限堆着
            try
            {
                string p = Path.Combine(Cfg.BaseDir, Path.Combine("icons", key + ".png"));
                t = File.Exists(p) ? LoadIconTexture(p) : null;
            }
            catch (Exception) { t = null; }
            iconCache[key] = t;
            return t;
        }

        // 图标缩到最长边 ≤64 像素再进显存。
        // 原始图最大 256x128, 而实际只在 16~24 像素的尺寸上绘制 —— 不缩的话 474 张全载入要几十 MB 显存,
        // 而且每遇到一个新物种就要上传一张大图(那一下就是一次掉帧)。
        // 缩放用"按 alpha 加权的盒式平均": 完全透明的像素不参与 RGB 平均, 否则边缘会发黑(之前那个黑边问题的同类坑)。
        private static Texture2D LoadIconTexture(string path)
        {
            Texture2D big = LoadTexture(path);
            if (big == null) return null;
            int w = big.width, h = big.height;
            int max = Mathf.Max(w, h);
            if (max <= 64) return big;
            try
            {
                float k = 64f / max;
                int nw = Mathf.Max(1, Mathf.RoundToInt(w * k));
                int nh = Mathf.Max(1, Mathf.RoundToInt(h * k));
                Color32[] src = big.GetPixels32();
                Color32[] dst = new Color32[nw * nh];
                for (int y = 0; y < nh; y++)
                {
                    int y0 = y * h / nh;
                    int y1 = Mathf.Max(y0 + 1, (y + 1) * h / nh);
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = x * w / nw;
                        int x1 = Mathf.Max(x0 + 1, (x + 1) * w / nw);
                        int r = 0, g = 0, b = 0, a = 0, n = 0;
                        for (int sy = y0; sy < y1; sy++)
                        {
                            int row = sy * w;
                            for (int sx = x0; sx < x1; sx++)
                            {
                                Color32 c = src[row + sx];
                                if (c.a == 0) continue;      // 透明像素不参与, 避免把边缘拉黑
                                r += c.r; g += c.g; b += c.b; a += c.a; n++;
                            }
                        }
                        Color32 o = new Color32(0, 0, 0, 0);
                        if (n != 0)
                        {
                            o.r = (byte)(r / n); o.g = (byte)(g / n); o.b = (byte)(b / n); o.a = (byte)(a / n);
                        }
                        dst[y * nw + x] = o;
                    }
                }
                Texture2D small = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                small.wrapMode = TextureWrapMode.Clamp;
                small.SetPixels32(dst);
                small.Apply();
                UnityEngine.Object.Destroy(big);
                return small;
            }
            catch (Exception) { return big; }
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
            float tDraw = Time.realtimeSinceStartup;

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

            // v2.7m: HUD 那行文字 10Hz 拼一次就够了 —— string.Format 每帧 1~2 个字符串对象,
            // 一秒上百个也是白给 GC 的。
            if (frame - hudLineFrame >= 6)
            {
                hudLineFrame = frame;
                string dir8 = Heading8();
                if (uiFontCjk)
                {
                    hudLineCache = biome == null
                        ? string.Format("X {0:F0}   Z {1:F0}   深度 {2:F0}m   {3}", pos.x, pos.z, depth, dir8)
                        : string.Format("X {0:F0}   Z {1:F0}   深度 {2:F0}m   {3}   {4}", pos.x, pos.z, depth, dir8, biome);
                }
                else
                {
                    hudLineCache = biome == null
                        ? string.Format("X {0:F0}   Z {1:F0}   Depth {2:F0}m   {3}", pos.x, pos.z, depth, dir8)
                        : string.Format("X {0:F0}   Z {1:F0}   Depth {2:F0}m   {3}   {4}", pos.x, pos.z, depth, dir8, biome);
                }
            }
            if (hudLineCache != null) LabelShadowed(new Rect(16, 12, 1200, 60), hudLineCache, hudStyle);

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

            float dgl = (Time.realtimeSinceStartup - tDraw) * 1000f;
            if (dgl > perfDrawMs) perfDrawMs = dgl;
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

            // 像素吸附: Unity 画贴图最终会吸附到整像素, 而窗口原点原来每帧跟着玩家的轻微晃动(在水里被水流推)
            // 连续微调 -> 标记就在相邻两个像素之间来回跳 = "一直抖动"。把原点吸附到"整屏幕像素"上,
            // 地图与所有标记用同一个原点, 于是水里晃动时整幅图纹丝不动地吸附在像素格上。
            float mpp = spanWorld / D;                              // 每个屏幕像素代表多少米
            float winZ0 = L.MinZ + v0 * (L.MaxZ - L.MinZ);         // 窗口南边界(世界 z)
            if (mpp > 0f)
            {
                winX0 = Mathf.Floor(winX0 / mpp) * mpp;
                winZ0 = Mathf.Floor(winZ0 / mpp) * mpp;
                // 原点变了, 贴图 UV 必须跟着重算, 否则底图和标记会错开
                u0 = Mathf.Clamp((winX0 - L.MinX) / (L.MaxX - L.MinX), 0f, 1f - su);
                v0 = Mathf.Clamp((winZ0 - L.MinZ) / (L.MaxZ - L.MinZ), 0f, 1f - sv);
            }

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
                    float sx = Mathf.Round(sq.x + (q.x - winX0) / spanWorld * D);
                    float sy = Mathf.Round(sq.y + (1f - (q.z - winZ0) / spanWorld) * D);
                    if (Vector2.Distance(new Vector2(sx, sy), sq.center) > D * 0.5f - 13f) continue;
                    bool isScan = pi.pingType == PingType.Signal;   // 扫描室扫描目标
                    if (isScan && !showScanSignals) continue;       // 设置窗口里可关掉
                    Texture2D ico = isScan ? GetIconTex(IconKeyForLabel(pi.GetLabel())) : null;
                    if (ico != null)
                    {
                        GUI.color = Color.white;
                        GUI.DrawTexture(new Rect(sx - 9f, sy - 9f, 18f, 18f), ico);
                    }
                    else if (isScan)
                    {
                        GUI.color = new Color(1f, 0.62f, 0.1f);
                        GUI.DrawTexture(new Rect(sx - 5f, sy - 5f, 10f, 10f), dotTex);
                        GUI.color = Color.white;
                    }
                    else
                    {
                        int ci = pi.colorIndex >= 0 ? pi.colorIndex : 0;
                        GUI.color = colors[ci % colors.Length];
                        GUI.DrawTexture(new Rect(sx - 5f, sy - 5f, 10f, 10f), dotTex);
                        GUI.color = Color.white;
                    }
                    if (isScan)
                    {
                        string lbl = pi.GetLabel();
                        if (!string.IsNullOrEmpty(lbl))
                            LabelShadowed(new Rect(sx + 7f, sy - 7f, 200f, 18f), lbl, miniSignalStyle);
                    }
                }
            }

            // 扫描室物品点(带物品图标; 没有图标就画小黄点)
            for (int i = 0; i < scanNodePos.Count; i++)
            {
                Vector3 q = scanNodePos[i];
                if (q.x < winX0 || q.x > winX0 + spanWorld || q.z < winZ0 || q.z > winZ0 + spanWorld) continue;
                float nx = Mathf.Round(sq.x + (q.x - winX0) / spanWorld * D);
                float ny = Mathf.Round(sq.y + (1f - (q.z - winZ0) / spanWorld) * D);
                if (Vector2.Distance(new Vector2(nx, ny), sq.center) > D * 0.5f - 12f) continue;
                Texture2D nico = GetIconTex(scanNodeKey[i]);
                if (nico != null)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(nx - 8f, ny - 8f, 16f, 16f), nico);
                }
                else
                {
                    GUI.color = new Color(1f, 0.85f, 0.2f);
                    GUI.DrawTexture(new Rect(nx - 4f, ny - 4f, 8f, 8f), dotTex);
                    GUI.color = Color.white;
                }
            }

            // 敌对生物: 小地图也画(有内置头像就画头像, 没有画黑描边红感叹号); 坐标每帧实时取
            for (int i = 0; i < trackedCreatures.Count; i++)
            {
                Creature c = trackedCreatures[i];
                if (c == null) continue;
                Vector3 q = c.transform.position;
                if (q.x < winX0 || q.x > winX0 + spanWorld || q.z < winZ0 || q.z > winZ0 + spanWorld) continue;
                float mx = Mathf.Round(sq.x + (q.x - winX0) / spanWorld * D);
                float my = Mathf.Round(sq.y + (1f - (q.z - winZ0) / spanWorld) * D);
                if (Vector2.Distance(new Vector2(mx, my), sq.center) > D * 0.5f - 12f) continue;
                Texture2D cico = GetIconTex(i < trackedKeys.Count ? trackedKeys[i] : null);
                if (cico != null)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(mx - 9f, my - 9f, 18f, 18f), cico);
                }
                else
                {
                    DrawAlertMark(mx, my);      // 没头像的生物: 黑描边红感叹号(原来是红点)
                }
            }

            // 玩家箭头: 位置按同一套换算(贴图层边缘被 clamp 时会离开圆心, 这样才对得上底图);
            // 方向不用 GUI 旋转矩阵(受 GUI 矩阵/缩放影响不可控), 自己在屏幕空间算三角形逐行填充
            float psx = Mathf.Round(sq.x + (p.x - winX0) / spanWorld * D);
            float psy = Mathf.Round(sq.y + (1f - (p.z - winZ0) / spanWorld) * D);
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

        // v2.7m: 结果缓存 0.2 秒。小地图每帧都要这份列表 —— 以前是每帧一次静态字段反射 +
        // 遍历字典 + new List; ping(信标)位置基本不动, 缓存完全看不出来。
        private List<PingInstance> GetPings()
        {
            if (frame - pingsCacheFrame < 12 && pingsCache.Count > 0) return pingsCache;
            pingsCacheFrame = frame;
            pingsCache.Clear();
            try
            {
                if (pingsDictField == null) return pingsCache;
                IDictionary dict = pingsDictField.GetValue(null) as IDictionary;
                if (dict == null) return pingsCache;
                foreach (object o in dict.Values)
                {
                    PingInstance pi = o as PingInstance;
                    if (pi != null) pingsCache.Add(pi);
                }
            }
            catch (Exception) { }
            return pingsCache;
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

            alertBlack = new GUIStyle(hudStyle);
            alertBlack.fontSize = Mathf.Max(16, Cfg.FontSize + 8);
            alertBlack.fontStyle = FontStyle.Bold;
            alertBlack.alignment = TextAnchor.MiddleCenter;
            alertBlack.normal.textColor = Color.black;
            alertRed = new GUIStyle(alertBlack);
            alertRed.normal.textColor = new Color(1f, 0.13f, 0.13f, 1f);
        }

        // 黑色描边的红色感叹号(给游戏里没有头像的生物当标记)
        private void DrawAlertMark(float x, float y)
        {
            if (alertBlack == null || alertRed == null) return;
            float w = 22f, h = 26f;
            Rect r = new Rect(x - w * 0.5f, y - h * 0.5f, w, h);
            for (int dx = -2; dx <= 2; dx += 2)
                for (int dy = -2; dy <= 2; dy += 2)
                    if (dx != 0 || dy != 0)
                        GUI.Label(new Rect(r.x + dx, r.y + dy, r.width, r.height), "!", alertBlack);
            GUI.Label(r, "!", alertRed);
        }
    }
}
