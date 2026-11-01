//! name: mean
//! description: Average per pixel over the whole clip (removes noise and everything that moves)

// The state is the sum of all frames. RGBA32F keeps the sum precise far beyond what a clip needs
// (24-bit mantissa: 1/255 precision up to about 65 000 frames of white).
// The average is computed in linear light, so it is physically correct: like one long exposure with an ND filter.
//
// Finalize runs once after the last frame. There, FrameIndex = the number of processed frames.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 sum = StateIn0[id.xy] + Input[id.xy];
    StateOut0[id.xy] = sum;
    Output[id.xy]    = sum / (FrameIndex + 1); // average so far, so Output is always a valid image
}

[numthreads(8, 8, 1)]
void Finalize(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 sum = StateIn0[id.xy];
    Output[id.xy]    = sum / max(FrameIndex, 1);
    StateOut0[id.xy] = sum; // keep the state; not needed here, but a good habit
}
