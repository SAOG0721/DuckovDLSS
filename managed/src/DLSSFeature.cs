using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DuckovDLSS;

public sealed class DLSSFeature : ScriptableRendererFeature
{
    private DLSSPass _dispatch;
    private DLSSPresentPass _present;

    public override void Create()
    {
        _dispatch = new DLSSPass { renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing };

        // URP's FinalBlitPass is AfterRendering + 1. Using +2 puts the DLSS image
        // after that pass while still preceding Duckov's later overlay/UI pass.
        _present = new DLSSPresentPass { renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRendering + 2) };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
    {
        ref CameraData cameraData = ref data.cameraData;
        Camera camera = cameraData.camera;
        if (!DLSSRuntime.Enabled || !DLSSRuntime.IsTargetCamera(camera) ||
            !cameraData.resolveFinalTarget || !cameraData.postProcessEnabled)
            return;

        if (cameraData.antialiasing != AntialiasingMode.TemporalAntiAliasing)
        {
            DLSSRuntime.Status = "Main camera TAA is required: DLSS uses its native jitter and motion-vector history";
            return;
        }

        // A contract/native failure is observed too late to restore this frame's
        // local TAA decision. Leave the following frame entirely to the game's TAA,
        // then retry DLSS with a reset on the next frame.
        if (!DLSSPass.AllowDlssThisFrame())
        {
            DLSSRuntime.DispatchQueued = false;
            return;
        }

        int renderWidth = Mathf.Max(1, cameraData.cameraTargetDescriptor.width);
        int renderHeight = Mathf.Max(1, cameraData.cameraTargetDescriptor.height);
        // The GPU projection helpers query cameraColorTarget and are not legal during
        // AddRenderPasses. URP's CPU projection is J * camera.projectionMatrix, so this
        // recovers exactly the already-generated TAA jitter without touching a target.
        Matrix4x4 jitterMatrix = cameraData.GetProjectionMatrix() * camera.projectionMatrix.inverse;

        float clipJitterX = jitterMatrix.m03 * renderWidth * 0.5f;
        float clipJitterY = jitterMatrix.m13 * renderHeight * 0.5f;

        var frame = new DLSSFrameState
        {
            camera = camera,
            frameNumber = Time.frameCount,
            renderWidth = renderWidth,
            renderHeight = renderHeight,
            outputWidth = Mathf.Max(1, camera.pixelWidth),
            outputHeight = Mathf.Max(1, camera.pixelHeight),
            // URP applies TAA as a positive clip-space translation J * P. The matrix
            // difference therefore already has NGX's input-pixel sign. HDRP's
            // apparently opposite convention comes from shifting perspective planes
            // (m02/m12), whose resulting clip displacement is inverted; do not copy
            // that extra negation onto URP's m03/m13 translation.
            jitterX = clipJitterX,
            jitterY = clipJitterY
        };

        _dispatch.SetFrameState(frame);

        // CameraData's jitter and MotionVectorsPersistentData were prepared before
        // renderer features are added. Disable only the local TAA resolve from here on.
        cameraData.antialiasing = AntialiasingMode.None;
        renderer.EnqueuePass(_dispatch);
        renderer.EnqueuePass(_present);
    }
}

internal struct DLSSFrameState
{
    internal Camera camera;
    internal int frameNumber;
    internal int renderWidth;
    internal int renderHeight;
    internal int outputWidth;
    internal int outputHeight;
    internal float jitterX;
    internal float jitterY;
}

internal sealed class DLSSPass : ScriptableRenderPass
{
    private static RenderTexture _rawOutput;
    private static RenderTexture _sharpenedOutput;
    private static bool _reset = true;
    private static bool _hasHistory;
    private static int _lastCameraId;
    private static int _lastFrameNumber = -1;
    private static int _lastRenderWidth;
    private static int _lastRenderHeight;
    private static int _lastOutputWidth;
    private static int _lastOutputHeight;
    private static DLSSMode _lastMode;
    private static float _lastFov;
    private static float _lastNear;
    private static float _lastFar;
    private static float _lastAspect;
    private static Vector3 _lastPosition;
    private static Quaternion _lastRotation;
    private static ulong _nextSequence = 1;
    private static ulong _lastCompletedSequence;
    private static ulong _lastFailureSequence;
    private static string _lastLoggedError;
    private static bool _loggedFirstSuccess;
    private static bool _nativeOperational;
    private static int _taaFallbackFrames;
    private static float _nextStatusUpdate;
    private static readonly NativeTextureCache _colorCache = new();
    private static readonly NativeTextureCache _depthCache = new();
    private static readonly NativeTextureCache _motionCache = new();
    private static readonly NativeTextureCache _rawOutputCache = new();
    private static readonly NativeTextureCache _sharpenedOutputCache = new();

