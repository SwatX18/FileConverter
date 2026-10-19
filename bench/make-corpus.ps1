<#
.SYNOPSIS
    Generates (and partly downloads) the benchmark corpus into bench/samples/.

.DESCRIPTION
    Videos, audio, JPEG/PNG/WebP/TIFF images are synthesized with ffmpeg (bundled Middleware/ffmpeg by default).
    HEIC and camera RAW files cannot be synthesized and are downloaded from public sample sets:
      - RAW:  https://raw.pixls.us (CC0)
      - HEIC: https://github.com/nokiatech/heif (gh-pages/content), used locally for benchmarking only.
    Existing files are kept, so the script can be re-run to complete a partial corpus. Use -Force to regenerate.
    The samples are gitignored (several GB). Generation takes a few minutes on a 16-core machine.

.PARAMETER JpegCount
    Number of 24 MP JPEGs to generate (PLAN.md: 200).
#>
[CmdletBinding()]
param(
    [string]$FFmpeg = '',
    [string]$OutDir = '',
    [int]$JpegCount = 200,
    [switch]$SkipDownloads,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is not available in param defaults under Windows PowerShell 5.1 -File.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $FFmpeg) { $FFmpeg = Join-Path $here '..\Middleware\ffmpeg\ffmpeg.exe' }
if (-not $OutDir) { $OutDir = Join-Path $here 'samples' }
$FFmpeg = (Resolve-Path $FFmpeg).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function Invoke-FFmpeg([string]$Target, [string[]]$Arguments) {
    if ((Test-Path $Target) -and -not $Force) { Write-Host "  keep   $Target"; return }
    Write-Host "  create $Target"
    & $FFmpeg -hide_banner -loglevel error -y @Arguments
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed ($LASTEXITCODE) for $Target" }
}

function Get-Sample([string]$Url, [string]$Target) {
    if ((Test-Path $Target) -and -not $Force) { Write-Host "  keep   $Target"; return }
    Write-Host "  fetch  $Url"
    $tmp = "$Target.part"
    Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing
    Move-Item -Force $tmp $Target
}

# Synthetic sources: moving test pattern + temporal noise, so encoders do realistic work
# (a clean testsrc2 compresses unrealistically well).
function VideoSrc([string]$Size, [int]$Seconds, [int]$Fps = 30) {
    "testsrc2=s=${Size}:r=${Fps}:d=${Seconds},noise=alls=10:allf=t+u"
}
function AudioSrc([int]$Seconds, [int]$Rate = 48000) {
    # Pink noise bed + two tones: not trivially compressible for FLAC/AAC.
    "anoisesrc=d=${Seconds}:c=pink:r=${Rate}:a=0.08[n];sine=f=440:d=${Seconds}:r=${Rate}[s1];sine=f=1250:d=${Seconds}:r=${Rate}[s2];[n][s1][s2]amix=inputs=3:normalize=0,pan=stereo|c0=c0|c1=c0"
}

Write-Host "ffmpeg: $FFmpeg"
Write-Host "output: $OutDir"

# ---------------------------------------------------------------- video
$video = Join-Path $OutDir 'video'
New-Item -ItemType Directory -Force $video | Out-Null
Write-Host 'Video'

Invoke-FFmpeg "$video\h264_1080p.mp4" @(
    '-f', 'lavfi', '-i', (VideoSrc '1920x1080' 60),
    '-f', 'lavfi', '-i', (AudioSrc 60),
    '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv420p',
    '-c:a', 'aac', '-b:a', '160k', '-movflags', '+faststart', '-shortest', "$video\h264_1080p.mp4")

Invoke-FFmpeg "$video\h264_4k.mp4" @(
    '-f', 'lavfi', '-i', (VideoSrc '3840x2160' 30),
    '-f', 'lavfi', '-i', (AudioSrc 30),
    '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv420p',
    '-c:a', 'aac', '-b:a', '160k', '-movflags', '+faststart', '-shortest', "$video\h264_4k.mp4")

