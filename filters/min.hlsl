//! name: min
//! description: Mørkeste verdi per kanal over hele klippet (fjerner lyse ting som beveger seg)
//! init: +inf

// Speilbildet av max. Starten må være +inf: med 0 ville min(0, x) blitt 0 overalt, og bildet svart.
// Kjernen fyller StateIn0 med +inf før første frame; etter frame 0 er state = første frame.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 m = min(StateIn0[id.xy], Input[id.xy]);
    StateOut0[id.xy] = m;
    Output[id.xy]    = m;
}
