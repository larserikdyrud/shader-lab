//! name: lagfun
//! description: Bright trails that fade: out = max(in, prev * decay), like ffmpeg's lagfun
//! param: decay float 0.95 min=0 max=1   // how much of the trail is left after one frame

// The difference from max: old light is dimmed by decay every frame, so the trail gets a tail.
// The tail is down to 1 % after ln(0.01) / ln(decay) frames (0.95 → ~90 frames, 0.9 → ~44).
// decay = 1 gives plain max; decay = 0 gives only the last frame.
// The dimming happens in linear light, so the tail fades the way light actually does.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 v = max(Input[id.xy], StateIn0[id.xy] * decay);
    StateOut0[id.xy] = v;
    Output[id.xy]    = v;
}