Invoke-FFmpeg "$video\hevc_1080p.mkv" @(
    '-f', 'lavfi', '-i', (VideoSrc '1920x1080' 60),
    '-f', 'lavfi', '-i', (AudioSrc 60),
    '-c:v', 'libx265', '-preset', 'fast', '-crf', '24', '-pix_fmt', 'yuv420p', '-x265-params', 'log-level=error',
    '-c:a', 'ac3', '-b:a', '384k', '-shortest', "$video\hevc_1080p.mkv")

# MKV with H.264 + FLAC + two SRT subtitle tracks (partial-remux case: video copy, audio re-encode).
$srtEn = Join-Path $video 'subs_en.srt'
$srtFr = Join-Path $video 'subs_fr.srt'
if (-not (Test-Path $srtEn) -or $Force) {
    $en = New-Object System.Text.StringBuilder; $fr = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt 30; $i++) {
        $start = [TimeSpan]::FromSeconds($i * 2); $end = [TimeSpan]::FromSeconds($i * 2 + 1.5)
        $ts = '{0:hh\:mm\:ss\,fff} --> {1:hh\:mm\:ss\,fff}' -f $start, $end
        [void]$en.AppendLine(($i + 1)).AppendLine($ts).AppendLine("Subtitle line $($i + 1)").AppendLine()
        [void]$fr.AppendLine(($i + 1)).AppendLine($ts).AppendLine("Sous-titre $($i + 1)").AppendLine()
    }
    [IO.File]::WriteAllText($srtEn, $en.ToString()); [IO.File]::WriteAllText($srtFr, $fr.ToString())
}
Invoke-FFmpeg "$video\h264_flac_subs.mkv" @(
    '-f', 'lavfi', '-i', (VideoSrc '1920x1080' 60),
    '-f', 'lavfi', '-i', (AudioSrc 60),
    '-i', $srtEn, '-i', $srtFr,
    '-map', '0:v', '-map', '1:a', '-map', '2:s', '-map', '3:s',
    '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv420p',
    '-c:a', 'flac', '-c:s', 'srt',
    '-metadata:s:s:0', 'language=eng', '-metadata:s:s:1', 'language=fre', '-shortest', "$video\h264_flac_subs.mkv")

# iPhone-style MOV: HEVC Main10 4K30, hvc1 tag, AAC, QuickTime metadata.
Invoke-FFmpeg "$video\iphone_hevc10.mov" @(
    '-f', 'lavfi', '-i', (VideoSrc '3840x2160' 20),
    '-f', 'lavfi', '-i', (AudioSrc 20 44100),
    '-c:v', 'libx265', '-preset', 'fast', '-crf', '24', '-pix_fmt', 'yuv420p10le', '-tag:v', 'hvc1', '-x265-params', 'log-level=error',
    '-c:a', 'aac', '-b:a', '128k', '-ac', '1',
    '-metadata', 'creation_time=2026-06-01T12:00:00Z', '-metadata', 'com.apple.quicktime.make=Apple',
    '-metadata', 'com.apple.quicktime.model=iPhone 15 Pro', '-movflags', '+use_metadata_tags', '-shortest', "$video\iphone_hevc10.mov")

# 2 s clip: benchmark warm-up and quick fallback checks (try-case.ps1).
Invoke-FFmpeg "$video\tiny.mp4" @(
    '-f', 'lavfi', '-i', 'testsrc2=s=1280x720:r=30:d=2', '-f', 'lavfi', '-i', 'sine=d=2',
    '-c:v', 'libx264', '-preset', 'fast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', "$video\tiny.mp4")

# 4:2:2 / 10-bit sources (PLAN 1.3): ProRes 422 (no NVDEC support -> exercises the decode fallback)
# and H.264 High 4:2:2 10-bit.
Invoke-FFmpeg "$video\prores422_1080p.mov" @(
    '-f', 'lavfi', '-i', (VideoSrc '1920x1080' 20 25),
    '-f', 'lavfi', '-i', (AudioSrc 20),
    '-c:v', 'prores_ks', '-profile:v', '2', '-pix_fmt', 'yuv422p10le', '-c:a', 'pcm_s16le', '-shortest', "$video\prores422_1080p.mov")
