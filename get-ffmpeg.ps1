# Laster ned den pinnede BtbN FFmpeg 7.1 LGPL shared-bygget og pakker ut DLL-ene shader-lab trenger til ffmpeg/.
# Versjonen må passe til FFmpeg.AutoGen 7.1.1 i ShaderLab.csproj (avcodec-61, avformat-61, avutil-59, swscale-8, swresample-5).
$ErrorActionPreference = 'Stop'

$url    = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-07-31-14-10/ffmpeg-n7.1.5-12-g1fdbca85aa-win64-lgpl-shared-7.1.zip'
$sha256 = '0f376f96fb38554ccefb1b2ae9c7c6a7b351f0e60a372b38262c320e8392c5d0'
$dlls   = 'avcodec-61', 'avformat-61', 'avutil-59', 'swscale-8', 'swresample-5'

$dest = Join-Path $PSScriptRoot 'ffmpeg'
$zip  = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetFileName($url))

if (-not (Test-Path $zip)) {
    Write-Host "Laster ned $url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $zip
}

$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sha256) {
    Remove-Item $zip -Force
    throw "SHA-256 stemmer ikke for $([IO.Path]::GetFileName($url)): forventet $sha256, fikk $actual"
}

New-Item -ItemType Directory -Force $dest | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($name in $dlls) {
        $entry = $archive.Entries | Where-Object { $_.FullName -like "*/bin/$name.dll" } | Select-Object -First 1
        if (-not $entry) { throw "$name.dll finnes ikke i arkivet" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dest "$name.dll"), $true)
    }
}
finally {
    $archive.Dispose()
}

Remove-Item $zip -Force
Write-Host "FFmpeg-DLL-er skrevet til $dest"
