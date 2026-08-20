cbuffer SharpenConstants : register(b0)
{
    float2 InvOutputSize;
    float Strength;
    float Padding;
};

Texture2D<float4> InputTexture : register(t0);
SamplerState LinearClampSampler : register(s0);
RWTexture2D<float4> OutputTexture : register(u0);

// Four bilinear samples at half-texel diagonal offsets exactly reconstruct
// the separable 3x3 Gaussian kernel [1 2 1; 2 4 2; 1 2 1] / 16.
// The fifth texture instruction loads the unfiltered center for the USM term.
[numthreads(8, 8, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 outputSize;
    OutputTexture.GetDimensions(outputSize.x, outputSize.y);
    if (any(dispatchThreadId.xy >= outputSize))
        return;

    const uint2 pixel = dispatchThreadId.xy;
    const float2 uv = (float2(pixel) + 0.5) * InvOutputSize;
    const float2 halfTexel = 0.5 * InvOutputSize;

    const float4 center = InputTexture.Load(int3(pixel, 0));
    const float3 gaussian = 0.25 * (
        InputTexture.SampleLevel(LinearClampSampler, uv + float2(-halfTexel.x, -halfTexel.y), 0).rgb +
        InputTexture.SampleLevel(LinearClampSampler, uv + float2( halfTexel.x, -halfTexel.y), 0).rgb +
        InputTexture.SampleLevel(LinearClampSampler, uv + float2(-halfTexel.x,  halfTexel.y), 0).rgb +
        InputTexture.SampleLevel(LinearClampSampler, uv + float2( halfTexel.x,  halfTexel.y), 0).rgb);

    const float3 lumaWeights = float3(0.2126, 0.7152, 0.0722);
    const float centerLuma = dot(center.rgb, lumaWeights);
    const float gaussianLuma = dot(gaussian, lumaWeights);
    float highFrequency = centerLuma - gaussianLuma;

    // Suppress tiny post-tonemap quantization/noise fluctuations and bound the
    // local high-frequency term before it can form bright or dark edge halos.
    const float localLuma = max(max(abs(centerLuma), abs(gaussianLuma)), 0.02);
    const float threshold = max(0.0005, localLuma * 0.001);
    highFrequency = sign(highFrequency) * max(abs(highFrequency) - threshold, 0.0);
    highFrequency = clamp(highFrequency, -0.5 * localLuma, 0.5 * localLuma);

    const float sharpenedLuma = centerLuma + Strength * highFrequency;
    float3 sharpened = center.rgb;
    if (centerLuma > 0.0001)
    {
        // Apply the luminance change as an RGB ratio to preserve hue. The ratio
        // guard is a second bound for dark edges and pathological LDR values.
        const float lumaScale = clamp(max(sharpenedLuma, 0.0) / centerLuma, 0.5, 1.5);
        sharpened *= lumaScale;
    }

    OutputTexture[pixel] = float4(sharpened, center.a);
}
