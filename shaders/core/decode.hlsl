// decode.hlsl – core pass: one decoded NV12/P010 frame → linear RGB in Frame (RGBA32F, canvas size).
// Filters never see YUV or gamma; all of that happens here.

#include "common.hlsli"

Texture2D<float>    LumaPlane   : register(t0); // Y plane (R8 for NV12, R16 for P010)
Texture2D<float2>   ChromaPlane : register(t1); // UV plane, half resolution
RWTexture2D<float4> Frame       : register(u0);

cbuffer DecodeParams : register(b0)
{
    float4 conv0;   // yMul, ySub, cMul, cSub   (computed in ColorConversion.ShaderConstants)
    float4 conv1;   // ar, ag, bg, ab
    uint4  dims;    // canvas W/H, source W/H
};

[numthreads(8, 8, 1)]
void Decode(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= dims.x || id.y >= dims.y) return;

    // Nearest neighbour from the source to the canvas (smaller sources are scaled up).
    uint2 src = uint2(float2(id.xy) * (float2(dims.zw) / float2(dims.xy)));

    float  y  = LumaPlane.Load(int3(src, 0)) * conv0.x - conv0.y;
    float2 uv = ChromaPlane.Load(int3(src >> 1, 0)) * conv0.z - conv0.w;
    float3 rgb = saturate(float3(y + conv1.x * uv.y, y + conv1.y * uv.x + conv1.z * uv.y, y + conv1.w * uv.x));

    Frame[id.xy] = float4(SrgbToLinear(rgb), 1);
}
