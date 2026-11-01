//! name: max
//! description: Lyseste verdi per kanal over hele klippet (lyn, stjernespor, lysmaling)

// Grunnmønsteret for alle filtre med state:
//   les forrige state (StateIn0) og nåværende frame (Input), regn ut ny state,
//   skriv den til StateOut0 (neste frame ser den som StateIn0), og skriv det som skal vises til Output.
// state: 1 og init: 0 er standard, så de trengs ikke i headeren. Lineær RGB er aldri negativ, så 0 er en trygg start.
// max gir samme resultat i lineær RGB og i sRGB, fordi transferkurven er monoton.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return; // lerretet er sjelden et multiplum av 8

    float4 m = max(StateIn0[id.xy], Input[id.xy]);
    StateOut0[id.xy] = m;
    Output[id.xy]    = m;
}
