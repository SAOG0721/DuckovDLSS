#include <Windows.h>
#include <d3d11.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <utility>

#include "Unity/IUnityGraphics.h"
#include "gaussian_usm_cs.h"
#include "nvsdk_ngx.h"
#include "nvsdk_ngx_helpers.h"

namespace
{
#pragma pack(push, 8)
struct ExecuteParams
{
    void* color;
    void* depth;
    void* motionVectors;
    void* rawOutput;
    void* sharpenedOutput;
    uint32_t renderWidth;
    uint32_t renderHeight;
    uint32_t outputWidth;
    uint32_t outputHeight;
    uint32_t mode;
    uint8_t hdr;
    uint8_t reset;
    uint8_t depthInverted;
    uint8_t motionVectorsJittered;
    float jitterX;
    float jitterY;
    float motionScaleX;
    float motionScaleY;
    float sharpenStrength;
    uint64_t sequence;
};
#pragma pack(pop)

static_assert(sizeof(ExecuteParams) == 96, "Managed/native ExecuteParams layout mismatch");

struct EventPacket
{
    ExecuteParams params{};
    std::wstring pluginPath;
};

constexpr int kEvaluateEvent = 1;
constexpr int kShutdownEvent = 2;

std::mutex g_statusMutex;
std::string g_status{"Direct NGX bridge loaded; waiting for the first render event"};
std::string g_failureStatus;
std::atomic<uint64_t> g_completedSequence{0};
std::atomic<uint64_t> g_failureSequence{0};
std::atomic<int> g_lastSuccess{0};

ID3D11Device* g_device{};
ID3D11DeviceContext* g_context{};
NVSDK_NGX_Parameter* g_parameters{};
NVSDK_NGX_Handle* g_feature{};
bool g_initialized{};
uint32_t g_lastRenderWidth{};
uint32_t g_lastRenderHeight{};
uint32_t g_lastOutputWidth{};
uint32_t g_lastOutputHeight{};
uint32_t g_lastMode{UINT32_MAX};
bool g_lastHdr{};
uint64_t g_featureCreateCount{};
bool g_reportNextEvaluation{true};

// Gaussian USM writes to a separate texture. NGX always owns rawOutput, so the
// post-process never reads and writes the same D3D11 resource. This also leaves
// the managed fallback texture intact if NGX or the sharpen pass fails.
struct SharpenResources
{
    ID3D11Resource* rawOutput{};
    ID3D11Resource* sharpenedOutput{};
    UINT width{};
    UINT height{};
    ID3D11ShaderResourceView* inputSrv{};
    ID3D11UnorderedAccessView* outputUav{};
    ID3D11ComputeShader* shader{};
    ID3D11Buffer* constants{};
    ID3D11SamplerState* linearSampler{};
};
SharpenResources g_sharpen;

void SetStatus(std::string status)
{
    std::lock_guard<std::mutex> lock(g_statusMutex);
    g_status = std::move(status);
}

void SetResultStatus(const char* stage, NVSDK_NGX_Result result)
{
    char text[384]{};
    std::snprintf(text, sizeof(text), "%s: NGX result 0x%08x", stage,
        static_cast<unsigned int>(result));
    SetStatus(text);
}

void NVSDK_CONV NgxLogCallback(const char* message, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature)
{
    if (!message)
        return;
    if (std::strstr(message, "error") || std::strstr(message, "Error") ||
        std::strstr(message, "ERROR") || std::strstr(message, "fail") ||
        std::strstr(message, "Fail") || std::strstr(message, "FAIL"))
        SetStatus(std::string("NGX: ") + message);
}

void ReleasePacket(EventPacket* packet)
{
    if (!packet)
        return;
    auto release = [](void* pointer)
    {
        if (pointer)
            static_cast<ID3D11Resource*>(pointer)->Release();
    };
    release(packet->params.color);
    release(packet->params.depth);
    release(packet->params.motionVectors);
    release(packet->params.rawOutput);
    release(packet->params.sharpenedOutput);
    delete packet;
}

void ReleaseSharpenResources()
{
    auto release = [](IUnknown* object)
    {
        if (object)
            object->Release();
    };
    release(g_sharpen.inputSrv);
    release(g_sharpen.outputUav);
    release(g_sharpen.shader);
    release(g_sharpen.constants);
    release(g_sharpen.linearSampler);
    g_sharpen = {};
}

void ReleaseFeature()
{
    if (g_feature)
    {
        NVSDK_NGX_D3D11_ReleaseFeature(g_feature);
        g_feature = nullptr;
    }
    g_lastMode = UINT32_MAX;
}

void ShutdownState()
{
    ReleaseFeature();
    ReleaseSharpenResources();
    if (g_parameters)
    {
        NVSDK_NGX_D3D11_DestroyParameters(g_parameters);
        g_parameters = nullptr;
    }
    if (g_initialized && g_device)
        NVSDK_NGX_D3D11_Shutdown1(g_device);
    g_initialized = false;
    if (g_context)
        g_context->Release();
    if (g_device)
        g_device->Release();
    g_context = nullptr;
    g_device = nullptr;
}

NVSDK_NGX_PerfQuality_Value GetMode(uint32_t mode)
{
    switch (mode)
    {
    case 1: return NVSDK_NGX_PerfQuality_Value_Balanced;
    case 2: return NVSDK_NGX_PerfQuality_Value_MaxPerf;
    case 3: return NVSDK_NGX_PerfQuality_Value_UltraPerformance;
    case 4: return NVSDK_NGX_PerfQuality_Value_DLAA;
    default: return NVSDK_NGX_PerfQuality_Value_MaxQuality;
    }
}

bool InitializeFromResource(ID3D11Resource* resource, const std::wstring& pluginPath)
{
    if (!resource || pluginPath.empty())
    {
        SetStatus("NGX initialization received a null texture or plugin path");
        return false;
    }

    ID3D11Device* resourceDevice{};
    resource->GetDevice(&resourceDevice);
    if (!resourceDevice)
    {
        SetStatus("Could not obtain Unity's D3D11 device from the color texture");
        return false;
    }

    if (g_initialized && resourceDevice == g_device)
    {
        resourceDevice->Release();
        return true;
    }

    ShutdownState();
    g_device = resourceDevice;
    g_device->GetImmediateContext(&g_context);
    if (!g_context)
    {
        SetStatus("Could not obtain Unity's D3D11 immediate context");
        ShutdownState();
        return false;
    }

    const wchar_t* paths[] = {pluginPath.c_str()};
    NVSDK_NGX_FeatureCommonInfo commonInfo{};
    commonInfo.PathListInfo.Path = paths;
    commonInfo.PathListInfo.Length = 1;
    commonInfo.LoggingInfo.LoggingCallback = NgxLogCallback;
    commonInfo.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_OFF;
    commonInfo.LoggingInfo.DisableOtherLoggingSinks = true;

    constexpr const char* projectId = "6f3d0e49-8da1-4f55-b47f-31ca4df5e9c8";
    NVSDK_NGX_Result result = NVSDK_NGX_D3D11_Init_with_ProjectID(
        projectId, NVSDK_NGX_ENGINE_TYPE_UNITY, "2022.3.62f2",
        pluginPath.c_str(), g_device, &commonInfo, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(result))
    {
        SetResultStatus("NVSDK_NGX_D3D11_Init_with_ProjectID failed", result);
        ShutdownState();
        return false;
    }
    g_initialized = true;

    result = NVSDK_NGX_D3D11_GetCapabilityParameters(&g_parameters);
    if (NVSDK_NGX_FAILED(result) || !g_parameters)
    {
        SetResultStatus("NVSDK_NGX_D3D11_GetCapabilityParameters failed", result);
        ShutdownState();
        return false;
    }

    int available = 0;
    result = g_parameters->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
    if (NVSDK_NGX_FAILED(result) || !available)
    {
        SetResultStatus("DLSS SuperSampling is unavailable", result);
        ShutdownState();
        return false;
    }

    SetStatus("Direct NGX initialized on Unity's D3D11 render thread");
    return true;
}

bool CreateFeature(const ExecuteParams& p)
{
    ReleaseFeature();

    g_parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality,
        static_cast<int>(NVSDK_NGX_DLSS_Hint_Render_Preset_K));
    g_parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA,
        static_cast<int>(NVSDK_NGX_DLSS_Hint_Render_Preset_K));
    g_parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced,
        static_cast<int>(NVSDK_NGX_DLSS_Hint_Render_Preset_K));
    g_parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance,
        static_cast<int>(NVSDK_NGX_DLSS_Hint_Render_Preset_M));
    g_parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance,
        static_cast<int>(NVSDK_NGX_DLSS_Hint_Render_Preset_L));

    NVSDK_NGX_DLSS_Create_Params create{};
    create.Feature.InWidth = p.renderWidth;
    create.Feature.InHeight = p.renderHeight;
    create.Feature.InTargetWidth = p.outputWidth;
    create.Feature.InTargetHeight = p.outputHeight;
    create.Feature.InPerfQualityValue = GetMode(p.mode);
    create.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
    if (p.hdr)
    {
        create.InFeatureCreateFlags |= NVSDK_NGX_DLSS_Feature_Flags_IsHDR;
        create.InFeatureCreateFlags |= NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
    }
    if (p.depthInverted)
        create.InFeatureCreateFlags |= NVSDK_NGX_DLSS_Feature_Flags_DepthInverted;
    if (p.motionVectorsJittered)
        create.InFeatureCreateFlags |= NVSDK_NGX_DLSS_Feature_Flags_MVJittered;
    create.InEnableOutputSubrects = false;

    NVSDK_NGX_Result result = NGX_D3D11_CREATE_DLSS_EXT(g_context, &g_feature, g_parameters, &create);
    if (NVSDK_NGX_FAILED(result) || !g_feature)
    {
        SetResultStatus("NGX_D3D11_CREATE_DLSS_EXT failed", result);
        g_feature = nullptr;
        return false;
    }

    g_lastRenderWidth = p.renderWidth;
    g_lastRenderHeight = p.renderHeight;
    g_lastOutputWidth = p.outputWidth;
    g_lastOutputHeight = p.outputHeight;
    g_lastMode = p.mode;
    g_lastHdr = p.hdr != 0;
    ++g_featureCreateCount;
    g_reportNextEvaluation = true;
    return true;
}