    private DLSSFrameState _frame;
    private bool _hasFrame;

    internal static RenderTexture Output => _sharpenedOutput;

    // RenderTextures are replaced from the render thread inside EnsureOutput, but
    // UnityEngine.Object.Destroy must run on the main thread. Replaced targets are
    // parked here and destroyed from ModBehaviour.Update.
    internal static readonly List<RenderTexture> PendingDestroy = new();

    internal static void RequestReset()
    {
        _reset = true;
        _hasHistory = false;
    }

    internal static bool AllowDlssThisFrame()
    {
        // Poll render-thread completion before making the per-frame TAA decision.
        // A native failure already completed by now can therefore keep native TAA
        // in this same frame rather than waiting for another low-resolution frame.
        if (!RefreshNativeCompletion())
        {
            _taaFallbackFrames = 0;
            return false;
        }
        if (_taaFallbackFrames <= 0)
            return true;
        _taaFallbackFrames--;
        return false;
    }

    private static void RequestTaaFallback()
    {
        _reset = true;
        _hasHistory = false;
        _nativeOperational = false;
        _taaFallbackFrames = Mathf.Max(_taaFallbackFrames, 1);
        DLSSRuntime.DispatchQueued = false;
    }

    internal static void FlushPendingDestroy()
    {
        lock (PendingDestroy)
        {
            for (int i = 0; i < PendingDestroy.Count; i++)
            {
                RenderTexture texture = PendingDestroy[i];
                if (texture == null)
                    continue;
                texture.Release();
                UnityEngine.Object.Destroy(texture);
            }
            PendingDestroy.Clear();
        }
    }

    internal static void DisposeResources()
    {
        QueueForDestroy(ref _rawOutput);
        QueueForDestroy(ref _sharpenedOutput);
        FlushPendingDestroy();
        InvalidateNativePointers();
        _nativeOperational = false;
        _hasHistory = false;
        _reset = true;
        _taaFallbackFrames = 0;
        DLSSRuntime.DispatchQueued = false;
    }

    private static void QueueForDestroy(ref RenderTexture texture)
    {
        if (texture == null)
            return;
        lock (PendingDestroy)
            PendingDestroy.Add(texture);
        texture = null;
    }

