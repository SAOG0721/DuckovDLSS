using System;
using System.IO;
using System.Reflection;
using Duckov.Modding;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace DuckovDLSS;

public sealed class ModBehaviour : Duckov.Modding.ModBehaviour
{
    private const string ModVersion = "0.5.1";
    private const int ControlWindowId = 0x444C5353;

    private float _nextScan;
    private Rect _controlWindow = new(140f, 100f, 650f, 450f);
    private GUIStyle _labelStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _activeButtonStyle;
    private bool _panelVisible;
    private bool _showInfo = true;
    private bool _cursorStateSaved;
    private bool _inputWarningLogged;
    private CursorLockMode _cursorLockBeforePanel;
    private bool _cursorVisibleBeforePanel;

    protected override void OnAfterSetup()
    {
        try
        {
            DisableFSR2IfLoaded();
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new InvalidOperationException("The current DLSS bridge requires Direct3D 11");

            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            DLSSRuntime.PluginPath = directory;
            if (System.Runtime.InteropServices.Marshal.SizeOf<NativeDLSS.ExecuteParams>() != 96)
                throw new InvalidOperationException("Managed DLSS event ABI is not 96 bytes");
            string bridgePath = Path.Combine(directory, "duckov-dlss-bridge.dll");
            if (!File.Exists(bridgePath) || NativeDLSS.LoadLibrary(bridgePath) == IntPtr.Zero)
                throw new FileNotFoundException("Failed to preload the render-thread NGX bridge", bridgePath);

            DLSSRuntime.Asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset ??
                                QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (DLSSRuntime.Asset == null)
                throw new InvalidOperationException("Active URP asset not found");

            DLSSRuntime.OriginalRenderScale = DLSSRuntime.Asset.renderScale;
            DLSSRuntime.OriginalUpscaler = DLSSRuntime.Asset.upscalingFilter;
            DLSSRuntime.OriginalMsaa = DLSSRuntime.Asset.msaaSampleCount;

            DLSSRuntime.Feature = ScriptableObject.CreateInstance<DLSSFeature>();
            DLSSRuntime.Feature.name = "Duckov DLSS Runtime Feature";
            DLSSRuntime.Feature.Create();
            foreach (ScriptableRendererData data in Resources.FindObjectsOfTypeAll<ScriptableRendererData>())
            {
                if (data == null || data.rendererFeatures.Contains(DLSSRuntime.Feature))
                    continue;
                data.rendererFeatures.Add(DLSSRuntime.Feature);
                data.SetDirty();
                DLSSRuntime.RendererData.Add(data);
            }

            if (DLSSRuntime.RendererData.Count == 0)
                throw new InvalidOperationException("No URP renderer data found");

            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Camera.onPreCull += OnCameraPreCull;
            DLSSRuntime.ApplyQuality();
            ScanTargetCamera();
            Debug.Log($"[DuckovDLSS] Direct NGX bridge loaded. Renderer assets patched: {DLSSRuntime.RendererData.Count}");
        }
        catch (Exception ex)
        {
            DLSSRuntime.Enabled = false;
            DLSSRuntime.Status = "DLSS setup failed: " + ex.Message;
            Debug.LogException(ex);
        }
    }

    private static void DisableFSR2IfLoaded()
    {
        Type runtime = Type.GetType("DuckovFSR2.FSR2Runtime, DuckovFSR2", false);
        FieldInfo enabled = runtime?.GetField("Enabled", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (enabled != null && enabled.FieldType == typeof(bool) && (bool)enabled.GetValue(null))
        {
            enabled.SetValue(null, false);
            MethodInfo apply = runtime.GetMethod("ApplyQuality", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            apply?.Invoke(null, null);
            Debug.LogWarning("[DuckovDLSS] DuckovFSR2 was active and has been disabled to prevent renderer conflicts.");
        }
    }

    private static void OnActiveSceneChanged(Scene previous, Scene next)
    {
        DLSSRuntime.TargetCamera = null;
        DLSSPass.ResetHistory();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        DLSSRuntime.TargetCamera = null;
        DLSSPass.ResetHistory();
    }

    private static void OnCameraPreCull(Camera camera)
    {
        if (!DLSSRuntime.Enabled || camera == null || DLSSRuntime.TargetCamera == null || camera != DLSSRuntime.TargetCamera)
            return;

        // URP exposes the game's temporal reset request here and decrements it once
        // per rendered frame inside the pipeline. The mod disables the local TAA
        // resolve, so nothing else consumes this signal; map it to a DLSS history
        // reset so teleports and camera cuts requested by the game are honoured.
        UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
        if (data != null && data.resetHistory)
            DLSSPass.RequestReset();
    }

    private void Update()
    {
        DLSSPass.FlushPendingDestroy();

        if (Input.GetKeyDown(KeyCode.F8))
            SetDlssEnabled(!DLSSRuntime.Enabled);
        if (Input.GetKeyDown(KeyCode.F9))
            SetPanelVisible(!_panelVisible);

        // Some game states re-apply their cursor policy every frame. Keep the cursor
        // usable while the control window is open, then restore the exact old state.
        if (_panelVisible)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SetGameInputBlocked(true);
        }

        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 2f;
            ScanTargetCamera();
        }
    }