DXGI_FORMAT LinearViewFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS:
        return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default:
        return format;
    }
}

bool EnsureSharpenResources(ID3D11Resource* rawOutput, ID3D11Resource* sharpenedOutput,
    UINT width, UINT height)
{
    if (g_sharpen.rawOutput == rawOutput && g_sharpen.sharpenedOutput == sharpenedOutput &&
        g_sharpen.width == width && g_sharpen.height == height && g_sharpen.inputSrv &&
        g_sharpen.outputUav && g_sharpen.shader && g_sharpen.constants &&
        g_sharpen.linearSampler)
        return true;

    ReleaseSharpenResources();

    auto* rawTexture = static_cast<ID3D11Texture2D*>(rawOutput);
    auto* sharpenedTexture = static_cast<ID3D11Texture2D*>(sharpenedOutput);
    D3D11_TEXTURE2D_DESC rawDesc{};
    D3D11_TEXTURE2D_DESC sharpenedDesc{};
    rawTexture->GetDesc(&rawDesc);
    sharpenedTexture->GetDesc(&sharpenedDesc);
    if (rawDesc.Width != width || rawDesc.Height != height ||
        sharpenedDesc.Width != width || sharpenedDesc.Height != height)
    {
        SetStatus("Gaussian USM resource dimensions do not match the declared output");
        return false;
    }
    if (rawDesc.Format != sharpenedDesc.Format || rawDesc.SampleDesc.Count != 1 ||
        sharpenedDesc.SampleDesc.Count != 1)
    {
        SetStatus("Gaussian USM requires matching non-MSAA raw and sharpened outputs");
        return false;
    }
    if (!(rawDesc.BindFlags & D3D11_BIND_SHADER_RESOURCE) ||
        !(sharpenedDesc.BindFlags & D3D11_BIND_UNORDERED_ACCESS))
    {
        SetStatus("Gaussian USM textures lack required SRV/UAV bind flags");
        return false;
    }

    const DXGI_FORMAT rawFormat = LinearViewFormat(rawDesc.Format);
    const DXGI_FORMAT sharpenedFormat = LinearViewFormat(sharpenedDesc.Format);
    if (rawFormat == DXGI_FORMAT_UNKNOWN || sharpenedFormat == DXGI_FORMAT_UNKNOWN ||
        rawFormat != sharpenedFormat)
    {
        SetStatus("Gaussian USM received an unsupported or mismatched output format");
        return false;
    }

    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc{};
    srvDesc.Format = rawFormat;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;
    if (FAILED(g_device->CreateShaderResourceView(rawOutput, &srvDesc, &g_sharpen.inputSrv)))
    {
        SetStatus("Gaussian USM input SRV creation failed");
        ReleaseSharpenResources();
        return false;
    }

    D3D11_UNORDERED_ACCESS_VIEW_DESC uavDesc{};
    uavDesc.Format = sharpenedFormat;
    uavDesc.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
    if (FAILED(g_device->CreateUnorderedAccessView(sharpenedOutput, &uavDesc, &g_sharpen.outputUav)))
    {
        SetStatus("Gaussian USM output UAV creation failed");
        ReleaseSharpenResources();
        return false;
    }

    if (FAILED(g_device->CreateComputeShader(g_gaussianUsmCs, sizeof(g_gaussianUsmCs),
        nullptr, &g_sharpen.shader)))
    {
        SetStatus("Gaussian USM compute shader creation failed");
        ReleaseSharpenResources();
        return false;
    }

    D3D11_BUFFER_DESC cbDesc{};
    cbDesc.ByteWidth = 16;
    cbDesc.Usage = D3D11_USAGE_DYNAMIC;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(g_device->CreateBuffer(&cbDesc, nullptr, &g_sharpen.constants)))
    {
        SetStatus("Gaussian USM constant buffer creation failed");
        ReleaseSharpenResources();
        return false;
    }

    D3D11_SAMPLER_DESC samplerDesc{};
    samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(g_device->CreateSamplerState(&samplerDesc, &g_sharpen.linearSampler)))
    {
        SetStatus("Gaussian USM sampler creation failed");
        ReleaseSharpenResources();
        return false;
    }

    g_sharpen.rawOutput = rawOutput;
    g_sharpen.sharpenedOutput = sharpenedOutput;
    g_sharpen.width = width;
    g_sharpen.height = height;
    return true;
}