    internal DLSSPass()
    {
        ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);
    }

    internal void SetFrameState(DLSSFrameState frame)
    {
        _frame = frame;
        _hasFrame = true;
    }

    internal static void ResetHistory()
    {
        _reset = true;
        _hasHistory = false;
        DLSSRuntime.DispatchQueued = false;
        InvalidateNativePointers();
    }

    private static void InvalidateNativePointers()
    {
        _colorCache.Clear();
        _depthCache.Clear();
        _motionCache.Clear();
        _rawOutputCache.Clear();
        _sharpenedOutputCache.Clear();
    }

    private static bool EnsureOutput(int width, int height, RenderTexture source)
    {
        // Preserve the actual GraphicsFormat/color-space metadata instead of reducing
        // it to the legacy RenderTextureFormat enum. This keeps the NGX output on the
        // same linear precision path as URP's post-processed camera color.
        RenderTextureDescriptor descriptor = source.descriptor;
        descriptor.width = width;
        descriptor.height = height;
        descriptor.depthBufferBits = 0;
        descriptor.msaaSamples = 1;
        descriptor.bindMS = false;
        descriptor.useMipMap = false;
        descriptor.autoGenerateMips = false;
        descriptor.enableRandomWrite = true;
        descriptor.useDynamicScale = false;
        descriptor.memoryless = RenderTextureMemoryless.None;

        if (_rawOutput != null && _sharpenedOutput != null &&
            _rawOutput.width == width && _rawOutput.height == height &&
            _rawOutput.graphicsFormat == descriptor.graphicsFormat && _rawOutput.sRGB == descriptor.sRGB &&
            _sharpenedOutput.width == width && _sharpenedOutput.height == height &&
            _sharpenedOutput.graphicsFormat == descriptor.graphicsFormat && _sharpenedOutput.sRGB == descriptor.sRGB)
            return false;

        // Main-thread destruction only: park replaced targets and let Update
        // destroy them after the render event has retained its native references.
        QueueForDestroy(ref _rawOutput);
        QueueForDestroy(ref _sharpenedOutput);

        _rawOutput = new RenderTexture(descriptor)
        {
            name = "Duckov_DLSS_RawOutput",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _sharpenedOutput = new RenderTexture(descriptor)
        {
            name = "Duckov_DLSS_SharpenedOutput",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        if (!_rawOutput.Create() || !_sharpenedOutput.Create())
            throw new InvalidOperationException("Could not create the DLSS raw/sharpened output textures");
        _reset = true;
        _nativeOperational = false;
        InvalidateNativePointers();
        return true;
    }

    private static bool DetectDiscontinuity(in DLSSFrameState frame)
    {
        Camera camera = frame.camera;
        Transform transform = camera.transform;
        if (!_hasHistory || _reset || camera.GetInstanceID() != _lastCameraId ||
            frame.frameNumber != _lastFrameNumber + 1 ||
            frame.renderWidth != _lastRenderWidth || frame.renderHeight != _lastRenderHeight ||
            frame.outputWidth != _lastOutputWidth || frame.outputHeight != _lastOutputHeight ||
            DLSSRuntime.Mode != _lastMode ||
            Mathf.Abs(camera.fieldOfView - _lastFov) > 0.01f ||
            Mathf.Abs(camera.nearClipPlane - _lastNear) > 0.001f ||
            Mathf.Abs(camera.farClipPlane - _lastFar) > 0.01f ||
            Mathf.Abs(camera.aspect - _lastAspect) > 0.001f)
            return true;

        if ((transform.position - _lastPosition).sqrMagnitude > 100f ||
            Quaternion.Angle(transform.rotation, _lastRotation) > 60f)
            return true;

        return false;
    }

    private static void CommitFrame(in DLSSFrameState frame)
    {
        Camera camera = frame.camera;
        Transform transform = camera.transform;
        _lastCameraId = camera.GetInstanceID();
        _lastFrameNumber = frame.frameNumber;
        _lastRenderWidth = frame.renderWidth;
        _lastRenderHeight = frame.renderHeight;
        _lastOutputWidth = frame.outputWidth;
        _lastOutputHeight = frame.outputHeight;
        _lastMode = DLSSRuntime.Mode;
        _lastFov = camera.fieldOfView;
        _lastNear = camera.nearClipPlane;
        _lastFar = camera.farClipPlane;
        _lastAspect = camera.aspect;
        _lastPosition = transform.position;
        _lastRotation = transform.rotation;
        _hasHistory = true;
        _reset = false;
    }

    private static string GetNativeStatus()
    {
        var buffer = new StringBuilder(1024);
        NativeDLSS.DLSSGetStatus(buffer, (uint)buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetNativeFailureStatus()
    {
        var buffer = new StringBuilder(1024);
        NativeDLSS.DLSSGetFailureStatus(buffer, (uint)buffer.Capacity);
        return buffer.ToString();
    }

    private static bool RefreshNativeCompletion()
    {
        ulong completed = NativeDLSS.DLSSGetCompletedSequence();
        ulong failure = NativeDLSS.DLSSGetFailureSequence();
        // Failures are sticky by sequence so a later success cannot hide an
        // intervening failed event if the main thread did not poll every event.
        if (failure != 0 && failure != _lastFailureSequence)
        {
            _lastFailureSequence = failure;
            _lastCompletedSequence = completed;
            RequestTaaFallback();
            string failureStatus = GetNativeFailureStatus();
            DLSSRuntime.Status = $"DLSS render-thread failure at seq={failure}: {failureStatus}";
            if (_lastLoggedError != DLSSRuntime.Status)
            {
                _lastLoggedError = DLSSRuntime.Status;
                Debug.LogError("[DuckovDLSS] " + DLSSRuntime.Status);
            }
            return false;
        }
        if (completed == 0 || completed == _lastCompletedSequence)
            return true;

        _lastCompletedSequence = completed;
        if (NativeDLSS.DLSSGetLastSuccess() != 0)
        {
            bool firstSuccess = !_nativeOperational;
            _nativeOperational = true;
            _lastLoggedError = null;
            if (!_loggedFirstSuccess)
            {
                _loggedFirstSuccess = true;
                string nativeStatus = GetNativeStatus();
                DLSSRuntime.Status = nativeStatus;
                Debug.Log("[DuckovDLSS] " + nativeStatus);
            }
            else if (firstSuccess || Time.unscaledTime >= _nextStatusUpdate)
            {
                _nextStatusUpdate = Time.unscaledTime + 1f;
                DLSSRuntime.Status = $"DLSS active: {_lastRenderWidth}x{_lastRenderHeight} -> {_lastOutputWidth}x{_lastOutputHeight}; completed seq={completed}";
            }
            return true;
        }
        else
        {
            RequestTaaFallback();
            string nativeStatus = GetNativeStatus();
            DLSSRuntime.Status = "DLSS render-thread failure: " + nativeStatus;
            if (_lastLoggedError != DLSSRuntime.Status)
            {
                _lastLoggedError = DLSSRuntime.Status;
                Debug.LogError("[DuckovDLSS] " + DLSSRuntime.Status);
            }
            return false;
        }
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (!DLSSRuntime.Enabled || !_hasFrame || _frame.camera != renderingData.cameraData.camera)
            return;

        DLSSRuntime.DispatchQueued = false;

        CommandBuffer command = CommandBufferPool.Get("Duckov DLSS Render-Thread Dispatch");
        IntPtr eventData = IntPtr.Zero;
        bool eventQueued = false;
        try
        {
            if (!RefreshNativeCompletion())
                return;

            // Query at execution time. URP post processing ping-pongs camera color
            // buffers, so the handle captured during SetupRenderPasses can become the
            // pre-Uber/untone-mapped buffer after an optional effect changes parity.
            RenderTexture source = renderingData.cameraData.renderer.cameraColorTargetHandle?.rt;
            Texture depth = Shader.GetGlobalTexture("_CameraDepthTexture");
            Texture motion = Shader.GetGlobalTexture("_MotionVectorTexture");
            if (source == null || depth == null || motion == null)
            {
                DLSSRuntime.Status = $"Missing DLSS input: color={source != null}, depth={depth != null}, motion={motion != null}";
                RequestTaaFallback();
                return;
            }

            // Strict input contract: color, depth and motion must all match the
            // camera target descriptor size captured for this frame. A silent min()
            // shrink hides broken states and evaluates with mismatched inputs.
            int renderWidth = _frame.renderWidth;
            int renderHeight = _frame.renderHeight;
            if (source.width != renderWidth || source.height != renderHeight ||
                depth.width != renderWidth || depth.height != renderHeight ||
                motion.width != renderWidth || motion.height != renderHeight)
            {
                RequestTaaFallback();
                DLSSRuntime.Status = $"DLSS input size mismatch: color={source.width}x{source.height}, depth={depth.width}x{depth.height}, motion={motion.width}x{motion.height}; expected {renderWidth}x{renderHeight}";
                if (_lastLoggedError != DLSSRuntime.Status)
                {
                    _lastLoggedError = DLSSRuntime.Status;
                    Debug.LogError("[DuckovDLSS] " + DLSSRuntime.Status);
                }
                return;
            }
            bool outputChanged = EnsureOutput(_frame.outputWidth, _frame.outputHeight, source);
            bool reset = outputChanged || DetectDiscontinuity(_frame);

            IntPtr colorPtr = _colorCache.Get(source);
            IntPtr depthPtr = _depthCache.Get(depth);
            IntPtr motionPtr = _motionCache.Get(motion);
            IntPtr rawOutputPtr = _rawOutputCache.Get(_rawOutput);
            IntPtr sharpenedOutputPtr = _sharpenedOutputCache.Get(_sharpenedOutput);
            if (colorPtr == IntPtr.Zero || depthPtr == IntPtr.Zero || motionPtr == IntPtr.Zero ||
                rawOutputPtr == IntPtr.Zero || sharpenedOutputPtr == IntPtr.Zero)
                throw new InvalidOperationException($"Unity native pointer missing: color={colorPtr != IntPtr.Zero}, depth={depthPtr != IntPtr.Zero}, motion={motionPtr != IntPtr.Zero}, raw={rawOutputPtr != IntPtr.Zero}, sharpened={sharpenedOutputPtr != IntPtr.Zero}");

            ulong sequence = _nextSequence++;
            var parameters = new NativeDLSS.ExecuteParams
            {
                color = colorPtr,
                depth = depthPtr,
                motionVectors = motionPtr,
                rawOutput = rawOutputPtr,
                sharpenedOutput = sharpenedOutputPtr,
                renderWidth = (uint)renderWidth,
                renderHeight = (uint)renderHeight,
                outputWidth = (uint)_frame.outputWidth,
                outputHeight = (uint)_frame.outputHeight,
                mode = (uint)DLSSRuntime.Mode,

                // The injection point is after URP tone mapping, so this is an LDR-linear input
                // even when the camera's intermediate target uses a half-float texture.
                hdr = 0,
                reset = (byte)(reset ? 1 : 0),
                depthInverted = 1,
                motionVectorsJittered = 0,
                jitterX = _frame.jitterX,
                jitterY = _frame.jitterY,

                // URP stores forward (current - previous) normalized UV motion. NGX wants
                // backward screen-pixel motion, hence both negative input dimensions.
                motionScaleX = -renderWidth,
                motionScaleY = -renderHeight,
                sharpenStrength = DLSSRuntime.SharpenStrength,
                sequence = sequence
            };

            eventData = NativeDLSS.DLSSCreateEventData(ref parameters, DLSSRuntime.PluginPath);
            if (eventData == IntPtr.Zero)
                throw new InvalidOperationException(GetNativeStatus());

            // Pre-fill the independently presented output every frame. If the native
            // render event fails after being queued, this frame shows a current spatial
            // upscale instead of stale DLSS history; the next frame restores game TAA.
            command.Blit(source, _sharpenedOutput);
            command.IssuePluginEventAndData(NativeDLSS.DLSSGetRenderEventFunc(), 1, eventData);
            eventQueued = true;
            context.ExecuteCommandBuffer(command);

            CommitFrame(_frame);
            DLSSRuntime.DispatchQueued = true;
            if (Time.unscaledTime >= _nextStatusUpdate)
            {
                _nextStatusUpdate = Time.unscaledTime + 1f;
                DLSSRuntime.Status = $"DLSS queued: {renderWidth}x{renderHeight} -> {_frame.outputWidth}x{_frame.outputHeight}; completed seq={_lastCompletedSequence}; reset={reset}; Gaussian USM={DLSSRuntime.SharpenStrength:0.00}";
            }
        }
        catch (Exception ex)
        {
            RequestTaaFallback();
            DLSSRuntime.Status = "DLSS dispatch failed: " + ex.Message;
            if (_lastLoggedError != DLSSRuntime.Status)
            {
                _lastLoggedError = DLSSRuntime.Status;
                Debug.LogError("[DuckovDLSS] " + DLSSRuntime.Status);
            }
        }
        finally
        {
            if (!eventQueued && eventData != IntPtr.Zero)
                NativeDLSS.DLSSReleaseEventData(eventData);
            CommandBufferPool.Release(command);
            _hasFrame = false;
        }
    }

    private sealed class NativeTextureCache
    {
        private Texture _texture;
        private int _width;
        private int _height;
        private IntPtr _pointer;

        internal IntPtr Get(Texture texture)
        {
            if (!ReferenceEquals(_texture, texture) || _width != texture.width || _height != texture.height || _pointer == IntPtr.Zero)
            {
                _texture = texture;
                _width = texture.width;
                _height = texture.height;
                _pointer = texture.GetNativeTexturePtr();
            }
            return _pointer;
        }

        internal void Clear()
        {
            _texture = null;
            _width = 0;
            _height = 0;
            _pointer = IntPtr.Zero;
        }
    }
}

internal sealed class DLSSPresentPass : ScriptableRenderPass
{
    public override void Execute(ScriptableRenderContext context, ref RenderingData data)
    {
        if (!DLSSRuntime.Enabled || !DLSSRuntime.DispatchQueued || DLSSPass.Output == null)
            return;

        CommandBuffer command = CommandBufferPool.Get("Duckov DLSS Present");
        command.Blit(DLSSPass.Output, BuiltinRenderTextureType.CameraTarget);
        context.ExecuteCommandBuffer(command);
        CommandBufferPool.Release(command);
    }
}
