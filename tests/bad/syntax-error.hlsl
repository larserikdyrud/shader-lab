//! name: syntax-error
//! description: Deliberately broken filter. run-tests.ps1 checks that the error points at line 10 in this file.
//! state: 0

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;

    Output[id.xy] = Inptu[id.xy]; // typo: Inptu
}