bool RunGaussianUsm(UINT width, UINT height, float strength)
{
    struct Constants
    {
        float invOutputWidth;
        float invOutputHeight;
        float strength;
        float padding;
    } constants{1.0f / static_cast<float>(width), 1.0f / static_cast<float>(height),
        strength, 0.0f};

    if (strength > 0.0f)
    {
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(g_context->Map(g_sharpen.constants, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        {
            SetStatus("Gaussian USM constant buffer update failed");
            return false;
        }
        std::memcpy(mapped.pData, &constants, sizeof(constants));
        g_context->Unmap(g_sharpen.constants, 0);
    }

    ID3D11ComputeShader* oldShader{};
    ID3D11ClassInstance* oldClassInstances[D3D11_SHADER_MAX_INTERFACES]{};
    UINT oldClassInstanceCount = D3D11_SHADER_MAX_INTERFACES;
    ID3D11ShaderResourceView* oldSrvs[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT]{};
    ID3D11UnorderedAccessView* oldUavs[D3D11_PS_CS_UAV_REGISTER_COUNT]{};
    ID3D11Buffer* oldConstantBuffer{};
    ID3D11SamplerState* oldSampler{};
    ID3D11RenderTargetView* oldRenderTargets[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};
    ID3D11DepthStencilView* oldDepthStencil{};
    g_context->CSGetShader(&oldShader, oldClassInstances, &oldClassInstanceCount);
    g_context->CSGetShaderResources(0, D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT, oldSrvs);
    g_context->CSGetUnorderedAccessViews(0, D3D11_PS_CS_UAV_REGISTER_COUNT, oldUavs);
    g_context->CSGetConstantBuffers(0, 1, &oldConstantBuffer);
    g_context->CSGetSamplers(0, 1, &oldSampler);
    g_context->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT,
        oldRenderTargets, &oldDepthStencil);

    // NGX may leave resources bound in arbitrary compute slots. Save and clear
    // every SRV/UAV slot before binding the separate raw/sharpened textures.
    ID3D11ShaderResourceView* noSrvs[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT]{};
    ID3D11UnorderedAccessView* noUavs[D3D11_PS_CS_UAV_REGISTER_COUNT]{};
    g_context->OMSetRenderTargets(0, nullptr, nullptr);
    g_context->CSSetShaderResources(0, D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT, noSrvs);
    g_context->CSSetUnorderedAccessViews(0, D3D11_PS_CS_UAV_REGISTER_COUNT, noUavs, nullptr);
    if (strength <= 0.0f)
    {
        // Exact zero-strength bypass: no shader arithmetic and no approximation.
        g_context->CopyResource(g_sharpen.sharpenedOutput, g_sharpen.rawOutput);
    }
    else
    {
        g_context->CSSetShader(g_sharpen.shader, nullptr, 0);
        g_context->CSSetShaderResources(0, 1, &g_sharpen.inputSrv);
        g_context->CSSetUnorderedAccessViews(0, 1, &g_sharpen.outputUav, nullptr);
        g_context->CSSetConstantBuffers(0, 1, &g_sharpen.constants);
        g_context->CSSetSamplers(0, 1, &g_sharpen.linearSampler);
        g_context->Dispatch((width + 7) / 8, (height + 7) / 8, 1);
    }

    g_context->CSSetShaderResources(0, D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT, noSrvs);
    g_context->CSSetUnorderedAccessViews(0, D3D11_PS_CS_UAV_REGISTER_COUNT, noUavs, nullptr);
    g_context->CSSetShader(oldShader, oldClassInstances, oldClassInstanceCount);
    g_context->CSSetShaderResources(0, D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT, oldSrvs);
    g_context->CSSetUnorderedAccessViews(0, D3D11_PS_CS_UAV_REGISTER_COUNT, oldUavs, nullptr);
    g_context->CSSetConstantBuffers(0, 1, &oldConstantBuffer);
    g_context->CSSetSamplers(0, 1, &oldSampler);
    g_context->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT,
        oldRenderTargets, oldDepthStencil);

    if (oldShader)
        oldShader->Release();
    for (UINT i = 0; i < oldClassInstanceCount; ++i)
        if (oldClassInstances[i]) oldClassInstances[i]->Release();
    if (oldConstantBuffer)
        oldConstantBuffer->Release();
    if (oldSampler)
        oldSampler->Release();
    for (auto* view : oldRenderTargets)
        if (view) view->Release();
    if (oldDepthStencil)
        oldDepthStencil->Release();
    for (auto* view : oldSrvs)
        if (view) view->Release();
    for (auto* view : oldUavs)
        if (view) view->Release();
    return true;
}

