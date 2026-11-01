//! name: grayscale
//! description: Siste frame i gråtoner (luminans), med valgfri blanding mot farge
//! state: 0
//! param: amount float 1 min=0 max=1   // 1 = helt grå, 0 = original

// Luminans må regnes av lineær RGB med vektene for primærfargene (BT.709: 0.2126, 0.7152, 0.0722).
// Grønt teller mest fordi øyet er mest følsomt for grønt. Luma() ligger i common.hlsli.

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    float3 rgb = Input[id.xy].rgb;
    Output[id.xy] = float4(lerp(rgb, Luma(rgb).xxx, amount), 1);
}
