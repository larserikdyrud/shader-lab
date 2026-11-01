//! name: lagfun
//! description: Lyse spor som falmer: out = max(in, prev * decay), som ffmpeg sitt lagfun
//! param: decay float 0.95 min=0 max=1   // hvor mye av sporet som er igjen etter én frame

// Forskjellen fra max: gammelt lys dempes med decay for hver frame, så sporet får en hale.
// Halen er nede i 1 % etter ln(0.01) / ln(decay) frames (0.95 → ~90 frames, 0.9 → ~44).
// decay = 1 gir vanlig max; decay = 0 gir bare siste frame.
// Dempingen skjer i lineært lys, så halen falmer som lys faktisk gjør.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 v = max(Input[id.xy], StateIn0[id.xy] * decay);
    StateOut0[id.xy] = v;
    Output[id.xy]    = v;
}
