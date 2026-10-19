<#
.SYNOPSIS
    Integration tests: converts the benchmark corpus with the built FileConverter.exe and checks every output.

.DESCRIPTION
    Each case runs FileConverter.exe the way the shell extension does (--conversion-preset / --input-files), with
    optional setting overrides, then checks:
      - the application exited by itself (it only does when every conversion succeeded);
      - each input has a "Job done" log line, with the expected conversion path (remux, nvenc..., vips...);
      - each output is readable: ffprobe reports no error, and a full decode (ffmpeg -f null) reports no error;
      - the output streams (type:codec, in order), the dimensions of the first video stream, and the duration
        (within 0.5 s of the source, or of an explicit value).

    Environment: like bench/run-bench.ps1, HKCU\Software\FileConverter\Path and
    %LOCALAPPDATA%\FileConverter\Settings.user.xml are set for the run and removed afterwards; the script refuses
    to run if FileConverter is installed or has user settings. Inputs are hard-linked into bench/work/integration.
    The corpus comes from bench/make-corpus.ps1.

.EXAMPLE
    .\Tests\integration.ps1
    .\Tests\integration.ps1 -Case video_*,img_heic*
    .\Tests\integration.ps1 -NoGpu        # machine without hardware encoder: GPU cases expect the CPU path
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string[]]$Case = @('*'),
    [string]$Output = '',
    [int]$TimeoutSeconds = 600,
    # Outputs larger than this are only probed, not fully decoded.
    [long]$MaximumDecodeBytes = 300MB,
    [switch]$NoGpu,
    [switch]$KeepOutputs
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = (Resolve-Path (Join-Path $here '..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'Application\FileConverter\bin\x64\Release\FileConverter.exe' }
if (-not $Output) { $Output = Join-Path $here 'integration.csv' }
$Exe = (Resolve-Path $Exe).Path
$samples = Join-Path $root 'bench\samples'
$workRoot = Join-Path $root 'bench\work\integration'
$ffprobe = (Get-Command ffprobe -ErrorAction SilentlyContinue).Source
if (-not $ffprobe) { $ffprobe = Join-Path (Split-Path $Exe) 'ffprobe.exe' }
$ffmpeg = Join-Path (Split-Path $Exe) 'ffmpeg.exe'
if (-not (Test-Path $ffprobe)) { throw 'ffprobe not found: put it on PATH.' }
if (-not (Test-Path (Join-Path $samples 'video'))) { throw "Corpus not found ($samples): run bench\make-corpus.ps1." }

# ------------------------------------------------------------------ cases
# Path: expected conversion path of every job ('hw' = any hardware encoder path, '*' = not checked).
# Streams: expected output streams 'type:codec' in order, joined by '+' ('*' = not checked).
# Size: 'WxH' of the first video stream, or '' (not checked). Duration: 'source', seconds, or '' (not checked).
# Outputs: number of output files (default: one per input).
# Known: a known issue; its problems are reported but the case counts as 'known', not as a failure.
$cases = @(
    # Video: stream copy
    @{ Name = 'video_remux_hevc_mkv_to_mp4'; Preset = 'To Mp4'; Inputs = 'video\hevc_1080p.mkv'; Path = 'remux'; Streams = 'video:hevc+audio:ac3'; Size = '1920x1080'; Duration = 'source' }
    @{ Name = 'video_remux_flac_subs_to_mp4'; Preset = 'To Mp4'; Inputs = 'video\h264_flac_subs.mkv'; Path = 'remux'; Streams = 'video:h264+audio:aac+subtitle:mov_text+subtitle:mov_text'; Size = '1920x1080'; Duration = 'source' }
    @{ Name = 'video_remux_mp4_to_mkv'; Preset = 'To Mkv'; Inputs = 'video\h264_1080p.mp4'; Path = 'remux'; Streams = 'video:h264+audio:aac'; Size = '1920x1080'; Duration = 'source' }
    @{ Name = 'video_remux_sintel_to_mp4'; Preset = 'To Mp4'; Inputs = 'video\sintel_1080p.mkv'; Path = 'remux'; Streams = '*'; Size = '1920x818'; Duration = 'source' }
    @{ Name = 'video_remux_iphone_hevc10_to_mp4'; Preset = 'To Mp4'; Inputs = 'video\iphone_hevc10.mov'; Path = 'remux'; Streams = 'video:hevc+audio:aac'; Size = '3840x2160'; Duration = 'source' }
    @{ Name = 'video_422_10bit_reencoded'; Preset = 'To Mp4'; Inputs = 'video\h264_422_10bit.mkv'; Path = 'hw'; Streams = 'video:h264+audio:aac'; Size = '1920x1080'; Duration = 'source' }
    # Video: hardware encode, fallbacks, CPU
    @{ Name = 'video_gpu_prores_to_mp4'; Preset = 'To Mp4'; Inputs = 'video\prores422_1080p.mov'; Path = 'hw'; Streams = 'video:h264+audio:aac'; Size = '1920x1080'; Duration = 'source' }
    @{ Name = 'video_gpu_4k_to_mkv'; Preset = 'To Mkv'; Inputs = 'video\h264_4k.mp4'; Settings = 'CopyStreamsWhenPossible=false'; Path = 'hw'; Streams = 'video:h264+audio:aac'; Size = '3840x2160'; Duration = 'source' }
    @{ Name = 'video_gpu_scale720'; Preset = 'Scale 720p/To Mp4'; Inputs = 'video\h264_1080p.mp4'; Path = 'hw'; Streams = 'video:h264+audio:aac'; Size = '1280x720'; Duration = 'source' }
    @{ Name = 'video_gpu_rotate_left'; Preset = 'Rotate left/To Mp4'; Inputs = 'video\tiny.mp4'; Path = 'hw'; Streams = 'video:h264+audio:aac'; Size = '720x1280'; Duration = 'source' }
    @{ Name = 'video_gpu_iphone_hevc10_to_mp4'; Preset = 'To Mp4'; Inputs = 'video\iphone_hevc10.mov'; Settings = 'CopyStreamsWhenPossible=false'; Path = 'hw'; Streams = 'video:h264+audio:aac'; Size = '3840x2160'; Duration = 'source' }
    @{ Name = 'video_amf_422_fallback'; Preset = 'To Mp4'; Inputs = 'video\h264_422_10bit.mkv'; Settings = 'HardwareAccelerationMode=AMF;CopyStreamsWhenPossible=false'; Path = 'amf-cpu-decode|cpu-fallback'; Streams = 'video:h264+audio:aac'; Size = '1920x1080'; Duration = 'source' }
    @{ Name = 'video_cpu_off'; Preset = 'To Mp4'; Inputs = 'video\tiny.mp4'; Settings = 'HardwareAccelerationMode=Off;CopyStreamsWhenPossible=false'; Path = 'cpu'; Streams = 'video:h264+audio:aac'; Size = '1280x720'; Duration = 'source' }
    @{ Name = 'video_to_webm'; Preset = 'To Webm'; Inputs = 'video\tiny.mp4'; Path = '*'; Streams = 'video:vp9+audio:vorbis'; Size = '1280x720'; Duration = 'source' }
    @{ Name = 'video_to_ogv'; Preset = 'To Ogv'; Inputs = 'video\tiny.mp4'; Path = '*'; Streams = 'video:theora+audio:vorbis'; Size = '1280x720'; Duration = 'source'; Known = 'the Theora output does not decode (error in unpack_block_qpis): ffmpeg libtheora on 64-bit, upstream issue (FFmpeg trac #8040, #8775)' }
    @{ Name = 'video_to_avi'; Preset = 'To Avi'; Inputs = 'video\tiny.mp4'; Path = '*'; Streams = 'video:mpeg4+audio:mp3'; Size = '1280x720'; Duration = 'source' }
    @{ Name = 'video_to_gif'; Preset = 'To Gif'; Inputs = 'video\tiny.mp4'; Path = 'gif'; Streams = 'video:gif'; Size = ''; Duration = '' }
    @{ Name = 'video_batch_mixed'; Preset = 'To Mp4'; Inputs = 'video\hevc_1080p.mkv', 'video\prores422_1080p.mov', 'video\h264_flac_subs.mkv', 'video\tiny.mp4'; Path = '*'; Streams = '*'; Size = ''; Duration = 'source' }
    # Audio
    @{ Name = 'audio_extract_mp3'; Preset = 'To Mp3'; Inputs = 'video\h264_1080p.mp4'; Path = 'ffmpeg'; Streams = 'audio:mp3'; Size = ''; Duration = 'source' }
    @{ Name = 'audio_flac_to_mp3'; Preset = 'To Mp3'; Inputs = 'audio\album_track_24bit.flac'; Path = 'ffmpeg'; Streams = 'audio:mp3'; Size = ''; Duration = 'source' }
    @{ Name = 'audio_wav_to_flac'; Preset = 'To Flac'; Inputs = 'audio\recording_16bit.wav'; Path = 'ffmpeg'; Streams = 'audio:flac'; Size = ''; Duration = 'source' }
    @{ Name = 'audio_flac_to_aac'; Preset = 'To Aac'; Inputs = 'audio\album_track_24bit.flac'; Path = 'ffmpeg'; Streams = 'audio:aac'; Size = ''; Duration = 'source' }
    @{ Name = 'audio_flac_to_ogg'; Preset = 'To Ogg'; Inputs = 'audio\album_track_24bit.flac'; Path = 'ffmpeg'; Streams = 'audio:vorbis'; Size = ''; Duration = 'source' }
    @{ Name = 'audio_flac_to_wav'; Preset = 'To Wav'; Inputs = 'audio\album_track_24bit.flac'; Path = 'ffmpeg'; Streams = 'audio:pcm_s16le'; Size = ''; Duration = 'source' }
    @{ Name = 'audio_remux_mov_to_aac'; Preset = 'To Aac'; Inputs = 'video\iphone_hevc10.mov'; Path = 'remux'; Streams = 'audio:aac'; Size = ''; Duration = 'source' }
    @{ Name = 'audio_tempo_faster'; Preset = 'Tempo - Pitch/To Mp3 (25% faster)'; Inputs = 'audio\recording_16bit.wav'; Path = 'ffmpeg'; Streams = 'audio:mp3'; Size = ''; Duration = '480' }
    # Images
    @{ Name = 'img_jpg_to_png'; Preset = 'To Png'; Inputs = 'jpg_24mp\img_001.jpg', 'jpg_24mp\img_002.jpg'; Path = 'vips'; Streams = 'video:png'; Size = '6000x4000'; Duration = '' }
    @{ Name = 'img_jpg_scale25_webp'; Preset = 'Scale 25%/To Webp'; Inputs = 'jpg_24mp\img_00[1-8].jpg'; Path = 'vips'; Streams = 'video:webp'; Size = '1500x1000'; Duration = '' }
    @{ Name = 'img_jpg_scale75_jpg'; Preset = 'Scale 75%/To Jpg'; Inputs = 'jpg_24mp\img_001.jpg'; Path = 'vips'; Streams = 'video:mjpeg'; Size = '4500x3000'; Duration = '' }
    @{ Name = 'img_jpg_rotate_left_png'; Preset = 'Rotate left/To Png'; Inputs = 'jpg_24mp\img_001.jpg'; Path = 'vips'; Streams = 'video:png'; Size = '4000x6000'; Duration = '' }
    @{ Name = 'img_jpg_to_avif'; Preset = 'To Avif'; Inputs = 'jpg_24mp\img_001.jpg'; Path = 'vips'; Streams = '*'; Size = ''; Duration = '' }
    @{ Name = 'img_png_to_jpg'; Preset = 'To Jpg'; Inputs = 'png\image_0[1-3].png'; Path = 'vips'; Streams = 'video:mjpeg'; Size = '4000x3000'; Duration = '' }
    @{ Name = 'img_webp_to_png'; Preset = 'To Png'; Inputs = 'webp\image_01.webp'; Path = 'vips'; Streams = 'video:png'; Size = '6000x4000'; Duration = '' }
    @{ Name = 'img_tiff_to_jpg'; Preset = 'To Jpg'; Inputs = 'tiff\scan_01.tif'; Path = 'vips'; Streams = 'video:mjpeg'; Size = '6000x4000'; Duration = '' }
    @{ Name = 'img_imagemagick_only'; Preset = 'To Png'; Inputs = 'jpg_24mp\img_001.jpg'; Settings = 'ImageEngine=ImageMagick'; Path = 'imagemagick'; Streams = 'video:png'; Size = '6000x4000'; Duration = '' }
    @{ Name = 'img_heic_to_jpg'; Preset = 'To Jpg'; Inputs = 'heic\*.heic'; Path = 'imagemagick'; Streams = 'video:mjpeg'; Size = ''; Duration = '' }
    @{ Name = 'img_raw_to_jpg'; Preset = 'To Jpg'; Inputs = 'raw\*.*'; Path = 'imagemagick'; Streams = 'video:mjpeg'; Size = ''; Duration = '' }
    @{ Name = 'img_png_to_ico'; Preset = 'To Ico'; Inputs = 'png\image_01.png'; Path = 'ico'; Streams = '*'; Size = ''; Duration = '' }
    @{ Name = 'doc_pdf_to_png_paged'; Preset = 'To Png (paged)'; Inputs = 'pdf\vector_3pages.pdf'; Outputs = 3; Path = 'imagemagick'; Streams = 'video:png'; Size = '1653x2339'; Duration = '' }
    @{ Name = 'img_jpg_to_pdf'; Preset = 'To Pdf'; Inputs = 'jpg_24mp\img_001.jpg'; Path = 'imagemagick'; Streams = 'pdf'; Size = ''; Duration = '' }
)
$Case = @($Case | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$cases = @($cases | Where-Object { $n = $_.Name; @($Case | Where-Object { $n -like $_ }).Count -gt 0 })
if ($cases.Count -eq 0) { throw "No case matches '$($Case -join ',')'." }

# ------------------------------------------------------------------ helpers
function Invoke-Native([string]$File, [string[]]$Arguments) {
    # Returns @{ Code; Out; Err } (PS 5.1 would turn stderr lines into errors under 'Stop').
    $psi = New-Object Diagnostics.ProcessStartInfo $File
    $psi.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    @{ Code = $p.ExitCode; Out = $out; Err = $errTask.Result.Trim() }
}

function Get-MediaInfo([string]$Path) {
    $r = Invoke-Native $ffprobe @('-v', 'error', '-show_entries', 'format=duration:stream=codec_type,codec_name,width,height', '-of', 'json', '--', $Path)
    $info = @{ Error = $null; Streams = ''; Size = ''; Duration = $null }
    if ($r.Code -ne 0 -or $r.Err) { $info.Error = "ffprobe: $(if ($r.Err) { ($r.Err -split "`n")[0] } else { "exit $($r.Code)" })"; return $info }
    $j = $r.Out | ConvertFrom-Json
    $streams = @($j.streams)
    $info.Streams = ($streams | ForEach-Object { "$($_.codec_type):$($_.codec_name)" }) -join '+'
    $video = $streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    if ($video -and $video.width) { $info.Size = "$($video.width)x$($video.height)" }
    if ($j.format.duration) { $info.Duration = [double]::Parse($j.format.duration, [Globalization.CultureInfo]::InvariantCulture) }
    if ([IO.Path]::GetExtension($Path) -eq '.aac') {
        # Raw ADTS: ffprobe estimates the duration from the bitrate. Count the packets instead (1024 samples each).
        $c = Invoke-Native $ffprobe @('-v', 'error', '-count_packets', '-select_streams', 'a:0', '-show_entries', 'stream=nb_read_packets,sample_rate', '-of', 'json', '--', $Path)
        $a = @(($c.Out | ConvertFrom-Json).streams)[0]
        if ($a.nb_read_packets -and $a.sample_rate) { $info.Duration = [double]$a.nb_read_packets * 1024 / [double]$a.sample_rate }
    }
    $info
}

function Test-Decode([string]$Path) {
    $r = Invoke-Native $ffmpeg @('-v', 'error', '-i', $Path, '-f', 'null', '-')
    if ($r.Code -ne 0) { return "decode: exit $($r.Code) $(($r.Err -split "`n")[0])" }
    if ($r.Err) { return "decode: $(($r.Err -split "`n")[0])" }
    $null
}

function Test-PathLabel([string]$Expected, [string]$Actual) {
    if ($Expected -eq '*') { return $true }
    if ($Expected -eq 'hw') {
        if ($NoGpu) { return $Actual -eq 'cpu' -or $Actual -eq 'cpu-fallback' }
        return $Actual -match '^(nvenc|amf|qsv)'
    }
    return @($Expected -split '\|') -contains $Actual
}

# ------------------------------------------------------------------ environment
$regPath = 'HKCU:\Software\FileConverter'
$userDir = Join-Path $env:LOCALAPPDATA 'FileConverter'
$userSettings = Join-Path $userDir 'Settings.user.xml'
if (Test-Path $regPath) { throw "$regPath exists (FileConverter installed?): refusing to modify it." }
if ((Test-Path $userDir) -and @(Get-ChildItem $userDir -Exclude 'hw-cache.json', 'Diagnostics-*').Count -gt 0) { throw "$userDir is in use: refusing to modify it." }
[xml]$defaults = Get-Content -Raw -Encoding UTF8 (Join-Path (Split-Path $Exe) 'Settings.default.xml')

$results = @()
try {
    New-Item -Force $regPath | Out-Null
    Set-ItemProperty $regPath -Name Path -Value $Exe
    New-Item -ItemType Directory -Force $userDir | Out-Null

    foreach ($c in $cases) {
        $work = Join-Path $workRoot $c.Name
        if (Test-Path $work) { Remove-Item -Recurse -Force $work }
        New-Item -ItemType Directory -Force $work | Out-Null
        $inputs = @()
        foreach ($pattern in @($c.Inputs)) { $inputs += @(Get-ChildItem (Join-Path $samples $pattern) -File | Sort-Object Name) }
        $linked = @()
        foreach ($f in $inputs) {
            $dst = Join-Path $work $f.Name
            try { New-Item -ItemType HardLink -Path $dst -Target $f.FullName | Out-Null } catch { Copy-Item $f.FullName $dst }
            $linked += $dst
        }
        $listFile = Join-Path $work 'inputs.txt'
        [IO.File]::WriteAllLines($listFile, [string[]]$linked)

        $doc = $defaults.Clone()
        $values = @{ ExitApplicationWhenConversionsFinished = 'true'; DurationBetweenEndOfConversionsAndApplicationExit = '0'; CheckUpgradeAtStartup = 'false'; CopyFilesInClipboardAfterConversion = 'false' }
        foreach ($pair in @(([string]$c.Settings) -split ';' | Where-Object { $_ })) { $k, $v = $pair -split '=', 2; $values[$k] = $v }
        foreach ($k in $values.Keys) {
            $node = $doc.Settings.SelectSingleNode($k)
            if (-not $node) { $node = $doc.CreateElement($k); [void]$doc.Settings.PrependChild($node) }
            $node.InnerText = $values[$k]
        }
        $doc.Save($userSettings)
        Get-ChildItem $userDir -Directory -Filter 'Diagnostics-*' | Remove-Item -Recurse -Force

        $sw = [Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Process -FilePath $Exe -ArgumentList @('--conversion-preset', "`"$($c.Preset)`"", '--input-files', "`"$listFile`"") -PassThru
        $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
        $sw.Stop()
        if (-not $exited) { & taskkill.exe /T /F /PID $proc.Id 2>&1 | Out-Null; Start-Sleep 1 }

        $problems = @()
        if (-not $exited) { $problems += 'did not exit (a conversion failed or timed out)' }
        $log = @(Get-ChildItem $userDir -Recurse -Filter *.log | Get-Content)
        $jobs = @($log | Select-String '^Job (done|failed): path (\S+), [\d.]+ s \([^)]*\): (.*)$' | ForEach-Object { @{ Done = $_.Matches[0].Groups[1].Value -eq 'done'; Path = $_.Matches[0].Groups[2].Value; Input = $_.Matches[0].Groups[3].Value } })
        $paths = @()
        foreach ($f in $linked) {
            # Gif and Ico jobs run an inner job on the same input first: the outer job's line is the last one.
            $job = $jobs | Where-Object { $_.Input -eq $f } | Select-Object -Last 1
            if (-not $job) { $problems += "$(Split-Path $f -Leaf): no job line"; continue }
            $paths += $job.Path
            if (-not $job.Done) { $problems += "$(Split-Path $f -Leaf): job failed ($($job.Path))" }
            elseif (-not (Test-PathLabel $c.Path $job.Path)) { $problems += "$(Split-Path $f -Leaf): path $($job.Path), expected $($c.Path)" }
        }

        $outputs = @(Get-ChildItem $work -File | Where-Object { $linked -notcontains $_.FullName -and $_.Name -ne 'inputs.txt' })
        $expectedOutputs = if ($c.Outputs) { [int]$c.Outputs } else { $linked.Count }
        if ($outputs.Count -ne $expectedOutputs) { $problems += "$($outputs.Count) outputs for $($linked.Count) inputs, expected $expectedOutputs" }
        foreach ($o in $outputs) {
            $name = $o.Name
            if ($c.Streams -eq 'pdf') {
                $head = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($o.FullName), 0, 5)
                if ($head -ne '%PDF-') { $problems += "${name}: not a PDF" }
                continue
            }

            $info = Get-MediaInfo $o.FullName
            if ($info.Error) { $problems += "${name}: $($info.Error)"; continue }
            if ($c.Streams -ne '*' -and $info.Streams -ne $c.Streams) { $problems += "${name}: streams $($info.Streams), expected $($c.Streams)" }
            if ($c.Size -and $info.Size -ne $c.Size) { $problems += "${name}: size $($info.Size), expected $($c.Size)" }
            if ($c.Duration) {
                $expected = $null
                if ($c.Duration -eq 'source') {
                    # Output names add to the input's: "tiny (1).mp4" (same format), "h264_1080p (720p).mp4" (scale presets).
                    $source = $linked | Where-Object { $o.BaseName.StartsWith([IO.Path]::GetFileNameWithoutExtension($_)) } | Select-Object -First 1
                    if ($source) { $expected = (Get-MediaInfo $source).Duration }
                }
                else { $expected = [double]$c.Duration }
                if ($null -eq $expected -or $null -eq $info.Duration) { $problems += "${name}: duration unknown" }
                elseif ([Math]::Abs($info.Duration - $expected) -gt 0.5) { $problems += ("${name}: duration {0:N2} s, expected {1:N2} s" -f $info.Duration, $expected) }
            }
            if ($o.Length -le $MaximumDecodeBytes) {
                $decodeError = Test-Decode $o.FullName
                if ($decodeError) { $problems += "${name}: $decodeError" }
            }
        }

        $status = if ($problems.Count -eq 0) { 'pass' } elseif ($c.Known) { 'known' } else { 'FAIL' }
        if ($status -eq 'known') { $problems = @("known issue: $($c.Known)") + $problems }
        $pathSummary = ($paths | Group-Object | ForEach-Object { if ($_.Count -gt 1) { "$($_.Name) x$($_.Count)" } else { $_.Name } }) -join ', '
        Write-Host ('{0,-5} {1,-34} {2,6:N1}s  {3}' -f $status, $c.Name, $sw.Elapsed.TotalSeconds, $pathSummary)
        foreach ($p in $problems) { Write-Host "       $p" }
        $results += [pscustomobject]@{ case = $c.Name; preset = $c.Preset; inputs = $linked.Count; status = $status; seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1); paths = $pathSummary; problems = ($problems -join ' | ') }

        if (-not $KeepOutputs) { Remove-Item -Recurse -Force $work }
    }
}
finally {
    Remove-Item -Recurse -Force $regPath -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 5 -and (Test-Path $userSettings); $i++) { Remove-Item -Force $userSettings -ErrorAction SilentlyContinue; if (Test-Path $userSettings) { Start-Sleep 1 } }
    Get-ChildItem $userDir -Directory -Filter 'Diagnostics-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Export-Csv -NoTypeInformation -Encoding UTF8 $Output
$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$known = @($results | Where-Object { $_.status -eq 'known' }).Count
Write-Host ("{0} cases, {1} passed, {2} known issues, {3} failed. Results: {4}" -f $results.Count, ($results.Count - $failed - $known), $known, $failed, $Output)
if ($failed -gt 0) { exit 1 }
