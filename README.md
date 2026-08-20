# DuckovDLSS

[English](#english) | [中文](#中文)

## English

Experimental NVIDIA DLSS Super Resolution and DLAA integration for **Escape from Duckov** on Direct3D 11.

### Features

- Direct NVIDIA NGX DLSS SR and DLAA evaluation on Unity's render thread.
- Quality, Balanced, Performance, Ultra Performance, and DLAA modes.
- Reuses URP TAA jitter, reversed-Z depth, and motion vectors.
- Optional luminance-domain 3×3 Gaussian USM sharpening after DLSS, adjustable from 0 to 1; zero is an exact bypass.
- Restores native TAA for a recovery frame after an evaluation failure and performs explicit render-thread shutdown.
- Captures game input while the control panel is open.

### Requirements

- Escape from Duckov `2.3.30` / Unity `2022.3.62f2`
- Windows x64 and Direct3D 11
- NVIDIA RTX GPU and a current NVIDIA driver
- Do not enable another temporal-upscaling mod at the same time

### Download and installation

Download `DuckovDLSS-v0.5.1-win64.zip` from the [v0.5.1 release](https://github.com/SAOG0721/DuckovDLSS/releases/tag/v0.5.1).

```text
67AC1FE005543881DCCD1DDED44F2876620978292220914726F72A64475640D6  DuckovDLSS-v0.5.1-win64.zip
```

Extract every file directly into:

```text
Escape from Duckov\Duckov_Data\Mods\DuckovDLSS\
```

Do not add another nested version directory.

### Controls

- `F8`: enable or disable DLSS.
- `F9`: open or close the draggable control panel.
- The panel selects the DLSS mode, sharpening strength, and technical-information display.

### Current limitations

- Transparent objects, particles, and camera-space UI do not currently have a reactive mask and may show temporal artifacts.
- This release was rebuilt and package-verified against the versions above. Final visual quality and long-session stability still require in-game validation on each system.

### Build

Managed bridge requirements: .NET SDK targeting `netstandard2.1` and a legally installed copy of the game.

```powershell
$env:DUCKOV_GAME_DIR = 'C:\path\to\Escape from Duckov'
dotnet build .\managed\DuckovDLSS.csproj -c Release
```

Native bridge requirements: Visual Studio 2022 C++ tools, CMake, Windows SDK `fxc.exe`, NVIDIA Streamline SDK 2.12 (for NGX headers/import library), and Unity NativeRenderingPlugin headers.

```powershell
cmake -S .\native -B .\native\build -G Ninja -DCMAKE_BUILD_TYPE=Release `
  -DSTREAMLINE_ROOT='C:\path\to\Streamline-SDK-2.12.0' `
  -DUNITY_NATIVE_PLUGIN_ROOT='C:\path\to\Unity-NativeRenderingPlugin\PluginSource\source'
cmake --build .\native\build --config Release
```

The repository contains only the mod's authored source. Game assemblies and redistributable runtime DLLs are not tracked.

### Third-party components and status

The binary release includes NVIDIA's signed DLSS runtime under the accompanying NVIDIA license. The native bridge uses Unity's MIT-licensed NativeRenderingPlugin headers. See [third-party notices](THIRD_PARTY_NOTICES.md).

This is an independent community mod and is not affiliated with or endorsed by Team Soda, Bilibili, Unity, or NVIDIA. No open-source license has been selected for the mod source at this time.

---

## 中文

为《逃离鸭科夫》Direct3D 11 提供实验性的 NVIDIA DLSS 超分辨率和 DLAA 集成。

### 功能

- 在 Unity 渲染线程上直接执行 NVIDIA NGX DLSS SR / DLAA。
- 支持质量、均衡、性能、超级性能和 DLAA 档位。
- 复用 URP TAA 抖动、反向 Z 深度和运动矢量。
- 可在 DLSS 原始输出后应用亮度域 3×3 Gaussian USM 锐化，强度范围 0–1；设为 0 时完全绕过。
- DLSS 评估失败后恢复一帧原生 TAA，并在退出时执行显式渲染线程清理。
- 控制面板打开时会接管游戏输入。

### 运行要求

- 《逃离鸭科夫》`2.3.30` / Unity `2022.3.62f2`
- Windows x64、Direct3D 11
- NVIDIA RTX 显卡和可用的新版本驱动
- 不要同时启用其他时域超分辨率模组

### 下载与安装

从 [v0.5.1 Release](https://github.com/SAOG0721/DuckovDLSS/releases/tag/v0.5.1) 下载 `DuckovDLSS-v0.5.1-win64.zip`，把压缩包内全部文件直接解压到：

```text
67AC1FE005543881DCCD1DDED44F2876620978292220914726F72A64475640D6  DuckovDLSS-v0.5.1-win64.zip
```

```text
Escape from Duckov\Duckov_Data\Mods\DuckovDLSS\
```

不要额外嵌套版本目录。

### 操作

- `F8`：启用或关闭 DLSS。
- `F9`：打开或关闭可拖动控制面板。
- 面板内可选择 DLSS 档位、锐化强度和技术信息显示。

### 当前限制

- 透明物体、粒子和相机空间 UI 目前没有 reactive mask，可能仍有时域瑕疵。
- 本版本已针对上述游戏版本完成重新构建和发布包校验；不同系统上的最终画质与长时间稳定性仍需实际游戏验证。

### 构建

托管桥接需要可编译 `netstandard2.1` 的 .NET SDK，以及用户合法安装的游戏。原生桥接还需要 Visual Studio 2022 C++ 工具、CMake、Windows SDK `fxc.exe`、NVIDIA Streamline SDK 2.12 和 Unity NativeRenderingPlugin 头文件。命令见上方英文部分。

仓库只包含模组作者编写的关键源码，不跟踪游戏程序集或可再分发的运行时 DLL。

### 第三方组件与项目状态

二进制发布包包含 NVIDIA 签名的 DLSS 运行库，并附带 NVIDIA 许可文本；原生桥接使用 Unity 以 MIT 许可发布的 NativeRenderingPlugin 头文件。详见[第三方说明](THIRD_PARTY_NOTICES.md)。

本项目是独立社区模组，与游戏开发商、发行商、Unity 或 NVIDIA 不存在隶属或背书关系。目前尚未为模组源码选择开源许可证。
