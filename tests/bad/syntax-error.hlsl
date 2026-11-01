//! name: syntax-error
//! description: Bevisst ødelagt filter. run-tests.ps1 (M7) sjekker at feilen peker på linje 10 i denne filen.
//! state: 0

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    Output[id.xy] = Inptu[id.xy]; // skrivefeil: Inptu
}
