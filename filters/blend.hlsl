//! name: blend
//! description: Layers frame on frame with a blend mode: 0 add, 1 screen, 2 lighten, 3 multiply
//! param: mode    uint  2   min=0 max=3
//! param: opacity float 0.1 min=0 max=1   // how much each new frame contributes

// Each frame is blended into what has been accumulated so far: result = lerp(a, Blend(a, b), opacity).
// All modes work in linear light, where "add" is physically correct: two lights on top of each other are the sum.
//   add:      a + b         Grows with the number of frames: N frames give about a + N·opacity·b. Use opacity ≈ 1/N.
//                           Values above 1.0 are allowed in state; encode clips only at the very end.
//   screen:   1-(1-a)(1-b)  Like add, but approaches 1.0 instead of going above. Also turns white with high opacity.
//   lighten:  max(a, b)     Default. Bright things give a soft glow that builds up; opacity 1 is the same as max.
//   multiply: a * b         Darkens every frame; with many frames everything that is not white goes towards black.
// Frame 0 is taken as is, so the result starts at the first image and not at black.

float3 Blend(float3 a, float3 b)
{
    switch (mode)
    {
        case 0:  return a + b;
        case 1:  return 1 - (1 - saturate(a)) * (1 - saturate(b)); // screen is only defined for 0..1
        case 2:  return max(a, b);
        default: return a * b;
    }
}

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float3 a = StateIn0[id.xy].rgb;
    float3 b = Input[id.xy].rgb;
    float3 result = FrameIndex == 0 ? b : lerp(a, Blend(a, b), opacity);

    StateOut0[id.xy] = float4(result, 1);
    Output[id.xy]    = float4(result, 1);
}
