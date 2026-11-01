// common.hlsli – helper functions that both the core and the filters can use.
// Rarely changed. Filters get this included automatically through the preamble (spec §5.1).

#ifndef SHADERLAB_COMMON_HLSLI
#define SHADERLAB_COMMON_HLSLI

// The sRGB transfer curve (IEC 61966-2-1). Used as an approximation for SDR video (BT.709).
// Negative values are mirrored (the sign is kept) instead of giving NaN from pow; values above 1 follow the curve further.
float3 SrgbToLinear(float3 c)
{
    float3 a = abs(c);
    return sign(c) * lerp(pow((a + 0.055) / 1.055, 2.4), a / 12.92, (float3)(a <= 0.04045));
}

float3 LinearToSrgb(float3 c)
{
    float3 a = abs(c);
    return sign(c) * lerp(1.055 * pow(a, 1.0 / 2.4) - 0.055, a * 12.92, (float3)(a <= 0.0031308));
}

// Relative luminance of linear RGB with BT.709 primaries.
float Luma(float3 linearRgb) { return dot(linearRgb, float3(0.2126, 0.7152, 0.0722)); }

#endif
