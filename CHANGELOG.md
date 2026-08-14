# Changelog

## 0.3.0

- Added native NGX DLAA at 100% render scale with preset K.
- Added `F10` toggle for the mod information panel.
- Added Quality, Balanced, Performance, Ultra Performance and DLAA mode cycling on `F9`.
- Fixed the NVIDIA development indicator Y orientation.
- Cached Unity native texture pointers and removed the per-frame fallback upscale after successful NGX evaluation.
- Reduced managed/native status logging overhead.
- Replaced the development NGX runtime with the NVIDIA-signed production runtime for distribution.

