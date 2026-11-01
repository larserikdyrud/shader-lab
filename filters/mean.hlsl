//! name: mean
//! description: Gjennomsnitt per piksel over hele klippet (fjerner støy og alt som beveger seg)

// State er summen av alle frames. RGBA32F holder summen presist langt forbi det et klipp trenger
// (24 bit mantisse: 1/255-presisjon helt opp til ~65 000 frames med hvitt).
// Snittet regnes i lineært lys, så det blir fysisk riktig: som én lang eksponering med ND-filter.
//
// Finalize kjøres én gang etter siste frame. Der er FrameIndex = antall behandlede frames.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 sum = StateIn0[id.xy] + Input[id.xy];
    StateOut0[id.xy] = sum;
    Output[id.xy]    = sum / (FrameIndex + 1); // snitt så langt, så Output alltid er et gyldig bilde
}

[numthreads(8, 8, 1)]
void Finalize(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 sum = StateIn0[id.xy];
    Output[id.xy]    = sum / max(FrameIndex, 1);
    StateOut0[id.xy] = sum; // state bevares; ikke nødvendig her, men god vane
}
