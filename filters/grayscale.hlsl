//! name: grayscale
//! description: The last frame in grayscale (luminance), with optional blending towards colour
//! state: 0
//! param: amount float 1 min=0 max=1   // 1 = fully gray, 0 = original

// Luminance must be computed from linear RGB with the weights for the primaries (BT.709: 0.2126, 0.7152, 0.0722).
// Green counts most because the eye is most sensitive to green. Luma() lives in common.hlsli.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float3 rgb = Input[id.xy].rgb;
    Output[id.xy] = float4(lerp(rgb, Luma(rgb).xxx, amount), 1);
}