bool Evaluate(const ExecuteParams& p, const std::wstring& pluginPath)
{
    if (!p.color || !p.depth || !p.motionVectors || !p.rawOutput || !p.sharpenedOutput ||
        !p.renderWidth || !p.renderHeight || !p.outputWidth || !p.outputHeight)
    {
        SetStatus("DLSS evaluation received an incomplete resource or dimension block");
        return false;
    }

    if (!InitializeFromResource(static_cast<ID3D11Resource*>(p.color), pluginPath))
        return false;

    if (!g_feature || g_lastRenderWidth != p.renderWidth || g_lastRenderHeight != p.renderHeight ||
        g_lastOutputWidth != p.outputWidth || g_lastOutputHeight != p.outputHeight ||
        g_lastMode != p.mode || g_lastHdr != (p.hdr != 0))
    {
        if (!CreateFeature(p))
            return false;
    }

    NVSDK_NGX_D3D11_DLSS_Eval_Params eval{};
    eval.Feature.pInColor = static_cast<ID3D11Resource*>(p.color);
    eval.Feature.pInOutput = static_cast<ID3D11Resource*>(p.rawOutput);
    eval.pInDepth = static_cast<ID3D11Resource*>(p.depth);
    eval.pInMotionVectors = static_cast<ID3D11Resource*>(p.motionVectors);
    eval.InJitterOffsetX = p.jitterX;
    eval.InJitterOffsetY = p.jitterY;
    eval.InRenderSubrectDimensions = {p.renderWidth, p.renderHeight};
    eval.InReset = p.reset ? 1 : 0;
    eval.InMVScaleX = p.motionScaleX;
    eval.InMVScaleY = p.motionScaleY;
    eval.InPreExposure = 1.0f;
    eval.InExposureScale = 1.0f;
    eval.InIndicatorInvertXAxis = 0;
    eval.InIndicatorInvertYAxis = 1;
    eval.InToneMapperType = NVSDK_NGX_TONEMAPPER_STRING;

    NVSDK_NGX_Result result = NGX_D3D11_EVALUATE_DLSS_EXT(g_context, g_feature, g_parameters, &eval);
    if (NVSDK_NGX_FAILED(result))
    {
        SetResultStatus("NGX_D3D11_EVALUATE_DLSS_EXT failed", result);
        return false;
    }

    if (!EnsureSharpenResources(static_cast<ID3D11Resource*>(p.rawOutput),
        static_cast<ID3D11Resource*>(p.sharpenedOutput), p.outputWidth, p.outputHeight))
        return false;

    const float sharpenStrength = std::isfinite(p.sharpenStrength)
        ? (p.sharpenStrength < 0.0f ? 0.0f : (p.sharpenStrength > 1.0f ? 1.0f : p.sharpenStrength))
        : 0.0f;
    if (!RunGaussianUsm(p.outputWidth, p.outputHeight, sharpenStrength))
        return false;

    if (g_reportNextEvaluation || p.reset)
    {
        char text[512]{};
        std::snprintf(text, sizeof(text),
            "DLSS evaluated on render thread [seq=%llu, creates=%llu]: %ux%u -> %ux%u, jitter=(%.3f,%.3f), MV scale=(%.0f,%.0f), reset=%u, Gaussian USM=%.2f",
            static_cast<unsigned long long>(p.sequence), static_cast<unsigned long long>(g_featureCreateCount),
            p.renderWidth, p.renderHeight, p.outputWidth, p.outputHeight, p.jitterX, p.jitterY,
            p.motionScaleX, p.motionScaleY, static_cast<unsigned int>(p.reset),
            static_cast<double>(sharpenStrength));
        SetStatus(text);
        g_reportNextEvaluation = false;
    }
    return true;
}

