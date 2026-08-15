# DuckovDLSS

Experimental NVIDIA DLSS Super Resolution and DLAA integration for **Escape from Duckov**.

《逃离鸭科夫》（Escape from Duckov）的实验性 NVIDIA DLSS Super Resolution / DLAA 模组。

> This is an unofficial community modification. It is not affiliated with, sponsored by, or endorsed by Team Soda, Bilibili or NVIDIA.
>
> 本模组是非官方社区实验项目，不代表游戏开发商、发行商或 NVIDIA 官方支持。

## Download / 下载

Download the current package from [GitHub Releases](https://github.com/SAOG0721/DuckovDLSS/releases/latest).

请从 [GitHub Releases](https://github.com/SAOG0721/DuckovDLSS/releases/latest) 下载当前版本。

Current release: **v0.4.1**

Package SHA-256: `83A12916D3F705D0F21A8930E1D2F9EDD86567F09EA79EA29648BA51E6243873`

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

## Controls / 操作

- `F8`: open or close the draggable control panel / 打开或关闭可拖动控制面板
- `DLSS: ON/OFF`: enable or disable DLSS inside the panel / 在面板内启用或关闭 DLSS
- Mode buttons: Ultra Performance, Performance, Balanced, Quality and DLAA / 直接选择五个档位
- `Show compact technical info overlay`: show or hide the technical information panel / 显示或隐藏技术信息框

The old direct `F9` mode-cycle and `F10` information hotkeys are no longer used. DLAA uses 100% input resolution, native NGX DLAA mode and preset K.

旧版 `F9` 循环档位和 `F10` 信息开关已取消。DLAA 以 100% 输入分辨率运行，并使用原生 NGX DLAA 模式和 preset K。

## Technical notes / 技术说明

- Reuses URP TAA jitter, reversed-Z depth and motion vectors.
- Orders DLSS/NGX evaluation on Unity's Direct3D 11 render thread.
- Resolves the current URP post-process color target at execution time, preserving its graphics format. This avoids washed-out output when optional post-processing passes change the color-buffer swap order.
- Submits URP 14 translation-matrix jitter with the matching NGX pixel-space sign. HDRP/DSP `m02/m12` sign rules do not apply to Duckov's URP `m03/m13` path.
- Treats the current post-tonemap input as LDR and does not request NGX auto exposure for that path.
- Transparent objects, particles and camera-space UI do not currently have a reactive mask and may show temporal artifacts.
- The release includes the NVIDIA-signed production `nvngx_dlss.dll`; its redistribution is governed by the accompanying `nvngx_dlss.license.txt`.

This remains an experimental release. The managed build and packaged hashes are verified, but standardized in-game A/B captures for the final jitter correction are still recommended.

## Uninstall / 卸载

Disable the mod in Duckov's mod menu, or remove the `DuckovDLSS` directory while the game is closed. When normally deactivated, the mod restores the original URP render scale, upscaling filter and MSAA state.
