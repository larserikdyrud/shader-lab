//! name: invert
//! description: Negativ av siste frame (det enkleste filteret: ingen state)
//! state: 0

// state: 0 betyr at filteret bare ser nåværende frame; resultatet er dermed siste frame.
// Input er lineær RGB. «1 - x» i lineært lys ser feil ut for øyet (mellomtoner blir for mørke),
// så vi inverterer i sRGB og går tilbake til lineært. Prøv å fjerne konverteringen og se forskjellen.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float3 srgb = LinearToSrgb(saturate(Input[id.xy].rgb));
    Output[id.xy] = float4(SrgbToLinear(1 - srgb), 1);
}
