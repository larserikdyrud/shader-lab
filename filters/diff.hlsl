//! name: diff
//! description: The difference between the last two frames (motion). The background turns black
//! init: first
//! param: gain      float 4    min=0 max=100   // amplification of the difference
//! param: threshold float 0.01 min=0 max=1     // differences below this count as noise

// The state holds the previous frame. init: first makes frame 0 compare with itself (black)
// instead of with black (which would show the whole first frame as "motion").
// Output only shows the difference at the last frame; a trail of motion over time also needs max/lagfun.
// smoothstep gives a soft threshold: below threshold → 0, above 2 × threshold → full strength.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float3 current  = Input[id.xy].rgb;
    float3 previous = StateIn0[id.xy].rgb;
    float3 d = abs(current - previous);

    float weight = smoothstep(threshold, 2 * threshold, Luma(d)); // Luma from common.hlsli
    StateOut0[id.xy] = float4(current, 1);
    Output[id.xy]    = float4(d * gain * weight, 1);
}
