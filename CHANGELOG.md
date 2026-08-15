# Changelog

## 0.4.1

- Added a draggable `F8` control panel containing DLSS enable/disable, direct Ultra Performance / Performance / Balanced / Quality / DLAA selection, and the technical-info display toggle.
- Removed the old direct `F8` enable toggle, `F9` mode cycle and `F10` information toggle.
- Fixed washed-out color caused by caching URP's camera-color handle before optional post-processing passes had completed their ping-pong swaps.
- Preserved the active source `GraphicsFormat` for the DLSS output and disabled NGX auto exposure for the current post-tonemap LDR input path.
- Corrected the jitter sign for Duckov's URP 14 `m03/m13` translation-matrix convention; the native centered Halton sequence and motion-vector history remain unchanged.
- Retained native NGX DLAA at 100% render scale and the NVIDIA-signed production runtime.

## 0.3.0

- Added native NGX DLAA at 100% render scale with preset K.
- Added `F10` toggle for the mod information panel.
- Added Quality, Balanced, Performance, Ultra Performance and DLAA mode cycling on `F9`.
- Fixed the NVIDIA development indicator Y orientation.
- Cached Unity native texture pointers and removed the per-frame fallback upscale after successful NGX evaluation.
- Reduced managed/native status logging overhead.
- Replaced the development NGX runtime with the NVIDIA-signed production runtime for distribution.