    private void SetPanelVisible(bool visible)
    {
        if (_panelVisible == visible)
            return;

        _panelVisible = visible;
        if (visible)
        {
            _cursorLockBeforePanel = Cursor.lockState;
            _cursorVisibleBeforePanel = Cursor.visible;
            _cursorStateSaved = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SetGameInputBlocked(true);
        }
        else
        {
            SetGameInputBlocked(false);
            if (_cursorStateSaved)
            {
                Cursor.visible = _cursorVisibleBeforePanel;
                Cursor.lockState = _cursorLockBeforePanel;
                _cursorStateSaved = false;
            }
        }

        Debug.Log($"[DuckovDLSS] F9 control panel {(visible ? "opened" : "closed")}");
    }

    private void SetGameInputBlocked(bool blocked)
    {
        try
        {
            if (blocked)
                global::InputManager.DisableInput(gameObject);
            else
                global::InputManager.ActiveInput(gameObject);
            _inputWarningLogged = false;
        }
        catch (Exception ex)
        {
            // InputManager may not have an instance during scene transitions. The
            // open-panel path retries every Update; close/deactivation must continue.
            if (blocked && !_inputWarningLogged)
            {
                _inputWarningLogged = true;
                Debug.LogWarning("[DuckovDLSS] Game input capture pending: " + ex.Message);
            }
        }
    }

    private static void SetDlssEnabled(bool enabled)
    {
        if (DLSSRuntime.Enabled == enabled)
            return;

        DLSSRuntime.Enabled = enabled;
        DLSSPass.ResetHistory();
        DLSSRuntime.ApplyQuality();
        if (enabled)
            ScanTargetCamera();
        Debug.Log($"[DuckovDLSS] F8 DLSS {(enabled ? "enabled" : "disabled")}");
    }

    private void SetInfoVisible(bool visible)
    {
        if (_showInfo == visible)
            return;

        _showInfo = visible;
        Debug.Log($"[DuckovDLSS] Info overlay {(visible ? "shown" : "hidden")} from control panel");
    }

    private static void ScanTargetCamera()
    {
        if (!DLSSRuntime.Enabled)
            return;

        Camera camera = Camera.main;
        if (camera == null)
        {
            foreach (Camera candidate in Camera.allCameras)
            {
                if (DLSSRuntime.IsTargetCamera(candidate))
                {
                    camera = candidate;
                    break;
                }
            }
        }

        if (camera == null)
        {
            DLSSRuntime.Status = "Waiting for the main game camera";
            return;
        }

        DLSSRuntime.TargetCamera = camera;

        UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
        if (data == null || data.antialiasing != AntialiasingMode.TemporalAntiAliasing)
            DLSSRuntime.Status = "Main camera TAA must remain enabled for native jitter and motion vectors";
    }

    private void OnGUI()
    {
        if (!_panelVisible && !_showInfo)
            return;

        EnsureGuiStyles();
        GUI.depth = -10000;

        if (_showInfo)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.Box(new Rect(18f, 122f, 940f, 104f), GUIContent.none);
            GUI.color = Color.white;
            string state = DLSSRuntime.Enabled ? "ON" : "OFF";
            GUI.Label(new Rect(30f, 129f, 920f, 88f),
                $"Duckov DLSS {ModVersion}  [F8 DLSS / F9 PANEL]  SR {state} / {DLSSRuntime.ModeName}  Gaussian USM {DLSSRuntime.SharpenStrength:0.00}\n" +
                DLSSRuntime.Status,
                _labelStyle);
        }

