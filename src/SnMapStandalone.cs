// SNMap 独立版 v1.2 (注入器/修改器方式, 无 BepInEx 依赖)
// 左上角: 坐标文字 + 圆形小地图(F7 循环 关->200->300->500->关), 与大地图当前图层同步
// F9: 全屏大地图, 顶部 [－][＋] 切换 maps/ 文件夹里的图层, 解锁光标+屏蔽游戏输入, 滚轮缩放/拖动平移
// 朝向: 使用渲染相机 MainCamera.camera (Player.main.transform 不随视角旋转!)
// 底图: maps/ 文件夹下所有 png/jpg, 命名序号决定顺序; maps.ini 可选每层标定: 文件名=minX,maxX,minZ,maxZ
//       默认标定: 中心=(0,0), 四边=±WorldRange; 未标定的非方形图层仅浏览(不画玩家/信标)
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
        public static int MinimapPixels = 220;
        public static float[] MinimapSpans = new float[] { 200f, 300f, 500f };
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
                sb.AppendLine("# 小地图各档显示范围(米), 逗号分隔, F7 循环: 关->第1档->第2档->...");
                sb.AppendLine("MinimapSpans=200,300,500");
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
        public bool Calibrated = true;   // false = 仅浏览(不画玩家/信标, 小地图不跟随)
        public bool HasIniBounds;
    }

    public class SnMapBehaviour : MonoBehaviour
    {
        private Texture2D arrowTex;
        private Texture2D dotTex;
        private Texture2D panelTex;
        private Texture2D circleMask;
        private Font uiFont;
        private bool uiFontCjk;
        private GUIStyle hudStyle;
        private GUIStyle smallStyle;
        private GUIStyle btnStyle;
        private GUIStyle bigBtnStyle;
        private bool mapOpen;
        private int minimapIdx = 1;       // 0=关, 1..N=MinimapSpans 档位, 默认开第1档
        private List<MapLayer> layers = new List<MapLayer>();
        private int layerIdx;
        private int lastCalIdx;
        private float zoom = 1f;
        private float centerU = 0.5f;
        private float centerV = 0.5f;
        private CursorLockMode prevLock = CursorLockMode.None;
        private bool prevVisible = true;
        private FieldInfo pingsDictField;
        private FieldInfo pingColorsField;
        private bool pingWarned;

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

        private void Awake()
        {
            LoadFont();
            BuildArrowTexture();
            BuildDotTexture();
            BuildPanelTexture();
            BuildCircleMask(Cfg.MinimapPixels);
            LoadLayers();
            LoadPingsApi();

            if (layers.Count > 0)
            {
                GetTex(layers[0]);
                Cfg.Log("SNMap 1.2 awake | layers=" + layers.Count +
                        " cjk=" + uiFontCjk + " pings=" + (pingsDictField != null) +
                        " bigKey=" + Cfg.ToggleMapKey + " miniKey=" + Cfg.ToggleHudKey);
            }
            else
            {
                Cfg.Log("SNMap 1.2 awake | NO map layers found!");
            }
        }

        private void OnDestroy()
        {
            try { if (mapOpen) RestoreCursor(); } catch (Exception) { }
        }

        private void Update()
        {
            KeyCode kc;
            if (Enum.TryParse(Cfg.ToggleMapKey, true, out kc) && Input.GetKeyDown(kc))
            {
                mapOpen = !mapOpen;
                if (mapOpen) FreeCursor(); else RestoreCursor();
            }
            if (Enum.TryParse(Cfg.ToggleHudKey, true, out kc) && Input.GetKeyDown(kc))
            {
                minimapIdx = (minimapIdx + 1) % (Cfg.MinimapSpans.Length + 1);
            }
            if (mapOpen)
            {
                try { GameInput.ClearInput(2); } catch (Exception) { }
                // 游戏每帧都会重新锁鼠标, 必须每帧抢回来
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch (Exception) { }
            }
        }

        private void FreeCursor()
        {
            try
            {
                prevLock = Cursor.lockState;
                prevVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch (Exception) { }
        }

        private void RestoreCursor()
        {
            try
            {
                Cursor.lockState = prevLock;
                Cursor.visible = prevVisible;
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- OnGUI

        private void OnGUI()
        {
            Player pl = Player.main;
            if (pl == null || pl.transform == null) return;
            EnsureStyles();

            if (mapOpen)
            {
                // OnGUI 每帧多次, 再抢一次鼠标, 确保渲染前状态正确
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch (Exception) { }
            }

            Vector3 pos = pl.transform.position;
            float depth = -pos.y;
            try { depth = pl.GetDepth(); } catch (Exception) { }
            string biome = null;
            try { biome = MapBiome(pl.GetBiomeString()); } catch (Exception) { }

            if (!mapOpen)
            {
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

            if (mapOpen)
                DrawBigMap(pl);
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
                Dictionary<string, float[]> ini = ParseMapsIni(Path.Combine(mapsDir, "maps.ini"));
                for (int i = 0; i < files.Count; i++)
                {
                    MapLayer L = new MapLayer();
                    L.Path = files[i];
                    L.Name = Path.GetFileNameWithoutExtension(files[i]);
                    int us = L.Name.IndexOf('_');
                    if (us == 2 || us == 3)
                    {
                        string head = L.Name.Substring(0, us);
                        int num;
                        if (int.TryParse(head, out num)) L.Name = L.Name.Substring(us + 1);
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
                // 兼容旧的 map.png
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

        private MapLayer CurrentLayer()
        {
            if (layers.Count == 0) return null;
            return layers[Mathf.Clamp(layerIdx, 0, layers.Count - 1)];
        }

        private MapLayer MinimapLayer()
        {
            MapLayer cur = CurrentLayer();
            if (cur != null && cur.Calibrated && cur.Tex != null) return cur;
            if (lastCalIdx >= 0 && lastCalIdx < layers.Count) return layers[lastCalIdx];
            return cur;
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
                    L.Calibrated = ar > 0.9f && ar < 1.1f;   // 方形视为全图投影
                }
                // 释放其他图层纹理, 控制显存(大图 8192^2 有 256MB)
                for (int i = 0; i < layers.Count; i++)
                {
                    if (layers[i] != L && layers[i].Tex != null)
                    {
                        UnityEngine.Object.Destroy(layers[i].Tex);
                        layers[i].Tex = null;
                    }
                }
                if (L.Calibrated) lastCalIdx = layers.IndexOf(L);
                return tex;
            }
            catch (Exception ex)
            {
                Cfg.Log("load layer failed: " + L.Name + " (" + ex.Message + ")");
                return null;
            }
        }

        private void SwitchLayer(int delta)
        {
            if (layers.Count == 0) return;
            layerIdx = (layerIdx + delta + layers.Count) % layers.Count;
            GetTex(layers[layerIdx]);
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
            Rect uvRect = new Rect(u0, 1f - (v0 + sv), su, sv);

            GUI.DrawTextureWithTexCoords(sq, L.Tex, uvRect, false);
            if (circleMask != null) GUI.DrawTexture(sq, circleMask);

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
                    if (Vector2.Distance(new Vector2(sx, sy), sq.center) > D * 0.5f - 8f) continue;
                    int ci = pi.colorIndex >= 0 ? pi.colorIndex : 0;
                    GUI.color = colors[ci % colors.Length];
                    GUI.DrawTexture(new Rect(sx - 5f, sy - 5f, 10f, 10f), dotTex);
                    GUI.color = Color.white;
                }
            }

            float ang = HeadingAngle();
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, sq.center);
            GUI.DrawTexture(new Rect(sq.center.x - 13f, sq.center.y - 13f, 26f, 26f), arrowTex);
            GUI.matrix = old;

            LabelShadowed(new Rect(sq.center.x - 20f, sq.y + 4f, 40f, 20f), "N", smallStyle);
            LabelShadowed(new Rect(sq.x, sq.yMax + 2f, 320f, 22f),
                string.Format(uiFontCjk ? "{0}  范围 {1:F0}m  [{2}切换]" : "{0}  range {1:F0}m  [{2}]",
                    L.Name, spanWorld, Cfg.ToggleHudKey),
                smallStyle);
        }

        // ------------------------------------------------------------- big map

        private void DrawBigMap(Player pl)
        {
            Rect full = new Rect(0f, 0f, Screen.width, Screen.height);
            GUI.DrawTexture(full, panelTex);

            MapLayer L = CurrentLayer();
            if (L == null)
            {
                GUI.Label(new Rect(20f, 60f, 600f, 40f), "maps 文件夹里没有地图", hudStyle);
                DrawCloseButton();
                return;
            }
            Texture2D tex = GetTex(L);
            if (tex == null)
            {
                GUI.Label(new Rect(20f, 60f, 700f, 40f), "图层加载失败: " + L.Name, hudStyle);
                DrawCloseButton();
                return;
            }

            // 顶部图层切换条: [－] 名称 (i/N) [＋]
            float barW = 520f;
            float barX = (Screen.width - barW) * 0.5f;
            bool repaint = Event.current != null && Event.current.type == EventType.Repaint;
            Rect prevR = new Rect(barX, 10f, 56f, 32f);
            Rect nextR = new Rect(barX + barW - 56f, 10f, 56f, 32f);
            if (repaint && GUI.Button(prevR, "－", bigBtnStyle)) SwitchLayer(-1);
            if (repaint && GUI.Button(nextR, "＋", bigBtnStyle)) SwitchLayer(1);
            LabelShadowed(new Rect(barX + 66f, 14f, barW - 132f, 28f),
                string.Format("{0}   ({1}/{2}){3}", L.Name, layerIdx + 1, layers.Count,
                    L.Calibrated ? "" : (uiFontCjk ? "  [未标定·仅浏览]" : "  [browse only]")),
                hudStyle);

            // 地图绘制区
            float availW = Screen.width - 16f;
            float availH = Screen.height - 60f;
            float side = Mathf.Min(availW, availH);
            float ar = L.Height > 0 ? (float)L.Width / L.Height : 1f;
            float drawW = side, drawH = side;
            if (ar >= 1f) drawH = side / ar; else drawW = side * ar;
            Rect sq = new Rect((Screen.width - drawW) * 0.5f, 48f + (availH - drawH) * 0.5f, drawW, drawH);

            HandleBigMapInput(sq);
            float spanU = 1f / zoom, spanV = 1f / zoom;
            centerU = Mathf.Clamp(centerU, spanU * 0.5f, 1f - spanU * 0.5f);
            centerV = Mathf.Clamp(centerV, spanV * 0.5f, 1f - spanV * 0.5f);
            Rect uvRect = new Rect(centerU - spanU * 0.5f, 1f - (centerV + spanV * 0.5f), spanU, spanV);
            GUI.DrawTextureWithTexCoords(sq, tex, uvRect, true);

            if (L.Calibrated)
            {
                float u0 = centerU - spanU * 0.5f, v0 = centerV - spanV * 0.5f;
                DrawPings(L, sq, u0, v0, spanU, spanV);
                DrawPlayer(L, pl, sq, u0, v0, spanU, spanV);
                float mPerPx = (L.MaxX - L.MinX) / (sq.width * spanU);
                if (mPerPx > 0f)
                {
                    LabelShadowed(new Rect(sq.x + 10f, sq.y + 8f, 340f, 26f),
                        string.Format(uiFontCjk ? "比例 1px = {0:F2}m (x{1:F1})" : "scale 1px = {0:F2}m (x{1:F1})", mPerPx, zoom),
                        smallStyle);
                }
            }
            else
            {
                LabelShadowed(new Rect(sq.x + 10f, sq.y + 8f, 560f, 26f),
                    uiFontCjk ? "该图层未标定世界坐标, 仅浏览 (玩家/信标不显示)"
                              : "layer not calibrated - view only",
                    smallStyle);
            }

            Player p2 = pl;
            Vector3 p = p2.transform.position;
            float depth = -p.y;
            try { depth = p2.GetDepth(); } catch (Exception) { }
            LabelShadowed(new Rect(14f, Screen.height - 34f, 700f, 26f),
                string.Format(uiFontCjk ? "X {0:F0}   Z {1:F0}   深度 {2:F0}m" : "X {0:F0}   Z {1:F0}   Depth {2:F0}m", p.x, p.z, depth),
                smallStyle);
            LabelShadowed(new Rect(14f, 52f, 700f, 26f),
                uiFontCjk ? "[" + Cfg.ToggleMapKey + " 关闭]  滚轮缩放  左键拖动平移"
                          : "[" + Cfg.ToggleMapKey + " close]  wheel=zoom  drag=pan",
                smallStyle);

            DrawCloseButton();
        }

        private void DrawCloseButton()
        {
            Rect closeR = new Rect(Screen.width - 46f, 10f, 34f, 28f);
            if (GUI.Button(closeR, "X", btnStyle))
            {
                mapOpen = false;
                RestoreCursor();
            }
        }

        private void HandleBigMapInput(Rect sq)
        {
            Event e = Event.current;
            if (e == null) return;
            float spanU = 1f / zoom, spanV = 1f / zoom;
            float u0 = centerU - spanU * 0.5f, v0 = centerV - spanV * 0.5f;

            if (e.type == EventType.ScrollWheel && sq.Contains(e.mousePosition))
            {
                float mu = u0 + (e.mousePosition.x - sq.x) / sq.width * spanU;
                float mv = v0 + (e.mousePosition.y - sq.y) / sq.height * spanV;
                float oldZoom = zoom;
                zoom = Mathf.Clamp(zoom * (e.delta.y > 0f ? 1.25f : 0.8f), 1f, 16f);
                if (zoom != oldZoom)
                {
                    float nu = 1f / zoom, nv = 1f / zoom;
                    centerU = mu + (centerU - mu) * (nu / spanU);
                    centerV = mv + (centerV - mv) * (nv / spanV);
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 &&
                     zoom > 1f && sq.Contains(e.mousePosition))
            {
                centerU -= e.delta.x / sq.width * spanU;
                centerV -= e.delta.y / sq.height * spanV;
                e.Use();
            }
        }

        // ------------------------------------------------------------- shared draw

        private void DrawPlayer(MapLayer L, Player pl, Rect sq, float u0, float v0, float spanU, float spanV)
        {
            if (pl == null || arrowTex == null) return;
            Vector3 p = pl.transform.position;
            float mx, my;
            if (!WorldToWindow(L, p.x, p.z, sq, u0, v0, spanU, spanV, out mx, out my)) return;

            float ang = HeadingAngle();
            float a = 34f;
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, new Vector2(mx, my));
            GUI.DrawTexture(new Rect(mx - a * 0.5f, my - a * 0.5f, a, a), arrowTex);
            GUI.matrix = old;
        }

        private void DrawPings(MapLayer L, Rect sq, float u0, float v0, float spanU, float spanV)
        {
            if (dotTex == null) return;
            try
            {
                List<PingInstance> list = GetPings();
                if (list == null) return;
                Color[] colors = GetPingColors();
                for (int i = 0; i < list.Count; i++)
                {
                    PingInstance pi = list[i];
                    if (pi == null || !pi.visible) continue;
                    Vector3 p = pi.GetPosition();
                    float mx, my;
                    if (!WorldToWindow(L, p.x, p.z, sq, u0, v0, spanU, spanV, out mx, out my)) continue;

                    int ci = pi.colorIndex >= 0 ? pi.colorIndex : 0;
                    GUI.color = colors[ci % colors.Length];
                    GUI.DrawTexture(new Rect(mx - 7f, my - 7f, 14f, 14f), dotTex);
                    GUI.color = Color.white;

                    string lbl = pi.GetLabel();
                    if (!string.IsNullOrEmpty(lbl))
                        LabelShadowed(new Rect(mx + 9f, my - 10f, 240f, 20f), lbl, smallStyle);
                }
            }
            catch (Exception ex)
            {
                if (!pingWarned)
                {
                    pingWarned = true;
                    Cfg.Log("draw pings failed: " + ex.Message);
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

        private bool WorldToWindow(MapLayer L, float wx, float wz, Rect sq, float u0, float v0, float spanU, float spanV, out float mx, out float my)
        {
            float u = (wx - L.MinX) / (L.MaxX - L.MinX);
            float v = (wz - L.MinZ) / (L.MaxZ - L.MinZ);
            mx = sq.x + (u - u0) / spanU * sq.width;
            my = sq.y + (v - v0) / spanV * sq.height;
            return mx >= sq.x - 24f && my >= sq.y - 24f && mx <= sq.xMax + 24f && my <= sq.yMax + 24f;
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
            float a = Mathf.Atan2(f.x, -f.z) * Mathf.Rad2Deg;
            if (a < 0f) a += 360f;
            if (a >= 360f) a -= 360f;
            return a;
        }

        private static string Heading8()
        {
            return Headings[Mathf.RoundToInt(HeadingAngle() / 45f) % 8];
        }

        private static string MapBiome(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string cn;
            if (BiomeCn.TryGetValue(id, out cn)) return cn;
            if (id.EndsWith("Cave") && BiomeCn.TryGetValue(id.Substring(0, id.Length - 4), out cn)) return cn + "洞穴";
            return id;
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

        private void BuildCircleMask(int D)
        {
            try
            {
                Color32[] px = new Color32[D * D];
                float c = (D - 1) * 0.5f;
                float R = D * 0.5f;
                Color32 hole = new Color32(0, 0, 0, 0);
                Color32 ring = new Color32(0, 0, 0, 235);
                Color32 plate = new Color32(10, 12, 20, 255);
                for (int y = 0; y < D; y++)
                {
                    for (int x = 0; x < D; x++)
                    {
                        float dx = x - c, dy = y - c;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        px[y * D + x] = d <= R - 3f ? hole : (d <= R + 1f ? ring : plate);
                    }
                }
                circleMask = new Texture2D(D, D, TextureFormat.RGBA32, false);
                circleMask.SetPixels32(px);
                circleMask.Apply();
            }
            catch (Exception ex)
            {
                Cfg.Log("circle mask failed: " + ex.Message);
            }
        }

        private void BuildArrowTexture()
        {
            int w = 32, h = 32;
            bool[] solid = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int half;
                if (y <= 19) half = 1 + (y * 14) / 19;
                else if (y <= 30) half = 3;
                else half = 2;
                for (int x = 0; x < w; x++)
                {
                    int dx = x - 16;
                    if (dx >= -half && dx <= half) solid[y * w + x] = true;
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
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1 && !near; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
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

        private void BuildPanelTexture()
        {
            Color32[] px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(8, 9, 14, 235);
            panelTex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            panelTex.SetPixels32(px);
            panelTex.Apply();
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

            btnStyle = new GUIStyle(GUI.skin.button);
            if (uiFont != null) btnStyle.font = uiFont;
            btnStyle.fontSize = 14;

            bigBtnStyle = new GUIStyle(btnStyle);
            bigBtnStyle.fontSize = Mathf.Max(16, Cfg.FontSize);
            bigBtnStyle.alignment = TextAnchor.MiddleCenter;
        }
    }
}
