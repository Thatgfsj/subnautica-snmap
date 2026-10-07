# Subnautica SNMap 深海迷航小地图

A trainer-style minimap & fullscreen map overlay for **Subnautica (Steam, x64)**.
深海迷航（Steam x64）的小地图 + 全屏地图辅助工具。修改器式注入：**不随游戏启动、不依赖任何模组框架（BepInEx 等）**。

> 仅用于单人游戏与学习交流。请勿用于任何联机对抗场景。

## 功能

- 左上角实时坐标：`X / Z / 深度 / 朝向(8向) / 生物群系(中文)`
- **F7** 圆形小地图：`关 → 200m → 300m → 500m` 循环，以玩家为中心，红箭头=玩家朝向，彩点=信标/生命舱，顶部 N 指北
- **F9** 全屏大地图：自动解锁鼠标 + `GameInput.ClearInput` 屏蔽游戏操作，滚轮缩放（以鼠标为中心，最高 16x），左键拖动平移；信标带名字
- 游戏更新一般无需重做：坐标直接读游戏对象（`Player.main` 等），**不使用内存偏移**

## 使用

1. 启动游戏（Steam）
2. 运行 `SNInjector.exe`：自动检测游戏进程（找不到时可手动选择窗口）
3. 点击【注入】，绿色提示即成功
4. 每次重启游戏需要重新注入一次

## 关于 map.png（不随仓库/发布包分发）

底图 `map.png` 为第三方绘制的版权作品，**本仓库与 Release 均不附带**。请自行准备一张底图放到 `SNInjector.exe` 同目录，命名为 `map.png`，标定要求：

- 图像中心 = 世界坐标 `(0, 0)`
- 四条边 = `±2000`
- 图片上方 = 北（`-Z` 方向），右 = 东（`+X`）

推荐使用社区广为流传的 KT411 2024 中文全标注图（请自行获取）。没有 `map.png` 时坐标 HUD 正常工作，地图界面会提示找不到图片。

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

## 构建 Build

```text
src/SNInjectorGui.cs    GUI 注入器 (WinForms, C#5)     -> csc @src/gui.rsp        (winexe, x64)
src/bootstrap.c         引导层 (C, llvm-mingw)          -> gcc @src/build.rsp
src/SnMapStandalone.cs  功能本体 (C#5, 无 BepInEx)      -> csc @src/compile.rsp    (nostdlib, 对齐游戏自带 mscorlib)
```

`*.rsp` 内使用本机路径（游戏 `Subnautica_Data/Managed`），按自己环境修改后编译。

## 实现原理

1. `SNInjector.exe`：按进程名找 PID（或用户选窗口）→ `CreateRemoteThread + LoadLibraryW` 注入 `SNMapBoot.dll`
2. `SNMapBoot.dll`：定位游戏 Mono 运行时导出（`mono-2.0-bdwgc.dll`）→ `WH_GETMESSAGE` 钩住进程全部线程 → **由主窗口线程**（游戏主线程，Unity API 要求）经 `mono_domain_assembly_open` 加载 `SNMapManaged.dll` 并调用 `Boot.InstallMain()`
3. `SNMapManaged.dll`：`AddComponent` 挂载行为，IMGUI 绘制 HUD/小地图/全屏地图；信标数据反射读取 `PingManager.pings`（私有静态字典）

## License

[MIT](LICENSE)
