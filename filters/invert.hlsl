//! name: invert
//! description: Negative of the last frame (the simplest filter: no state)
//! state: 0

// state: 0 means the filter only sees the current frame; the result is therefore the last frame.
// Input is linear RGB. "1 - x" in linear light looks wrong to the eye (midtones become too dark),
// so we invert in sRGB and go back to linear. Try removing the conversion and see the difference.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float3 srgb = LinearToSrgb(saturate(Input[id.xy].rgb));
    Output[id.xy] = float4(SrgbToLinear(1 - srgb), 1);
}
