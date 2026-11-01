//! name: ema
//! description: Exponential moving average: recent frames count most (a background that adapts)
//! init: first
//! param: alpha float 0.05 min=0 max=1   // weight of the new frame; 1 = only the last frame, 0 = only the first

// state = lerp(state, frame, alpha) = state + alpha * (frame - state).
// A frame that is k frames old counts (1 - alpha)^k. Half-life ≈ 0.69 / alpha frames (alpha 0.05 → ~14 frames).
// init: first makes the average start at the first frame. With init: 0 the image would start black and fade in.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 e = lerp(StateIn0[id.xy], Input[id.xy], alpha);
    StateOut0[id.xy] = e;
    Output[id.xy]    = e;
}
