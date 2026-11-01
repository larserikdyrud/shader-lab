//! name: diff
//! description: Forskjellen mellom de to siste framene (bevegelse). Bakgrunnen blir svart
//! init: first
//! param: gain      float 4    min=0 max=100   // forsterkning av forskjellen
//! param: threshold float 0.01 min=0 max=1     // forskjeller under dette regnes som støy

// State holder forrige frame. init: first gjør at frame 0 sammenlignes med seg selv (svart),
// i stedet for med svart (som ville gitt hele første frame som «bevegelse»).
// Output viser bare forskjellen ved siste frame; et spor av bevegelse over tid krever i tillegg max/lagfun.
// smoothstep gir en myk terskel: under threshold → 0, over 2 × threshold → full styrke.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float3 current  = Input[id.xy].rgb;
    float3 previous = StateIn0[id.xy].rgb;
    float3 d = abs(current - previous);

    float weight = smoothstep(threshold, 2 * threshold, Luma(d)); // Luma fra common.hlsli
    StateOut0[id.xy] = float4(current, 1);
    Output[id.xy]    = float4(d * gain * weight, 1);
}
