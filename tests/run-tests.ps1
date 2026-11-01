# Local test of shader-lab (spec §9). Run before committing:  powershell -File tests/run-tests.ps1
#
#  1. Builds (without warnings).
#  2. Checks that every filter in filters/ has a valid header and compiles (--list-filters).
#  3. Runs every filter, plus a few extra variants, on tests/clips/dot.mp4 with --max-frames 60 and compares with
#     tests/expected/<name>.png (max 2/255 per channel). If the reference image is missing it is created: look at it and commit it.
#  4. Checks that error messages point at the right file and line (tests/bad/).
#
# Actual images end up in tests/out/ (gitignored), so differences can be inspected. Exit 0 = all OK.
# Works in Windows PowerShell 5.1. Saved with a UTF-8 BOM, otherwise 5.1 misreads non-ASCII characters.
param(
    [switch]$UpdateExpected   # write new reference images for every case (after a deliberate change)
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
    $results.Add([pscustomobject]@{ Test = $name; Status = $status; Detail = $detail })
    $color = @{ 'OK' = 'Green'; 'NEW' = 'Yellow'; 'FAIL' = 'Red' }[$status]
    Write-Host ("{0,-5} {1,-24} {2}" -f $status, $name, $detail) -ForegroundColor $color
}

# Runs shaderlab with the arguments and returns the exit code and the combined output (stdout + stderr, UTF-8).
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

# 1. Build. --no-incremental so warnings show even when nothing has changed.
Write-Host '== Building'
$build = & dotnet build $repo -nologo -v q --no-incremental -warnaserror 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $build
    Add-Result 'build' 'FAIL' 'dotnet build failed (warnings count as errors)'
    exit 1
}
Add-Result 'build' 'OK' 'no warnings'
if (-not (Test-Path $clip)) { Add-Result 'test clip' 'FAIL' "missing $clip (run tests/make-clips.ps1)"; exit 1 }

# 2. Every filter compiles.
Write-Host '== Filters compile'
$list = Invoke-ShaderLab @('--list-filters')
if ($list.Code -eq 0) { Add-Result 'list-filters' 'OK' 'every filter in filters/ compiles' }
else { Write-Host $list.Output; Add-Result 'list-filters' 'FAIL' 'at least one filter fails (see above)' }

# 3. Reference images. One case per filter with default values, plus variants that cover parameters, encode and chains.
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

Write-Host "== Reference images ($($cases.Count) cases, dot.mp4, 60 frames)"
foreach ($name in $cases.Keys) {
    $actual = Join-Path $outDir "$name.png"
    $expected = Join-Path $expectedDir "$name.png"
    $run = Invoke-ShaderLab (@($clip, '-o', $actual, '--max-frames', '60') + $cases[$name])
    if ($run.Code -ne 0) { Write-Host $run.Output; Add-Result $name 'FAIL' "the run failed (exit $($run.Code))"; continue }

    if ($UpdateExpected -or -not (Test-Path $expected)) {
        Copy-Item $actual $expected -Force
        Add-Result $name 'NEW' "wrote tests/expected/$name.png - look at it and commit it"
        continue
    }
    $cmp = Invoke-ShaderLab @('--compare', $expected, $actual, '--tolerance', '2')
    Add-Result $name ($(if ($cmp.Code -eq 0) { 'OK' } else { 'FAIL' })) $cmp.Output
}

# 4. Error messages point at the user's file and line.
Write-Host '== Error messages'
$bad = Invoke-ShaderLab @('--list-filters', '--filters-dir', 'tests/bad')
$checks = [ordered]@{
    'error-syntax' = 'tests/bad/syntax-error.hlsl(10,21): error X3004'
    'error-header' = 'tests/bad/header-error.hlsl:3: unknown type'
}
foreach ($name in $checks.Keys) {
    if ($bad.Code -ne 0 -and $bad.Output.Contains($checks[$name])) { Add-Result $name 'OK' $checks[$name] }
    else { Write-Host $bad.Output; Add-Result $name 'FAIL' "did not find '$($checks[$name])' in the output (exit $($bad.Code))" }
}

# Summary
$failed = @($results | Where-Object Status -eq 'FAIL').Count
$new = @($results | Where-Object Status -eq 'NEW').Count
Write-Host ''
Write-Host ("{0} tests: {1} OK, {2} new reference images, {3} failed" -f $results.Count, ($results.Count - $failed - $new), $new, $failed) `
    -ForegroundColor $(if ($failed -gt 0) { 'Red' } elseif ($new -gt 0) { 'Yellow' } else { 'Green' })
exit $(if ($failed -gt 0) { 1 } else { 0 })
