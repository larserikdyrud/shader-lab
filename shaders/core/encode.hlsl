// encode.hlsl – kjernepass: filterets Output (lineær RGBA32F) → 8-bit sRGB, pakket for tilbakelesing.
// Her, og bare her, klippes verdiene til 0..1.

#include "common.hlsli"

Texture2D<float4>        Image : register(t0);
RWStructuredBuffer<uint> Out   : register(u0); // én uint per piksel: R | G << 8 | B << 16

cbuffer EncodeParams : register(b0)
{
    uint2 Size;
    float Scale;    // 2^exposure, ganger 1/maks med --normalize; 1 = uendret
    uint  _pad;
};

[numthreads(8, 8, 1)]
void Encode(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;
    float3 linearRgb = saturate(Image[id.xy].rgb * Scale);
    uint3 q = uint3(saturate(LinearToSrgb(linearRgb)) * 255.0 + 0.5);
    Out[id.y * Size.x + id.x] = q.x | (q.y << 8) | (q.z << 16);
}