void UNITY_INTERFACE_API OnRenderEventAndData(int eventId, void* data)
{
    EventPacket* packet = static_cast<EventPacket*>(data);
    if (!packet)
        return;

    if (eventId == kShutdownEvent)
    {
        ShutdownState();
        SetStatus("Direct NGX shut down on Unity's render thread");
        ReleasePacket(packet);
        return;
    }

    const uint64_t sequence = packet->params.sequence;
    bool success = false;
    if (eventId == kEvaluateEvent)
        success = Evaluate(packet->params, packet->pluginPath);
    else
        SetStatus("Unknown Unity render event id");

    ReleasePacket(packet);
    g_lastSuccess.store(success ? 1 : 0, std::memory_order_relaxed);
    if (!success)
    {
        {
            std::lock_guard<std::mutex> lock(g_statusMutex);
            g_failureStatus = g_status;
        }
        g_failureSequence.store(sequence, std::memory_order_release);
    }
    g_completedSequence.store(sequence, std::memory_order_release);
}
}

extern "C" __declspec(dllexport) UnityRenderingEventAndData __cdecl DLSSGetRenderEventFunc()
{
    return OnRenderEventAndData;
}

extern "C" __declspec(dllexport) void* __cdecl DLSSCreateEventData(
    const ExecuteParams* parameters, const wchar_t* pluginPath)
{
    if (!parameters || !pluginPath)
    {
        SetStatus("DLSSCreateEventData received a null argument");
        return nullptr;
    }

    EventPacket* packet = new (std::nothrow) EventPacket();
    if (!packet)
    {
        SetStatus("Could not allocate DLSS render-event data");
        return nullptr;
    }

    packet->params = *parameters;
    packet->pluginPath = pluginPath;
    auto addRef = [](void* pointer)
    {
        if (pointer)
            static_cast<ID3D11Resource*>(pointer)->AddRef();
    };
    addRef(packet->params.color);
    addRef(packet->params.depth);
    addRef(packet->params.motionVectors);
    addRef(packet->params.rawOutput);
    addRef(packet->params.sharpenedOutput);
    return packet;
}