Invoke-FFmpeg "$video\h264_422_10bit.mkv" @(
    '-f', 'lavfi', '-i', (VideoSrc '1920x1080' 30),
    '-f', 'lavfi', '-i', (AudioSrc 30),
    '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv422p10le', '-c:a', 'aac', '-b:a', '160k', '-shortest', "$video\h264_422_10bit.mkv")

# ---------------------------------------------------------------- audio
$audio = Join-Path $OutDir 'audio'
New-Item -ItemType Directory -Force $audio | Out-Null
Write-Host 'Audio'
Invoke-FFmpeg "$audio\album_track_24bit.flac" @('-f', 'lavfi', '-i', (AudioSrc 600 48000), '-c:a', 'flac', '-sample_fmt', 's32', '-bits_per_raw_sample', '24', "$audio\album_track_24bit.flac")
Invoke-FFmpeg "$audio\recording_16bit.wav" @('-f', 'lavfi', '-i', (AudioSrc 600 44100), '-c:a', 'pcm_s16le', "$audio\recording_16bit.wav")
Invoke-FFmpeg "$audio\tiny.wav" @('-f', 'lavfi', '-i', 'sine=f=440:d=1', '-c:a', 'pcm_s16le', "$audio\tiny.wav")

# ---------------------------------------------------------------- images
Write-Host 'Images'
$jpg = Join-Path $OutDir 'jpg_24mp'
New-Item -ItemType Directory -Force $jpg | Out-Null
$existing = @(Get-ChildItem $jpg -Filter '*.jpg' -ErrorAction SilentlyContinue).Count
if ($existing -lt $JpegCount -or $Force) {
    Write-Host "  create $jpg\img_###.jpg x $JpegCount (6000x4000)"
    # Animated gradients => every frame differs; 4:2:0 + temporal noise approximates a camera JPEG (~8-9 MB per file).
    & $FFmpeg -hide_banner -loglevel error -y -f lavfi -i "gradients=s=6000x4000:r=1:n=5:speed=0.15:seed=7,format=yuvj420p,noise=alls=16:allf=t" `
        -frames:v $JpegCount -q:v 3 -threads 0 "$jpg\img_%03d.jpg"
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed for JPEG corpus' }
} else { Write-Host "  keep   $jpg ($existing files)" }

$png = Join-Path $OutDir 'png'; $webp = Join-Path $OutDir 'webp'; $tiff = Join-Path $OutDir 'tiff'
New-Item -ItemType Directory -Force $png, $webp, $tiff | Out-Null
for ($i = 1; $i -le 10; $i++) {
    $src = "testsrc2=s=4000x3000:r=1:d=1,noise=alls=$(4 + $i):allf=u,hue=h=$($i * 36)"
    Invoke-FFmpeg ('{0}\image_{1:D2}.png' -f $png, $i) @('-f', 'lavfi', '-i', $src, '-frames:v', '1', ('{0}\image_{1:D2}.png' -f $png, $i))
    $src24 = "gradients=s=6000x4000:n=4:seed=$i,noise=alls=10:allf=u"
    Invoke-FFmpeg ('{0}\image_{1:D2}.webp' -f $webp, $i) @('-f', 'lavfi', '-i', $src24, '-frames:v', '1', '-c:v', 'libwebp', '-quality', '85', ('{0}\image_{1:D2}.webp' -f $webp, $i))
    if ($i -le 5) {
        Invoke-FFmpeg ('{0}\scan_{1:D2}.tif' -f $tiff, $i) @('-f', 'lavfi', '-i', $src24, '-frames:v', '1', '-pix_fmt', 'rgb24', '-compression_algo', 'lzw', ('{0}\scan_{1:D2}.tif' -f $tiff, $i))
    }
}

# PDF (rendered by Ghostscript through ImageMagick): 3 A4 pages of vector text and shapes, written by hand.
$pdfDir = Join-Path $OutDir 'pdf'
New-Item -ItemType Directory -Force $pdfDir | Out-Null
$pdfPath = Join-Path $pdfDir 'vector_3pages.pdf'
if (-not (Test-Path $pdfPath) -or $Force) {
    $pageCount = 3
    $objects = New-Object System.Collections.Generic.List[string]
    $objects.Add('<< /Type /Catalog /Pages 2 0 R >>')
    $kids = (0..($pageCount - 1) | ForEach-Object { '{0} 0 R' -f (4 + 2 * $_) }) -join ' '
    $objects.Add("<< /Type /Pages /Kids [$kids] /Count $pageCount >>")
    $objects.Add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
    for ($p = 1; $p -le $pageCount; $p++) {
        $content = "BT /F1 36 Tf 72 760 Td (File Converter test page $p of $pageCount) Tj ET`n" +
            "0.9 0.2 0.2 rg 72 400 200 200 re f`n0.2 0.4 0.9 rg 320 400 200 200 re f`n" +
            "0 0 0 RG 4 w 72 300 m 523 350 l S`nBT /F1 14 Tf 72 250 Td (The quick brown fox jumps over the lazy dog.) Tj ET"
        $objects.Add('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {0} 0 R >>' -f (5 + 2 * ($p - 1)))
        $objects.Add("<< /Length $($content.Length) >>`nstream`n$content`nendstream")
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("%PDF-1.4`n")
    $offsets = @()
    for ($i = 0; $i -lt $objects.Count; $i++) {
        $offsets += $sb.Length
        [void]$sb.Append("$($i + 1) 0 obj`n$($objects[$i])`nendobj`n")
    }
    $xref = $sb.Length
    [void]$sb.Append("xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n")
    foreach ($o in $offsets) { [void]$sb.Append(('{0:D10} 00000 n ' -f $o) + "`n") }
    [void]$sb.Append("trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [IO.File]::WriteAllText($pdfPath, $sb.ToString(), [Text.Encoding]::ASCII)
    Write-Host "  write  $pdfPath"
}

if (-not $SkipDownloads) {
    $ProgressPreference = 'SilentlyContinue'
    $heic = Join-Path $OutDir 'heic'; $raw = Join-Path $OutDir 'raw'
    New-Item -ItemType Directory -Force $heic, $raw | Out-Null
    Write-Host 'HEIC (nokiatech/heif samples)'
    $heifBase = 'https://raw.githubusercontent.com/nokiatech/heif/gh-pages/content'
    foreach ($f in 'cc0/3A_HEIC_HDR.heic', 'cc0/3B_HEIC_SDR.heic', 'nokia/meadow.heic', 'imagecompression/big_tree.heic') {
        Get-Sample "$heifBase/$f" (Join-Path $heic (Split-Path $f -Leaf))
    }
    Write-Host 'RAW (raw.pixls.us, CC0)'
    $rawBase = 'https://raw.pixls.us/data'
    $raws = @{
        'sony_a7iii.ARW'   = 'Sony/ILCE-7M3/_DSC0009.ARW'
        'canon_5d3.CR2'    = 'Canon/EOS%205D%20Mark%20III/5G4A9394.CR2'
        'nikon_d750.NEF'   = 'Nikon/D750/compressed_14_bit.NEF'
        'fuji_xt3.RAF'     = 'Fujifilm/X-T3/AFXT2720.RAF'
        'iphone12pro.DNG'  = 'Apple/iPhone%2012%20Pro/IMG_1361.DNG'
    }
    foreach ($name in $raws.Keys) { Get-Sample "$rawBase/$($raws[$name])" (Join-Path $raw $name) }

    # Real-world long video: Sintel (Blender Foundation, CC-BY 3.0), 14:48 1080p H.264 + AC3 + 10 SRT subtitle tracks, 1.1 GB.
    # Synthetic clips are fine for timing, but encoder quality/size calibration needs real content.
    Write-Host 'Real video (Sintel, CC-BY, 1.1 GB)'
    Get-Sample 'https://download.blender.org/demo/movies/Sintel.2010.1080p.mkv' (Join-Path $video 'sintel_1080p.mkv')
}

$total = (Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ('Corpus ready: {0:N0} files, {1:N2} GB' -f @(Get-ChildItem $OutDir -Recurse -File).Count, ($total / 1GB))
