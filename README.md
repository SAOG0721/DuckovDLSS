# DuckovDLSS

Experimental NVIDIA DLSS Super Resolution and DLAA integration for **Escape from Duckov**.

《逃离鸭科夫》（Escape from Duckov）的实验性 NVIDIA DLSS Super Resolution / DLAA 模组。

> This is an unofficial community modification. It is not affiliated with, sponsored by, or endorsed by Team Soda, Bilibili or NVIDIA.
>
> 本模组是非官方社区实验项目，不代表游戏开发商、发行商或 NVIDIA 官方支持。

## Download / 下载

Download the current package from [GitHub Releases](https://github.com/SAOG0721/DuckovDLSS/releases/latest).

请从 [GitHub Releases](https://github.com/SAOG0721/DuckovDLSS/releases/latest) 下载当前版本。

Current release: **v0.3.0**  
Package SHA-256: `7C10DF325792E01056921304B6D2998FB47BB75893F048378FF3C9EC8821A081`

## Requirements / 运行要求

- Escape from Duckov 2.3.30 / Unity 2022.3.62f2
- Windows x64 and Direct3D 11
- NVIDIA RTX GPU and a current NVIDIA driver
- Do not enable another temporal-upscaling mod at the same time
- 请勿与 DuckovFSR2 或其他时域超分模组同时启用

## Installation / 安装

Extract every file from the release archive directly into:

```text
Escape from Duckov\Duckov_Data\Mods\DuckovDLSS\
```

Do not add another nested version folder.

把压缩包内全部文件直接解压到上述目录，不要额外嵌套一层版本文件夹。

## Controls / 按键

- `F8`: enable or disable DLSS / 开关 DLSS
- `F9`: Quality → Balanced → Performance → Ultra Performance → DLAA
- `F10`: show or hide the mod information panel / 显示或隐藏模组信息框

DLAA uses 100% input resolution, native NGX DLAA mode and preset K.

## Technical notes / 技术说明

- Reuses URP TAA jitter, reversed-Z depth and motion vectors.
- Orders DLSS/NGX evaluation on Unity's Direct3D 11 render thread.
- Transparent objects, particles and camera-space UI do not currently have a reactive mask and may show temporal artifacts.
- The release includes the NVIDIA-signed production `nvngx_dlss.dll`; its redistribution is governed by the accompanying `nvngx_dlss.license.txt`.

## Uninstall / 卸载

Disable the mod in Duckov's mod menu, or remove the `DuckovDLSS` directory while the game is closed. When normally deactivated, the mod restores the original URP render scale, upscaling filter and MSAA state.

