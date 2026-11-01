// decode.hlsl – kjernepass: én dekodet NV12/P010-frame → lineær RGB i Frame (RGBA32F, lerretsstørrelse).
// Filtrene ser aldri YUV eller gamma; alt det skjer her.

#include "common.hlsli"

Texture2D<float>    LumaPlane   : register(t0); // Y-planet (R8 for NV12, R16 for P010)
Texture2D<float2>   ChromaPlane : register(t1); // UV-planet, halv oppløsning
RWTexture2D<float4> Frame       : register(u0);

cbuffer DecodeParams : register(b0)
{
    float4 conv0;   // yMul, ySub, cMul, cSub   (beregnet i ColorConversion.ShaderConstants)
    float4 conv1;   // ar, ag, bg, ab
    uint4  dims;    // lerret B/H, kilde B/H
};

[numthreads(8, 8, 1)]
void Decode(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= dims.x || id.y >= dims.y) return;

    // Nærmeste nabo fra kilden til lerretet (mindre kilder skaleres opp).
    uint2 src = uint2(float2(id.xy) * (float2(dims.zw) / float2(dims.xy)));

    float  y  = LumaPlane.Load(int3(src, 0)) * conv0.x - conv0.y;
    float2 uv = ChromaPlane.Load(int3(src >> 1, 0)) * conv0.z - conv0.w;
    float3 rgb = saturate(float3(y + conv1.x * uv.y, y + conv1.y * uv.x + conv1.z * uv.y, y + conv1.w * uv.x));

    Frame[id.xy] = float4(SrgbToLinear(rgb), 1);
}
