# Subnautica SNMap 深海迷航小地图

A trainer-style minimap & standalone map window for **Subnautica (Steam, x64)**.
深海迷航（Steam x64）的小地图 + 独立大地图窗口辅助工具。修改器式注入：**不随游戏启动、不依赖任何模组框架（BepInEx 等）**。

> 仅用于单人游戏与学习交流。请勿用于任何联机对抗场景。

当前版本：**v2.5**

## 坐标系约定（改代码必读）

- 世界 `+X = 东 = 屏幕右`，`+Z = 北 = 屏幕上`；地图图片上方即北（`MaxZ`），图片行号 `r = (1-v)*H`，`v = (z-MinZ)/(MaxZ-MinZ)`
- 世界→屏幕：`sx = 窗口西边界 + (wx-winX0)/span*D`，`sy = 窗口南边界 + (1 - (wz-winZ0)/span)*D`
- 玩家朝向 = 罗盘方位角 `θ = atan2(camera.forward.x, camera.forward.z)`（0=北、90=东、顺时针）；Unity GUI 与 GDI+ 的正角度都是顺时针，两边都直接用 θ
- 底图与叠加层（箭头/信标/生物）必须用**同一套换算**；小地图贴到图层边缘被 clamp 时，箭头要按窗口原点算、不能钉死在圆心

## 功能

- 左上角实时坐标：`X / Z / 深度 / 朝向(8向) / 生物群系(中文)`（朝向取自渲染相机）
- **F7** 圆形小地图：`200m → 300m → 500m → 1000m → 关 → 200m` 循环，以玩家为中心，红箭头=玩家朝向，彩点=信标/生命舱，橙点+名字=扫描室正在扫描的信号，顶部 N 指北；“关”档整块小地图隐藏（含信号点），只留一行 `小地图: 关 [F7 开启]` 提示
- **F9** 独立大地图窗口（`SNMapWindow.exe`，默认最大化）：按一下把窗口**抬到游戏画面最前**且不抢游戏焦点（所以再按 F9 依然关得掉）；滚轮缩放（以鼠标为中心，最高 16x）、左键拖动平移、双击回到跟随；**顶部 `－`/`＋` 按钮切换地图图层**，小地图跟随当前图层；窗口内可调 跟随 / 置顶
- **顶部【设置】窗口**（v2.5）：左侧三个总开关 —— 小地图大小 / 显示敌对生物 / 显示扫描室扫描目标；右侧 = 全部敌对生物逐个勾选（勾上显示、去掉隐藏），列表 = 内置已知物种 ∪ 游戏里实际遇到过的物种（模块把见过的 TechType 名单写进状态文件）。改动写进 `SNMapSettings.ini`，模块 ~0.1 秒轮询一次，**立即生效、不用重新注入**
- 多图层：`maps/` 文件夹里的每张 png/jpg 都是一个图层（主地图、失落之河、蛇菇洞、熔岩堡、海皇监狱、利维坦分布等），大地图小地图同步切换
- 游戏更新一般无需重做：坐标直接读游戏对象（`Player.main` 等），**不使用内存偏移**

## 使用

1. 启动游戏（Steam）
2. 运行 `SNInjector.exe`：**检测到游戏进程后自动注入**（每 1.5 秒探测，游戏重启换 PID 后会再次自动注入；已加载则远程热重载）
   - 检测不到可点 **[选择窗口]** 手动选窗口，再点 **[注入]**
3. 运行 `SNMapWindow.exe`（大地图窗口进程，**F9 靠它显示，不启动它按 F9 无反应**）
4. 绿色提示即成功。**发布包解压到任意目录即可用**，不需要放进游戏目录

## 图层与标定（maps/）

- `maps/` 下按文件名排序加载图层，文件名建议 `01_名字.png` 序号前缀决定顺序
- 默认标定：图像中心 = 世界 `(0,0)`，四边 = `±WorldRange`，上=北（`-Z`），右=东（`+X`）
- 区域裁剪图（如失落之河等洞穴图）可在 `maps/maps.ini` 里按层标定：
  `文件名.jpg=minX,maxX,minZ,maxZ`
