//! name: header-error
//! description: Deliberate error in the header. run-tests.ps1 checks that the error points at line 3.
//! param: gain flaot 1

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
}
