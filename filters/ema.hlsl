//! name: ema
//! description: Eksponentielt glidende snitt: nyere frames teller mest (bakgrunn som tilpasser seg)
//! init: first
//! param: alpha float 0.05 min=0 max=1   // vekt på ny frame; 1 = bare siste frame, 0 = bare første

// state = lerp(state, frame, alpha) = state + alpha * (frame - state).
// En frame som er k frames gammel teller (1 - alpha)^k. Halveringstid ≈ 0.69 / alpha frames (alpha 0.05 → ~14 frames).
// init: first gjør at snittet starter på første frame. Med init: 0 ville bildet startet svart og tonet inn.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float4 e = lerp(StateIn0[id.xy], Input[id.xy], alpha);
    StateOut0[id.xy] = e;
    Output[id.xy]    = e;
}
