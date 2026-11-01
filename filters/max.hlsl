//! name: max
//! description: Brightest value per channel over the whole clip (lightning, star trails, light painting)

// The basic pattern for every filter with state:
//   read the previous state (StateIn0) and the current frame (Input), compute the new state,
//   write it to StateOut0 (the next frame sees it as StateIn0), and write what should be shown to Output.
// state: 1 and init: 0 are the defaults, so they are not needed in the header. Linear RGB is never negative, so 0 is a safe start.
// max gives the same result in linear RGB and in sRGB, because the transfer curve is monotonic.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return; // the canvas is rarely a multiple of 8

    float4 m = max(StateIn0[id.xy], Input[id.xy]);
    StateOut0[id.xy] = m;
    Output[id.xy]    = m;
}