extern "C" __declspec(dllexport) void* __cdecl DLSSCreateShutdownEventData()
{
    EventPacket* packet = new (std::nothrow) EventPacket();
    if (!packet)
        SetStatus("Could not allocate DLSS shutdown-event data");
    return packet;
}

extern "C" __declspec(dllexport) void __cdecl DLSSReleaseEventData(void* eventData)
{
    ReleasePacket(static_cast<EventPacket*>(eventData));
}

extern "C" __declspec(dllexport) uint64_t __cdecl DLSSGetCompletedSequence()
{
    return g_completedSequence.load(std::memory_order_acquire);
}

extern "C" __declspec(dllexport) int __cdecl DLSSGetLastSuccess()
{
    return g_lastSuccess.load(std::memory_order_relaxed);
}

extern "C" __declspec(dllexport) uint64_t __cdecl DLSSGetFailureSequence()
{
    return g_failureSequence.load(std::memory_order_acquire);
}

extern "C" __declspec(dllexport) void __cdecl DLSSGetFailureStatus(char* buffer, uint32_t capacity)
{
    if (!buffer || capacity == 0)
        return;
    std::lock_guard<std::mutex> lock(g_statusMutex);
    std::snprintf(buffer, capacity, "%s", g_failureStatus.c_str());
}

extern "C" __declspec(dllexport) void __cdecl DLSSGetStatus(char* buffer, uint32_t capacity)
{
    if (!buffer || capacity == 0)
        return;
    std::lock_guard<std::mutex> lock(g_statusMutex);
    std::snprintf(buffer, capacity, "%s", g_status.c_str());
}
