# Changelog

## 0.5.1 - 2026-08-21

- Added optional luminance-domain 3×3 Gaussian USM sharpening on the original DLSS output.
- Added live sharpening control from 0 to 1 with an exact zero-strength bypass.
- Changed controls to `F8` for DLSS enable/disable and `F9` for the control panel.
- Added a native-TAA recovery frame after DLSS evaluation failures.
- Added game-input capture while the panel is open.
- Added explicit render-thread shutdown for the native bridge.
- Kept Direct3D 11, direct NGX DLSS SR/DLAA, URP jitter, reversed-Z depth, and motion-vector integration.

## 0.4.1

- Added a draggable control panel with direct mode selection and technical information.
- Fixed color-target handling after optional URP post-processing swaps.
- Corrected the jitter sign for Duckov's URP translation-matrix convention.
- Preserved native NGX DLAA at 100% input resolution.

## 0.3.0

- Added native NGX DLAA and the production NVIDIA DLSS runtime.
- Added initial quality-mode controls and native texture caching.
