//! name: min
//! description: Darkest value per channel over the whole clip (removes bright things that move)
//! init: +inf

// The mirror image of max. The start must be +inf: with 0, min(0, x) would be 0 everywhere and the image black.
// The core fills StateIn0 with +inf before the first frame; after frame 0 the state is the first frame.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 m = min(StateIn0[id.xy], Input[id.xy]);
    StateOut0[id.xy] = m;
    Output[id.xy]    = m;
}
