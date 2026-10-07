// SNMap 独立版 v1.1 (注入器/修改器方式, 无 BepInEx 依赖)
// 左上角: 坐标文字 + 圆形小地图(F7 循环 关->200->300->500->关, 显示范围=世界米数)
// F9: 全屏大地图 (自动解锁光标 + GameInput.ClearInput 屏蔽游戏输入, 滚轮缩放/拖动平移)
// 底图: KT411 2024 中文标注图, 中心=(0,0), 四边=±WorldRange, 上=北(-Z), 右=东(+X)
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
                if (GameObject.Find("SNMapRoot") != null)
                {
                    Cfg.Log("already installed, skip");
                    return;
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
                Log("config loaded: key=" + ToggleMapKey + " minimap=" + MinimapPixels + "px spans=" + MinimapSpans.Length);
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

    public class SnMapBehaviour : MonoBehaviour
    {
        private Texture2D mapTex;
        private Texture2D arrowTex;
        private Texture2D dotTex;
        private Texture2D panelTex;
        private Texture2D circleMask;
        private Font uiFont;
        private bool uiFontCjk;
        private GUIStyle hudStyle;
        private GUIStyle smallStyle;
        private GUIStyle btnStyle;
        private bool mapOpen;
        private int minimapIdx;           // 0=关, 1..N=MinimapSpans 档位
        private Rect mapSq;               // 全屏大地图中正方形底图区域
        private float zoom = 1f;          // 1..16
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
            LoadMapTexture();
            try
            {
                pingsDictField = typeof(PingManager).GetField("pings", BindingFlags.NonPublic | BindingFlags.Static);
                pingColorsField = typeof(PingManager).GetField("colorOptions", BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception) { }

            Cfg.Log("SNMap 1.1 behaviour awake ok | map=" + (mapTex != null) + " cjk=" + uiFontCjk +
                    " pings=" + (pingsDictField != null) + " bigKey=" + Cfg.ToggleMapKey +
                    " miniKey=" + Cfg.ToggleHudKey);
        }

        private void OnDestroy()
        {
            try
            {
                if (mapOpen) RestoreCursor();
            }
            catch (Exception) { }
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
                minimapIdx = (minimapIdx + 1) % (Cfg.MinimapSpans.Length + 1); // 关->1->2->3->关
            }
            if (mapOpen)
            {
                try { GameInput.ClearInput(2); } catch (Exception) { } // 屏蔽移动/视角/按键
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

            Transform tr = pl.transform;
            Vector3 pos = tr.position;
            float depth = -pos.y;
            try { depth = pl.GetDepth(); } catch (Exception) { }
            string biome = null;
            try { biome = MapBiome(pl.GetBiomeString()); } catch (Exception) { }

            if (!mapOpen)
            {
                string dir8 = Heading8(tr);
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

                if (minimapIdx > 0 && mapTex != null)
                    DrawMinimap(pl, tr);
            }

            if (mapOpen)
                DrawBigMap(pl);
        }

        // ------------------------------------------------------------- minimap

        private void DrawMinimap(Player pl, Transform tr)
        {
            float D = Mathf.Min(Cfg.MinimapPixels, Screen.height - 80f);
            Rect sq = new Rect(16f, 48f, D, D);
            float spanWorld = Cfg.MinimapSpans[Mathf.Clamp(minimapIdx - 1, 0, Cfg.MinimapSpans.Length - 1)];
            float spanUV = spanWorld / (2f * Cfg.WorldRange);

            Vector3 p = pl.transform.position;
            float uc = (p.x + Cfg.WorldRange) / (2f * Cfg.WorldRange);
            float vc = (p.z + Cfg.WorldRange) / (2f * Cfg.WorldRange);
            float u0 = Mathf.Clamp(uc - spanUV * 0.5f, 0f, 1f - spanUV);
            float v0 = Mathf.Clamp(vc - spanUV * 0.5f, 0f, 1f - spanUV);
            Rect uvRect = new Rect(u0, 1f - (v0 + spanUV), spanUV, spanUV);

            GUI.DrawTextureWithTexCoords(sq, mapTex, uvRect, false);
            if (circleMask != null) GUI.DrawTexture(sq, circleMask);

            // 信标点
            Color[] colors = GetPingColors();
            List<PingInstance> list = GetPings();
            if (list != null)
            {
                float half = spanWorld * 0.5f;
                for (int i = 0; i < list.Count; i++)
                {
                    PingInstance pi = list[i];
                    if (pi == null || !pi.visible) continue;
                    Vector3 q = pi.GetPosition();
                    float dx = q.x - p.x, dz = q.z - p.z;
                    if (Mathf.Abs(dx) > half || Mathf.Abs(dz) > half) continue;
                    float sx = sq.x + (0.5f + dx / spanWorld) * D;
                    float sy = sq.y + (0.5f + dz / spanWorld) * D;
                    if (Vector2.Distance(new Vector2(sx, sy), sq.center) > D * 0.5f - 8f) continue;
                    int ci = pi.colorIndex >= 0 ? pi.colorIndex : 0;
                    GUI.color = colors[ci % colors.Length];
                    GUI.DrawTexture(new Rect(sx - 5f, sy - 5f, 10f, 10f), dotTex);
                    GUI.color = Color.white;
                }
            }

            // 玩家箭头(朝向)
            float ang = HeadingAngle(tr);
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, sq.center);
            GUI.DrawTexture(new Rect(sq.center.x - 13f, sq.center.y - 13f, 26f, 26f), arrowTex);
            GUI.matrix = old;

            LabelShadowed(new Rect(sq.center.x - 20f, sq.y + 4f, 40f, 20f), "N", smallStyle);
            LabelShadowed(new Rect(sq.x, sq.yMax + 2f, 260f, 22f),
                string.Format(uiFontCjk ? "范围 {0:F0}m  [{1}切换]" : "range {0:F0}m  [{1}]", spanWorld, Cfg.ToggleHudKey),
                smallStyle);
        }

        // ------------------------------------------------------------- big map

        private void DrawBigMap(Player pl)
        {
            Rect full = new Rect(0f, 0f, Screen.width, Screen.height);
            GUI.DrawTexture(full, panelTex);

            if (mapTex == null)
            {
                GUI.Label(new Rect(20f, 20f, 600f, 40f), "map.png not found", hudStyle);
                DrawCloseButton();
                return;
            }

            float side = Mathf.Min(Screen.width, Screen.height);
            mapSq = new Rect((Screen.width - side) * 0.5f, (Screen.height - side) * 0.5f, side, side);

            HandleBigMapInput(mapSq);
            float span = 1f / zoom;
            float u0 = Mathf.Clamp(centerU - span * 0.5f, 0f, 1f - span);
            float v0 = Mathf.Clamp(centerV - span * 0.5f, 0f, 1f - span);
            Rect uvRect = new Rect(u0, 1f - (v0 + span), span, span);
            GUI.DrawTextureWithTexCoords(mapSq, mapTex, uvRect, true);

            DrawPings(mapSq, u0, v0, span, true);
            DrawPlayer(mapSq, u0, v0, span);

            float pxPerM = side / (span * 2f * Cfg.WorldRange);
            if (pxPerM > 0f)
            {
                LabelShadowed(new Rect(mapSq.x + 10f, mapSq.y + 8f, 320f, 26f),
                    string.Format(uiFontCjk ? "比例 1px = {0:F2}m (x{1:F1})" : "scale 1px = {0:F2}m (x{1:F1})", 1f / pxPerM, zoom),
                    smallStyle);
            }

            Player p2 = pl;
            Vector3 p = p2.transform.position;
            float depth = -p.y;
            try { depth = p2.GetDepth(); } catch (Exception) { }
            LabelShadowed(new Rect(14f, Screen.height - 34f, 700f, 26f),
                string.Format(uiFontCjk ? "X {0:F0}   Z {1:F0}   深度 {2:F0}m" : "X {0:F0}   Z {1:F0}   Depth {2:F0}m", p.x, p.z, depth),
                smallStyle);
            LabelShadowed(new Rect(14f, 10f, 700f, 26f),
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
            float span = 1f / zoom;
            float u0 = Mathf.Clamp(centerU - span * 0.5f, 0f, 1f - span);
            float v0 = Mathf.Clamp(centerV - span * 0.5f, 0f, 1f - span);

            if (e.type == EventType.ScrollWheel && sq.Contains(e.mousePosition))
            {
                float mu = u0 + (e.mousePosition.x - sq.x) / sq.width * span;
                float mv = v0 + (e.mousePosition.y - sq.y) / sq.height * span;
                float oldZoom = zoom;
                zoom = Mathf.Clamp(zoom * (e.delta.y > 0f ? 1.25f : 0.8f), 1f, 16f);
                if (zoom != oldZoom)
                {
                    float newSpan = 1f / zoom;
                    centerU = mu + (centerU - mu) * (newSpan / span);
                    centerV = mv + (centerV - mv) * (newSpan / span);
                    ClampCenter(newSpan);
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 &&
                     zoom > 1f && sq.Contains(e.mousePosition))
            {
                centerU -= e.delta.x / sq.width * span;
                centerV -= e.delta.y / sq.height * span;
                ClampCenter(span);
                e.Use();
            }
        }

        private void ClampCenter(float span)
        {
            centerU = Mathf.Clamp(centerU, span * 0.5f, 1f - span * 0.5f);
            centerV = Mathf.Clamp(centerV, span * 0.5f, 1f - span * 0.5f);
        }

        // ------------------------------------------------------------- shared draw

        private void DrawPlayer(Rect sq, float u0, float v0, float span)
        {
            Player pl = Player.main;
            if (pl == null || arrowTex == null) return;
            Vector3 p = pl.transform.position;
            float mx, my;
            if (!WorldToWindow(p.x, p.z, sq, u0, v0, span, out mx, out my)) return;

            float ang = HeadingAngle(pl.transform);
            float a = 34f;
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, new Vector2(mx, my));
            GUI.DrawTexture(new Rect(mx - a * 0.5f, my - a * 0.5f, a, a), arrowTex);
            GUI.matrix = old;
        }

        private void DrawPings(Rect sq, float u0, float v0, float span, bool withLabels)
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
                    if (!WorldToWindow(p.x, p.z, sq, u0, v0, span, out mx, out my)) continue;

                    int ci = pi.colorIndex >= 0 ? pi.colorIndex : 0;
                    GUI.color = colors[ci % colors.Length];
                    GUI.DrawTexture(new Rect(mx - 7f, my - 7f, 14f, 14f), dotTex);
                    GUI.color = Color.white;

                    if (withLabels)
                    {
                        string lbl = pi.GetLabel();
                        if (!string.IsNullOrEmpty(lbl))
                            LabelShadowed(new Rect(mx + 9f, my - 10f, 240f, 20f), lbl, smallStyle);
                    }
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

        private bool WorldToWindow(float wx, float wz, Rect sq, float u0, float v0, float span, out float mx, out float my)
        {
            float R = Cfg.WorldRange;
            float u = (wx + R) / (2f * R);
            float v = (wz + R) / (2f * R);
            mx = sq.x + (u - u0) / span * sq.width;
            my = sq.y + (v - v0) / span * sq.height;
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

        private static float HeadingAngle(Transform tr)
        {
            Vector3 f = tr.forward;
            float a = 0f;
            if (f.sqrMagnitude > 0.000001f) a = Mathf.Atan2(f.x, -f.z) * Mathf.Rad2Deg + 180f;
            if (a < 0f) a += 360f;
            if (a >= 360f) a -= 360f;
            return a;
        }

        private static string Heading8(Transform tr)
        {
            return Headings[Mathf.RoundToInt(HeadingAngle(tr) / 45f) % 8];
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

        private void LoadMapTexture()
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(typeof(SnMapBehaviour).Assembly.Location); } catch (Exception) { }

            string[] cands = new string[]
            {
                dir != null ? Path.Combine(dir, "map.png") : null,
                dir != null ? Path.Combine(dir, "map.jpg") : null
            };

            for (int i = 0; i < cands.Length; i++)
            {
                string path = cands[i];
                if (path == null || !File.Exists(path)) continue;
                try
                {
                    byte[] raw = File.ReadAllBytes(path);
                    Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    if (tex.LoadImage(raw))
                    {
                        mapTex = tex;
                        Cfg.Log("map texture loaded: " + path + " (" + tex.width + "x" + tex.height + ")");
                        return;
                    }
                    Cfg.Log("LoadImage failed: " + path);
                }
                catch (Exception ex)
                {
                    Cfg.Log("read map failed: " + path + " (" + ex.Message + ")");
                }
            }
            Cfg.Log("map image not found next to SNMapManaged.dll");
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
        }
    }
}
