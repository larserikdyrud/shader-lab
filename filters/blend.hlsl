//! name: blend
//! description: Legger frame på frame med en blandingsmodus: 0 add, 1 screen, 2 lighten, 3 multiply
//! param: mode    uint  2   min=0 max=3
//! param: opacity float 0.1 min=0 max=1   // hvor mye hver ny frame bidrar

// Hver frame blandes inn i det som er samlet opp så langt: result = lerp(a, Blend(a, b), opacity).
// Alle modusene regner i lineært lys, der «add» er fysisk riktig: to lys oppå hverandre er summen.
//   add:      a + b         Vokser med antall frames: N frames gir ca. a + N·opacity·b. Bruk opacity ≈ 1/N.
//                           Verdier over 1.0 er lov i state; encode klipper først til slutt.
//   screen:   1-(1-a)(1-b)  Som add, men nærmer seg 1.0 i stedet for å gå over. Blir også hvitt med høy opacity.
//   lighten:  max(a, b)     Standard. Lyse ting gir en myk glød som bygger seg opp; opacity 1 er det samme som max.
//   multiply: a * b         Mørkner for hver frame; med mange frames går alt som ikke er hvitt mot svart.
// Frame 0 tas som den er, så resultatet starter på første bilde og ikke på svart.

float3 Blend(float3 a, float3 b)
{
    switch (mode)
    {
        case 0:  return a + b;
        case 1:  return 1 - (1 - saturate(a)) * (1 - saturate(b)); // screen er bare definert for 0..1
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