        if (!_panelVisible)
            return;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        float maxX = Mathf.Max(0f, Screen.width - _controlWindow.width);
        float maxY = Mathf.Max(0f, Screen.height - _controlWindow.height);
        _controlWindow.x = Mathf.Clamp(_controlWindow.x, 0f, maxX);
        _controlWindow.y = Mathf.Clamp(_controlWindow.y, 0f, maxY);
        _controlWindow = GUI.Window(ControlWindowId, _controlWindow, DrawControlWindow,
            $"Duckov DLSS {ModVersion}  -  F9 closes this panel");
    }

    private void DrawControlWindow(int windowId)
    {
        GUILayout.Space(8f);

        GUILayout.BeginHorizontal();
        GUILayout.Label("DLSS Super Resolution", _sectionStyle, GUILayout.Width(390f));
        if (GUILayout.Button(DLSSRuntime.Enabled ? "DLSS: ON" : "DLSS: OFF",
                DLSSRuntime.Enabled ? _activeButtonStyle : GUI.skin.button,
                GUILayout.Width(190f), GUILayout.Height(34f)))
        {
            SetDlssEnabled(!DLSSRuntime.Enabled);
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        GUILayout.Label("Quality mode", _labelStyle);
        GUILayout.BeginHorizontal();
        DrawModeButton(DLSSMode.UltraPerformance, "Ultra Performance");
        DrawModeButton(DLSSMode.Performance, "Performance");
        DrawModeButton(DLSSMode.Balanced, "Balanced");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        DrawModeButton(DLSSMode.Quality, "Quality");
        DrawModeButton(DLSSMode.DLAA, "DLAA");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        GUILayout.Label("DLSS Gaussian USM sharpening", _labelStyle, GUILayout.Width(430f));
        GUILayout.Label(DLSSRuntime.SharpenStrength.ToString("0.00"), _labelStyle, GUILayout.Width(80f));
        GUILayout.EndHorizontal();
        float sharpenStrength = GUILayout.HorizontalSlider(DLSSRuntime.SharpenStrength, 0f, 1f,
            GUILayout.Width(600f), GUILayout.Height(24f));
        if (!Mathf.Approximately(sharpenStrength, DLSSRuntime.SharpenStrength))
            DLSSRuntime.SetSharpenStrength(sharpenStrength);
        GUILayout.Label("0 = exact original DLSS output; 1 = maximum luminance-domain 3x3 Gaussian USM", _labelStyle);

        GUILayout.Space(8f);
        bool showInfo = GUILayout.Toggle(_showInfo, " Show compact technical info overlay");
        if (showInfo != _showInfo)
            SetInfoVisible(showInfo);

        GUILayout.Space(8f);
        GUILayout.Label(
            $"API: {SystemInfo.graphicsDeviceType}    SR: {(DLSSRuntime.Enabled ? "active" : "off")}    " +
            $"Mode: {DLSSRuntime.ModeName}    Render scale: {DLSSRuntime.RenderScale:F3}    Gaussian USM {DLSSRuntime.SharpenStrength:0.00}\n{DLSSRuntime.Status}",
            _labelStyle);

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close panel", GUILayout.Height(32f)))
            SetPanelVisible(false);

        GUI.DragWindow(new Rect(0f, 0f, _controlWindow.width, 30f));
    }

    private void DrawModeButton(DLSSMode mode, string label)
    {
        GUIStyle style = DLSSRuntime.Mode == mode ? _activeButtonStyle : GUI.skin.button;
        if (GUILayout.Button((DLSSRuntime.Mode == mode ? "● " : string.Empty) + label,
                style, GUILayout.Width(190f), GUILayout.Height(31f)))
        {
            DLSSRuntime.SetMode(mode);
        }
    }

    private void EnsureGuiStyles()
    {
        if (_labelStyle != null)
            return;

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            wordWrap = true,
            normal = { textColor = Color.white }
        };
        _sectionStyle = new GUIStyle(_labelStyle)
        {
            fontSize = 18,
            normal = { textColor = new Color(0.35f, 0.85f, 1f) }
        };
        _activeButtonStyle = new GUIStyle(GUI.skin.button)
        {
            normal = { textColor = new Color(0.25f, 1f, 0.45f) }
        };
    }

    protected override void OnBeforeDeactivate()
    {
        SetPanelVisible(false);
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Camera.onPreCull -= OnCameraPreCull;
        DLSSPass.FlushPendingDestroy();
        DLSSRuntime.Enabled = false;
        if (DLSSRuntime.Asset != null)
        {
            DLSSRuntime.Asset.renderScale = DLSSRuntime.OriginalRenderScale;
            DLSSRuntime.Asset.upscalingFilter = DLSSRuntime.OriginalUpscaler;
            DLSSRuntime.Asset.msaaSampleCount = DLSSRuntime.OriginalMsaa;
        }

        foreach (ScriptableRendererData data in DLSSRuntime.RendererData)
        {
            if (data == null)
                continue;
            data.rendererFeatures.Remove(DLSSRuntime.Feature);
            data.SetDirty();
        }
        DLSSRuntime.RendererData.Clear();

        // Shutdown must be an explicit render-thread event; merely setting a native
        // flag after removing the feature can leave no later render callback to run it.
        IntPtr shutdownData = IntPtr.Zero;
        CommandBuffer shutdownCommand = null;
        try
        {
            shutdownData = NativeDLSS.DLSSCreateShutdownEventData();
            if (shutdownData != IntPtr.Zero)
            {
                shutdownCommand = new CommandBuffer { name = "Duckov DLSS Render-Thread Shutdown" };
                shutdownCommand.IssuePluginEventAndData(NativeDLSS.DLSSGetRenderEventFunc(), 2, shutdownData);
                Graphics.ExecuteCommandBuffer(shutdownCommand);
                shutdownData = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[DuckovDLSS] Could not queue render-thread shutdown: " + ex.Message);
        }
        finally
        {
            shutdownCommand?.Release();
            if (shutdownData != IntPtr.Zero)
                NativeDLSS.DLSSReleaseEventData(shutdownData);
        }

        DLSSPass.DisposeResources();
        if (DLSSRuntime.Feature != null)
            UnityEngine.Object.Destroy(DLSSRuntime.Feature);
        DLSSRuntime.Feature = null;
    }
}
