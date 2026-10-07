# Subnautica SNMap 深海迷航小地图

A trainer-style minimap & fullscreen map overlay for **Subnautica (Steam, x64)**.
深海迷航（Steam x64）的小地图 + 全屏地图辅助工具。修改器式注入：**不随游戏启动、不依赖任何模组框架（BepInEx 等）**。

> 仅用于单人游戏与学习交流。请勿用于任何联机对抗场景。

## 功能

- 左上角实时坐标：`X / Z / 深度 / 朝向(8向) / 生物群系(中文)`（朝向取自渲染相机）
- **F7** 圆形小地图：`关 → 200m → 300m → 500m` 循环，以玩家为中心，红箭头=玩家朝向，彩点=信标/生命舱，顶部 N 指北
- **F9** 全屏大地图：自动解锁鼠标 + `GameInput.ClearInput` 屏蔽游戏操作；滚轮缩放（以鼠标为中心，最高 16x）、左键拖动平移；**顶部 `－`/`＋` 按钮切换地图图层**，小地图跟随当前图层
- 多图层：`maps/` 文件夹里的每张 png/jpg 都是一个图层（主地图、失落之河、蛇菇洞、熔岩堡、海皇监狱、利维坦分布等），大地图小地图同步切换
- 游戏更新一般无需重做：坐标直接读游戏对象（`Player.main` 等），**不使用内存偏移**

## 使用

1. 启动游戏（Steam）
2. 运行 `SNInjector.exe`：**检测到游戏进程后自动注入**（每 1.5 秒探测，游戏重启换 PID 后会再次自动注入）
   - 检测不到可点 **[选择窗口]** 手动选窗口，再点 **[注入]**
3. 绿色提示即成功。**发布包解压到任意目录即可用**，不需要放进游戏目录

## 图层与标定（maps/）

- `maps/` 下按文件名排序加载图层，文件名建议 `01_名字.png` 序号前缀决定顺序
- 默认标定：图像中心 = 世界 `(0,0)`，四边 = `±WorldRange`，上=北（`-Z`），右=东（`+X`）
- 区域裁剪图（如失落之河等洞穴图）可在 `maps/maps.ini` 里按层标定：
  `文件名.jpg=minX,maxX,minZ,maxZ`
- 未标定的非方形图层自动进入"仅浏览"模式（可全屏查看，但不画玩家/信标，小地图继续显示上一个已标定图层）

## 配置

首次注入后生成 `config.ini`：

| 键 | 默认 | 说明 |
|---|---|---|
| ToggleMapKey | F9 | 全屏大地图热键（Unity KeyCode 名） |
| ToggleHudKey | F7 | 小地图档位循环热键 |
| FontSize | 20 | HUD 字号 |
| WorldRange | 2000 | 底图世界半径 |
| MinimapPixels | 220 | 小地图像素直径 |
| MinimapSpans | 200,300,500 | 小地图档位（米），逗号分隔 |

## 构建与发布包 Build

```text
src/SNInjectorGui.cs    GUI 注入器 (WinForms, C#5, 蓝色主题, 自动注入) -> csc @src/gui.rsp     (winexe, x64)
src/bootstrap.c         引导层 (C, llvm-mingw)                                        -> gcc @src/build.rsp
src/SnMapStandalone.cs  功能本体 (C#5, 无 BepInEx)                                    -> csc @src/compile.rsp (nostdlib, 对齐游戏 mscorlib)
```

Release 压缩包解压即用（含全部图层）；仓库不含二进制与地图图片。

## 实现原理

1. `SNInjector.exe`：按进程名找 PID（或用户选窗口）→ `CreateRemoteThread + LoadLibraryW` 注入 `SNMapBoot.dll`
2. `SNMapBoot.dll`：定位游戏 Mono 运行时导出（`mono-2.0-bdwgc.dll`）→ `WH_GETMESSAGE` 钩住进程全部线程 → **由主窗口线程**（游戏主线程，Unity API 要求）经 `mono_domain_assembly_open` 加载 `SNMapManaged.dll` 并调用 `Boot.InstallMain()`
3. `SNMapManaged.dll`：`AddComponent` 挂载行为，IMGUI 绘制 HUD/小地图/全屏地图；朝向用 `MainCamera.camera`（`Player.main.transform` 不随视角旋转）；信标反射读取 `PingManager.pings`（私有静态字典）

## 致谢 Credits

- 主地图：KT411 2024 中文全标注图；图层来源：[subnauticawiki.com](https://www.subnauticawiki.com/zh/map)（碧蓝之星汉化组）等社区作品，版权归原作者所有
- 地图图片不随本仓库分发，随 Release 包附带仅用于便利玩家，侵权请联系删除

## License

[MIT](LICENSE)
