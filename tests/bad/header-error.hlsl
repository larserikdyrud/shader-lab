//! name: header-error
//! description: Bevisst feil i headeren. run-tests.ps1 sjekker at feilen peker på linje 3.
//! param: gain flaot 1

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
}
