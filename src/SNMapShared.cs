// SNMap 共享定义: 同时编译进 游戏内模块(SNMapManaged) 与 大地图窗口(SNMapWindow)
// 只依赖 BCL, 两边保证协议一致
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SNMap
{
    /// 共享协议 v2: 模块每秒 20 次写 SNMapState.bin, 窗口轮询读取;
    /// 双向设置(小地图大小/生物开关/F9标志/图层)通过 SNMapSettings.ini 交换
    public static class Proto
    {
        public const string StateFileName = "SNMapState.bin";
        public const string SettingsFileName = "SNMapSettings.ini";
        public const string KeyShowWindow = "ShowWindow";
        public const string KeyMinimapPixels = "MinimapPixels";
        public const string KeyShowCreatures = "ShowCreatures";
        public const string KeyWindowLayer = "WindowLayer";
        public const string KeyShowScanSignals = "ShowScanSignals";
        public const string KeyCreatureShow = "CreatureShow";   // 逗号分隔的 TechType 名(要显示的物种); "*"=全部

        public const int Version = 3;           // v3: 增加物种清单(OffSpecies*)与两个新设置键
        public const int Magic = 0x534E4D50;
        public const int StateSize = 8192;      // 状态文件固定长度(窗口复用读缓冲要用)

        public const int OffMagic = 0;          // int
        public const int OffVersion = 4;        // int
        public const int OffTick = 8;           // long
        public const int OffPlayerValid = 16;   // int
        public const int OffX = 20;             // float
        public const int OffY = 24;             // float 深度
        public const int OffZ = 28;             // float
        public const int OffHeading = 32;       // float 角度(顺时针, 0=北)
        public const int OffShowWindow = 36;    // byte  F9 标志(模块写)
        public const int OffModVerLen = 40;     // int
        public const int OffModVer = 44;        // 32B utf8 模块版本
        public const int OffBeaconCount = 80;   // int
        public const int OffBiomeLen = 84;      // int
        public const int OffBiome = 88;         // 64B utf8
        public const int OffBeacons = 160;      // 32 * 128B: x(f) z(4f) color(8i) visible(12i) labelLen(16i) label(20+63B)
        public const int OffWindowLayerEcho = 4256; // int  模块回显(读自设置)
        public const int OffMinimapPixelsEcho = 4260; // int 模块回显
        public const int OffCreatureCount = 4268; // int 模块->窗口
        public const int OffCreatures = 4272;   // 24 * 80B: x(f) z(4f) labelLen(8i) label(12+66B)
        public const int OffSpeciesCount = 6200; // int  模块见过的敌对物种数(供设置窗口列出全部敌对生物)
        public const int OffSpecies = 6204;     // 40 * 32B: nameLen(i) + utf8 TechType 名(<=28B)

        public const int MaxBeacons = 32;
        public const int MaxCreatures = 24;
        public const int MaxSpecies = 40;
        public const int SpeciesStride = 32;

        public static Dictionary<string, float[]> ParseMapsIni(string path)
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

        public static Dictionary<string, string> ParseIniStrings(string path)
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

        private static readonly Dictionary<string, string> BiomeCn = new Dictionary<string, string>
        {
            { "safeShallows", "浅滩区" },
            { "kelpForest", "海藻区" },
            { "grassyPlateaus", "红藻区" },
            { "mushroomForest", "蘑菇林" },
            { "kooshZone", "库什区" },
            { "mountains", "山脉区" },
            { "crashZone", "坠毁区" },
            { "dunes", "沙丘区" },
            { "grandReef", "水雷区" },
            { "underIslands", "浮岛区" },
            { "sparseReef", "暗礁区" },
            { "seaTreadersPath", "踏浪者小径" },
            { "bloodKelp", "血藻区" },
            { "lostRiver", "失落之河" },
            { "inactiveLavaZone", "非活跃熔岩区" },
            { "lavaLakes", "熔岩湖" },
            { "activeLavaZone", "活跃熔岩区" },
            { "void", "虚空" }
        };

        public static string BiomeCnOrNull(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string cn;
            if (BiomeCn.TryGetValue(id, out cn)) return cn;
            if (id.EndsWith("Cave") && BiomeCn.TryGetValue(id.Substring(0, id.Length - 4), out cn)) return cn + "洞穴";
            return id;
        }
    }
}
