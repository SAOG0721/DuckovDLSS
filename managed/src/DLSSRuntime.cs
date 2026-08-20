using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DuckovDLSS;

internal enum DLSSMode { Quality, Balanced, Performance, UltraPerformance, DLAA }

internal static class DLSSRuntime
{
    private static readonly float[] Scales = { 2f / 3f, 1f / 1.7f, 0.5f, 1f / 3f, 1f };
    private static readonly string[] Names = { "Quality / K", "Balanced / K", "Performance / M", "Ultra Performance / L", "DLAA / K" };

    internal static bool Enabled = true;
    internal static DLSSMode Mode = DLSSMode.Quality;
    internal static float SharpenStrength = 0.5f;
    internal static string Status = "Waiting for the main TAA camera";
    internal static bool DispatchQueued;
    internal static UniversalRenderPipelineAsset Asset;
    internal static float OriginalRenderScale;
    internal static UpscalingFilterSelection OriginalUpscaler;
    internal static int OriginalMsaa;
    internal static readonly List<ScriptableRendererData> RendererData = new();
    internal static DLSSFeature Feature;
    internal static string PluginPath;
    internal static Camera TargetCamera;
    internal static float RenderScale => Scales[(int)Mode];
    internal static string ModeName => Names[(int)Mode];

    internal static bool IsTargetCamera(Camera camera)
    {
        if (camera == null || camera.cameraType != CameraType.Game || camera.orthographic)
            return false;

        Camera main = TargetCamera;
        if (main == null)
        {
            main = Camera.main;
            TargetCamera = main;
        }
        return main != null ? camera == main : camera.name == "Main Camera";
    }

    internal static void ApplyQuality()
    {
        if (Asset == null)
            return;

        Asset.renderScale = Enabled ? RenderScale : OriginalRenderScale;
        Asset.upscalingFilter = Enabled ? UpscalingFilterSelection.Linear : OriginalUpscaler;
        Asset.msaaSampleCount = Enabled ? 1 : OriginalMsaa;
        Status = Enabled ? $"DLSS {ModeName} selected; waiting for render-thread evaluation" : "DLSS disabled";
    }

    internal static void SetMode(DLSSMode mode)
    {
        if (Mode == mode)
            return;

        Mode = mode;
        DLSSPass.ResetHistory();
        ApplyQuality();
        Debug.Log($"[DuckovDLSS] Mode changed to {ModeName}; renderScale={RenderScale:F3}");
    }

    internal static void SetSharpenStrength(float strength)
    {
        SharpenStrength = Mathf.Clamp01(strength);
    }
}
