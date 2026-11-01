# Lager testklippene i tests/clips/ på nytt. Krever ffmpeg med libx264 på PATH.
# Regler (se spesifikasjonen §8.2): minst 256x144, partall i begge dimensjoner, og aldri lossless H.264
# (-qp 0 gir profilen High 4:4:4, som D3D11VA ikke kan dekode). Bruk -crf 1 -profile:v high.
$ErrorActionPreference = 'Stop'

$clips = Join-Path $PSScriptRoot 'clips'
New-Item -ItemType Directory -Force $clips | Out-Null

$h264 = '-c:v', 'libx264', '-crf', '1', '-profile:v', 'high', '-pix_fmt', 'yuv420p'

# dot.mp4: hvit 8x8-prikk som beveger seg over grå bakgrunn, 256x144, 30 fps, 4 s (120 frames). Committes.
# overlay regner ut x for hver frame; drawbox med t gjør det ikke (prikken står stille).
& ffmpeg -v error -y -f lavfi -i 'color=c=gray:s=256x144:r=30:d=4' -f lavfi -i 'color=c=white:s=8x8:r=30' `
    -filter_complex "[0][1]overlay=x='mod(t*60,256)':y=64:shortest=1" @h264 (Join-Path $clips 'dot.mp4')
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg feilet for dot.mp4' }

# Resten committes ikke (se .gitignore); de lages lokalt ved behov.

# black.mp4: helt svart, 1 s. Alle filtre skal gi svart (eller det filteret logisk gjør med 0).
& ffmpeg -v error -y -f lavfi -i 'color=c=black:s=256x144:r=30:d=1' @h264 (Join-Path $clips 'black.mp4')
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg feilet for black.mp4' }

# color.mp4: testsrc2 (farger, bevegelse, tekst) som HEVC 10-bit, 2 s. Tester P010-stien og gir noe å se på.
# HEVC 10-bit kan være lossless (profilen forblir Main 10), men lossy holder her.
& ffmpeg -v error -y -f lavfi -i 'testsrc2=s=512x288:r=30:d=2' -c:v libx265 -crf 4 -pix_fmt yuv420p10le `
    -x265-params log-level=error (Join-Path $clips 'color.mp4')
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg feilet for color.mp4 (krever libx265)' }

Write-Host "Testklipp skrevet til $clips"
