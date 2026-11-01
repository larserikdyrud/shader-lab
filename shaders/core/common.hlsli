// common.hlsli – hjelpefunksjoner som både kjernen og filtrene kan bruke.
// Endres sjelden. Filtrene får denne inkludert automatisk via preamblen (spesifikasjonen §5.1).

#ifndef SHADERLAB_COMMON_HLSLI
#define SHADERLAB_COMMON_HLSLI

// sRGB-transferkurven (IEC 61966-2-1). Brukes som tilnærming for SDR-video (BT.709).
// Negative verdier speiles (fortegnet beholdes) i stedet for å gi NaN fra pow; verdier over 1 følger kurven videre.
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

// Relativ luminans av lineær RGB med BT.709-primærfarger.
float Luma(float3 linearRgb) { return dot(linearRgb, float3(0.2126, 0.7152, 0.0722)); }

#endif
