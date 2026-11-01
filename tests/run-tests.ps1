# Lokal test av shader-lab (spesifikasjonen §9). Kjør før commit:  powershell -File tests/run-tests.ps1
#
#  1. Bygger (uten advarsler).
#  2. Sjekker at alle filtre i filters/ har gyldig header og kompilerer (--list-filters).
#  3. Kjører hvert filter, og noen ekstra varianter, på tests/clips/dot.mp4 med --max-frames 60 og sammenligner med
#     tests/expected/<navn>.png (maks 2/255 per kanal). Mangler referansebildet, lages det: se over og commit det.
#  4. Sjekker at feilmeldinger peker på riktig fil og linje (tests/bad/).
#
# Faktiske bilder havner i tests/out/ (gitignored), så avvik kan ses på. Exit 0 = alt OK.
# Virker i Windows PowerShell 5.1. Lagret med UTF-8 BOM, ellers leser 5.1 æøå feil.
param(
    [switch]$UpdateExpected   # skriv nye referansebilder for alle tilfeller (etter en bevisst endring)
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'bin\Debug\net9.0\shaderlab.exe'
$clip = Join-Path $repo 'tests\clips\dot.mp4'
$expectedDir = Join-Path $repo 'tests\expected'
$outDir = Join-Path $repo 'tests\out'
New-Item -ItemType Directory -Force $expectedDir, $outDir | Out-Null

$results = New-Object System.Collections.Generic.List[object]
function Add-Result($name, $status, $detail) {
    $results.Add([pscustomobject]@{ Test = $name; Status = $status; Detalj = $detail })
    $color = @{ 'OK' = 'Green'; 'NY' = 'Yellow'; 'FEIL' = 'Red' }[$status]
    Write-Host ("{0,-5} {1,-24} {2}" -f $status, $name, $detail) -ForegroundColor $color
}

# Kjører shaderlab med argumentene og gir tilbake exit-kode og samlet utdata (stdout + stderr, UTF-8).
function Invoke-ShaderLab([string[]]$arguments) {
    $stdout = [IO.Path]::GetTempFileName()
    $stderr = [IO.Path]::GetTempFileName()
    try {
        $quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
        $p = Start-Process -FilePath $exe -ArgumentList $quoted -WorkingDirectory $repo -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $text = (Get-Content $stdout -Raw -Encoding UTF8) + (Get-Content $stderr -Raw -Encoding UTF8)
        return [pscustomobject]@{ Code = $p.ExitCode; Output = "$text".Trim() }
    }
    finally {
        Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
    }
}

# 1. Bygg. --no-incremental så advarsler vises selv om ingenting er endret.
Write-Host '== Bygger'
$build = & dotnet build $repo -nologo -v q --no-incremental -warnaserror 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $build
    Add-Result 'bygg' 'FEIL' 'dotnet build feilet (advarsler regnes som feil)'
    exit 1
}
Add-Result 'bygg' 'OK' 'uten advarsler'
if (-not (Test-Path $clip)) { Add-Result 'testklipp' 'FEIL' "mangler $clip (kjør tests/make-clips.ps1)"; exit 1 }

# 2. Alle filtre kompilerer.
Write-Host '== Filtre kompilerer'
$list = Invoke-ShaderLab @('--list-filters')
if ($list.Code -eq 0) { Add-Result 'list-filters' 'OK' 'alle filtre i filters/ kompilerer' }
else { Write-Host $list.Output; Add-Result 'list-filters' 'FEIL' 'minst ett filter feiler (se over)' }

# 3. Referansebilder. Ett tilfelle per filter med standardverdier, pluss varianter som dekker parametere og encode.
$cases = [ordered]@{}
foreach ($file in Get-ChildItem (Join-Path $repo 'filters') -Filter *.hlsl | Sort-Object Name) {
    $header = Select-String -Path $file.FullName -Pattern '^\s*//!\s*name:\s*([A-Za-z0-9_-]+)' | Select-Object -First 1
    if ($header) { $cases[$header.Matches[0].Groups[1].Value] = @('-f', $header.Matches[0].Groups[1].Value) }
}
$cases['lagfun-decay-0.8'] = @('-f', 'lagfun:decay=0.8')
$cases['blend-add']        = @('-f', 'blend:mode=0,opacity=0.01')
$cases['diff-normalize']   = @('-f', 'diff:gain=1', '--normalize')
$cases['mean-exposure+1']  = @('-f', 'mean', '--exposure', '1')
$cases['chain-diff-lagfun'] = @('-f', 'diff:gain=4', '-f', 'lagfun:decay=0.95')

Write-Host "== Referansebilder ($($cases.Count) tilfeller, dot.mp4, 60 frames)"
foreach ($name in $cases.Keys) {
    $actual = Join-Path $outDir "$name.png"
    $expected = Join-Path $expectedDir "$name.png"
    $run = Invoke-ShaderLab (@($clip, '-o', $actual, '--max-frames', '60') + $cases[$name])
    if ($run.Code -ne 0) { Write-Host $run.Output; Add-Result $name 'FEIL' "kjøringen feilet (exit $($run.Code))"; continue }

    if ($UpdateExpected -or -not (Test-Path $expected)) {
        Copy-Item $actual $expected -Force
        Add-Result $name 'NY' "skrev tests/expected/$name.png - se over og commit"
        continue
    }
    $cmp = Invoke-ShaderLab @('--compare', $expected, $actual, '--tolerance', '2')
    Add-Result $name ($(if ($cmp.Code -eq 0) { 'OK' } else { 'FEIL' })) $cmp.Output
}

# 4. Feilmeldinger peker på brukerens fil og linje.
Write-Host '== Feilmeldinger'
$bad = Invoke-ShaderLab @('--list-filters', '--filters-dir', 'tests/bad')
$checks = [ordered]@{
    'feil-syntaks' = 'tests/bad/syntax-error.hlsl(10,21): error X3004'
    'feil-header'  = 'tests/bad/header-error.hlsl:3: ukjent type'
}
foreach ($name in $checks.Keys) {
    if ($bad.Code -ne 0 -and $bad.Output.Contains($checks[$name])) { Add-Result $name 'OK' $checks[$name] }
    else { Write-Host $bad.Output; Add-Result $name 'FEIL' "fant ikke '$($checks[$name])' i utdata (exit $($bad.Code))" }
}

# Sammendrag
$failed = @($results | Where-Object Status -eq 'FEIL').Count
$new = @($results | Where-Object Status -eq 'NY').Count
Write-Host ''
Write-Host ("{0} tester: {1} OK, {2} nye referansebilder, {3} feil" -f $results.Count, ($results.Count - $failed - $new), $new, $failed) `
    -ForegroundColor $(if ($failed -gt 0) { 'Red' } elseif ($new -gt 0) { 'Yellow' } else { 'Green' })
exit $(if ($failed -gt 0) { 1 } else { 0 })
