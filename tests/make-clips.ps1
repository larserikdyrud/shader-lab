# Recreates the test clips in tests/clips/. Needs ffmpeg with libx264 on PATH.
# Rules (see the spec §8.2): at least 256x144, even width and height, and never lossless H.264
# (-qp 0 gives the High 4:4:4 profile, which D3D11VA cannot decode). Use -crf 1 -profile:v high.
$ErrorActionPreference = 'Stop'

$clips = Join-Path $PSScriptRoot 'clips'
New-Item -ItemType Directory -Force $clips | Out-Null

$h264 = '-c:v', 'libx264', '-crf', '1', '-profile:v', 'high', '-pix_fmt', 'yuv420p'

# dot.mp4: white 8x8 dot moving over a gray background, 256x144, 30 fps, 4 s (120 frames). Committed.
# overlay evaluates x for every frame; drawbox with t does not (the dot would stand still).
& ffmpeg -v error -y -f lavfi -i 'color=c=gray:s=256x144:r=30:d=4' -f lavfi -i 'color=c=white:s=8x8:r=30' `
    -filter_complex "[0][1]overlay=x='mod(t*60,256)':y=64:shortest=1" @h264 (Join-Path $clips 'dot.mp4')
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed for dot.mp4' }

# The rest is not committed (see .gitignore); they are made locally when needed.

# black.mp4: completely black, 1 s. Every filter should give black (or whatever the filter logically does with 0).
& ffmpeg -v error -y -f lavfi -i 'color=c=black:s=256x144:r=30:d=1' @h264 (Join-Path $clips 'black.mp4')
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed for black.mp4' }

# color.mp4: testsrc2 (colours, motion, text) as 10-bit HEVC, 2 s. Tests the P010 path and gives something to look at.
# 10-bit HEVC can be lossless (the profile stays Main 10), but lossy is fine here.
& ffmpeg -v error -y -f lavfi -i 'testsrc2=s=512x288:r=30:d=2' -c:v libx265 -crf 4 -pix_fmt yuv420p10le `
    -x265-params log-level=error (Join-Path $clips 'color.mp4')
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed for color.mp4 (needs libx265)' }

Write-Host "Test clips written to $clips"