- 未标定的非方形图层自动进入“仅浏览”模式（可全屏查看，但不画玩家/信标，小地图继续显示上一个已标定图层）

## 配置

首次注入后生成 `config.ini`（模块侧，改完重新注入生效）：

| 键 | 默认 | 说明 |
|---|---|---|
| ToggleMapKey | F9 | 大地图窗口显示/隐藏热键（Unity KeyCode 名） |
| ToggleHudKey | F7 | 小地图档位循环热键 |
| FontSize | 20 | HUD 字号 |
| WorldRange | 2000 | 底图世界半径 |
| MinimapPixels | 360 | 小地图像素直径 |
| MinimapSpans | 200,300,500,1000 | 小地图档位（米），逗号分隔；F7 循环末尾固定追加“关”档 |
| CreatureWhitelist | 11 种大型敌对生物 | 大地图红三角白名单（TechType 名逗号分隔）；删掉该行 = 显示全部攻击性生物 |

大地图窗口与模块之间另有一份 `SNMapSettings.ini`（`ShowWindow` / `MinimapPixels` / `ShowCreatures` / `ShowScanSignals` / `CreatureShow` / `WindowLayer`），由窗口写、模块读，不需要手改。其中 `CreatureShow` 是设置窗口勾选出来的"要显示的物种"清单（TechType 名逗号分隔，`*`=全部，空=全不显示）；没有这个键时，模块回落到 `config.ini` 的 `CreatureWhitelist`。

## 构建与发布包 Build

```text
src/SNMapWindow.cs      独立大地图窗口 (WinForms, C#5, 蓝色主题)  -> csc @src/window.rsp  (winexe, x64)
src/SNInjectorGui.cs    GUI 注入器 (WinForms, C#5, 自动注入)      -> csc @src/gui.rsp     (winexe, x64)
src/bootstrap.c         引导层 (C, llvm-mingw)                    -> gcc @src/build.rsp
src/SnMapStandalone.cs  游戏内模块 (C#5, 无 BepInEx)              -> csc @src/compile.rsp (nostdlib, 对齐游戏 mscorlib)
src/SNMapShared.cs      模块与窗口共享协议/生物群系中文表（编进上面两个目标）
```

Release 压缩包解压即用（含全部图层）；仓库不含二进制与地图图片。

## 实现原理

1. `SNInjector.exe`：按进程名找 PID（或用户选窗口）→ `CreateRemoteThread + LoadLibraryW` 注入 `SNMapBoot.dll`
2. `SNMapBoot.dll`：定位游戏 Mono 运行时导出（`mono-2.0-bdwgc.dll`）→ `WH_GETMESSAGE` 钩住进程全部线程 → **由主窗口线程**（游戏主线程，Unity API 要求）经 `mono_domain_assembly_open` 加载 `SNMapManaged.v200.dll` 并调用 `Boot.InstallMain()`；导出 `SNMapTrigger` 供热重载
3. `SNMapManaged.v200.dll`：`AddComponent` 挂载行为，IMGUI 绘制 HUD + 圆形小地图；朝向用 `MainCamera.camera`（`Player.main.transform` 不随视角旋转）；信标反射读取 `PingManager.pings`（私有静态字典）；以 20Hz 写 `SNMapState.bin`（Unity 裁剪版 System.Core 没有 MemoryMappedFiles，故用文件交换）
4. `SNMapWindow.exe`：轮询 `SNMapState.bin` 渲染独立大地图窗口，mip 金字塔预缩放（参考 Xaero's Minimap），单实例

## 致谢 Credits

- 主地图：KT411 2024 中文全标注图；图层来源：[subnauticawiki.com](https://www.subnauticawiki.com/zh/map)（碧蓝之星汉化组）等社区作品，版权归原作者所有
- 地图图片不随本仓库分发，随 Release 包附带仅用于便利玩家，侵权请联系删除

## License

[MIT](LICENSE)
