<#
.SYNOPSIS
    Runs representative conversions through FileConverter.exe's command line and records the results in a CSV.

.DESCRIPTION
    Each case launches FileConverter.exe exactly the way the shell extension does
    (--conversion-preset "<name>" --input-files <list>). It measures wall time from launch to exit, which
    includes about 1-2 s of WPF startup (see the 'startup_overhead' case), and validates every output with ffprobe.

    For the duration of the run the script:
      - points HKCU\Software\FileConverter\Path at the exe under test (the app needs it to find Settings.default.xml);
      - writes %LOCALAPPDATA%\FileConverter\Settings.user.xml from the exe's Settings.default.xml with:
        auto-exit on, 0 s exit delay, upgrade check off, clipboard copy off (everything else is left at the defaults);
    and restores both afterwards (including when interrupted).

    The app does not exit when a conversion fails, and ffmpeg can deadlock (see docs/ARCHITECTURE.md). So a case
    whose whole process tree has used no CPU and written no bytes for -StallSeconds is stopped and classified:
      hung    = an ffmpeg child process was still alive (blocked)
      failed  = no child alive: the app is idle and showing a failure
    'seconds' is then the time until the last observed activity.

    Inputs are hard-linked into bench/work/<case>/ so the outputs (written next to the inputs, as the presets do)
    never touch bench/samples/. Outputs are deleted after validation unless -KeepOutputs is given.

.EXAMPLE
    .\bench\run-bench.ps1                                   # all cases -> bench/baseline.csv
    .\bench\run-bench.ps1 -Output bench\results.csv -Label phase1
    .\bench\run-bench.ps1 -Case video_*,img_jpg200*
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$Samples = '',
    [string]$Output = '',
    [string]$Label = 'baseline',
    [string[]]$Case = @('*'),
    [int]$StallSeconds = 45,
    [int]$TimeoutSeconds = 3600,
    # Leave empty to keep Settings.default.xml's values (2 simultaneous conversions, hardware acceleration Off).
    [string]$MaxSimultaneous = '',
    [string]$HardwareAcceleration = '',
    [switch]$KeepOutputs
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is not available in param defaults under Windows PowerShell 5.1 -File.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Exe) { $Exe = Join-Path $here '..\Application\FileConverter\bin\x64\Release\FileConverter.exe' }
if (-not $Samples) { $Samples = Join-Path $here 'samples' }
if (-not $Output) { $Output = Join-Path $here 'baseline.csv' }
$Exe = (Resolve-Path $Exe).Path
$Samples = (Resolve-Path $Samples).Path
$WorkRoot = Join-Path $here 'work'

$ffprobe = (Get-Command ffprobe -ErrorAction SilentlyContinue).Source
if (-not $ffprobe) { $ffprobe = Join-Path $here '..\Middleware\ffmpeg\ffprobe.exe' }
if (-not (Test-Path $ffprobe)) { throw 'ffprobe not found (needed to validate outputs). Put it on PATH or in Middleware\ffmpeg.' }

# ------------------------------------------------------------------ cases
# Inputs are paths relative to bench/samples (wildcards allowed). Every input must produce exactly one output.
$cases = @(
    @{ Name = 'startup_overhead';            Preset = 'To Mp3';             Inputs = 'audio\tiny.wav' }
    @{ Name = 'video_h264_1080p_to_mp4';     Preset = 'To Mp4';             Inputs = 'video\h264_1080p.mp4' }
    @{ Name = 'video_h264_1080p_to_mkv';     Preset = 'To Mkv';             Inputs = 'video\h264_1080p.mp4' }
    @{ Name = 'video_h264_4k_to_mp4';        Preset = 'To Mp4';             Inputs = 'video\h264_4k.mp4' }
    @{ Name = 'video_hevc_mkv_to_mp4';       Preset = 'To Mp4';             Inputs = 'video\hevc_1080p.mkv' }
    @{ Name = 'video_mkv_flac_subs_to_mp4';  Preset = 'To Mp4';             Inputs = 'video\h264_flac_subs.mkv' }
    @{ Name = 'video_iphone_mov_to_mp4';     Preset = 'To Mp4';             Inputs = 'video\iphone_hevc10.mov' }
    @{ Name = 'video_1080p_scale720_mp4';    Preset = 'Scale 720p/To Mp4';  Inputs = 'video\h264_1080p.mp4' }
    @{ Name = 'video_extract_mp3';           Preset = 'To Mp3';             Inputs = 'video\h264_1080p.mp4' }
    @{ Name = 'video_iphone_mov_to_aac';     Preset = 'To Aac';             Inputs = 'video\iphone_hevc10.mov' }
    @{ Name = 'audio_flac_to_mp3';           Preset = 'To Mp3';             Inputs = 'audio\album_track_24bit.flac' }
    @{ Name = 'audio_wav_to_flac';           Preset = 'To Flac';            Inputs = 'audio\recording_16bit.wav' }
    @{ Name = 'audio_flac_to_aac';           Preset = 'To Aac';             Inputs = 'audio\album_track_24bit.flac' }
    @{ Name = 'img_jpg200_scale25_webp';     Preset = 'Scale 25%/To Webp';  Inputs = 'jpg_24mp\*.jpg' }
    @{ Name = 'img_jpg20_to_jpg';            Preset = 'To Jpg';             Inputs = 'jpg_24mp\img_00*.jpg', 'jpg_24mp\img_01[0-9].jpg', 'jpg_24mp\img_020.jpg' }
    @{ Name = 'img_jpg20_to_avif';           Preset = 'To Avif';            Inputs = 'jpg_24mp\img_00*.jpg', 'jpg_24mp\img_01[0-9].jpg', 'jpg_24mp\img_020.jpg' }
    @{ Name = 'img_png_to_jpg';              Preset = 'To Jpg';             Inputs = 'png\*.png' }
    @{ Name = 'img_webp_to_png';             Preset = 'To Png';             Inputs = 'webp\*.webp' }
    @{ Name = 'img_tiff_to_jpg';             Preset = 'To Jpg';             Inputs = 'tiff\*.tif' }
    @{ Name = 'img_heic_to_jpg';             Preset = 'To Jpg';             Inputs = 'heic\*.heic' }
    @{ Name = 'img_raw_to_jpg';              Preset = 'To Jpg';             Inputs = 'raw\*.*' }
)
$Case = @($Case | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # -File passes "a,b" as one string
$cases = @($cases | Where-Object { $n = $_.Name; @($Case | Where-Object { $n -like $_ }).Count -gt 0 })
if ($cases.Count -eq 0) { throw "No case matches '$($Case -join ',')'." }

# ------------------------------------------------------------------ helpers
function Get-Tree([int]$RootId) {
    # Root process + all descendants (FileConverter -> ffmpeg / gswin64c ...).
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, KernelModeTime, UserModeTime)
    $ids = New-Object System.Collections.Generic.HashSet[int]
    [void]$ids.Add($RootId)
    do {
        $added = $false
        foreach ($p in $all) {
            if ($ids.Contains([int]$p.ParentProcessId) -and -not $ids.Contains([int]$p.ProcessId)) { [void]$ids.Add([int]$p.ProcessId); $added = $true }
        }
    } while ($added)
    @($all | Where-Object { $ids.Contains([int]$_.ProcessId) })
}

function Stop-Tree([int]$RootId) {
    & taskkill.exe /T /F /PID $RootId 2>&1 | Out-Null
}

function Get-DirBytes([string]$Dir) {
    $s = (Get-ChildItem $Dir -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($s) { [long]$s } else { 0L }
}

function Test-Media([string]$Path) {
    # Returns @{ Ok; Duration; Info }.
    $ErrorActionPreference = 'Continue'   # PS 5.1 turns native stderr into a terminating error under 'Stop'
    $json = & $ffprobe -v error -show_entries 'format=duration,format_name:stream=codec_type,codec_name' -of json -- "$Path" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { return @{ Ok = $false; Duration = $null; Info = 'ffprobe error' } }
    $j = ($json -join "`n") | ConvertFrom-Json
    if (-not $j.streams -or @($j.streams).Count -eq 0) { return @{ Ok = $false; Duration = $null; Info = 'no streams' } }
    $d = $null; if ($j.format.duration) { $d = [double]::Parse($j.format.duration, [Globalization.CultureInfo]::InvariantCulture) }
    @{ Ok = $true; Duration = $d; Info = (@($j.streams | ForEach-Object { "$($_.codec_type):$($_.codec_name)" }) -join '+') }
}

function Get-DecodedDuration([string]$Path) {
    $ErrorActionPreference = 'Continue'
    $ffmpeg = Join-Path (Split-Path $ffprobe) 'ffmpeg.exe'
    $last = & $ffmpeg -hide_banner -nostats -v error -stats -i "$Path" -f null - 2>&1 | ForEach-Object { "$_" } | Select-String 'time=(\d+):(\d+):(\d+\.\d+)' | Select-Object -Last 1
    if (-not $last) { return $null }
    $g = $last.Matches[0].Groups
    [int]$g[1].Value * 3600 + [int]$g[2].Value * 60 + [double]::Parse($g[3].Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Set-XmlElement([xml]$Doc, [string]$Name, [string]$Value) {
    $node = $Doc.Settings.SelectSingleNode($Name)
    if (-not $node) { $node = $Doc.CreateElement($Name); [void]$Doc.Settings.PrependChild($node) }
    $node.InnerText = $Value
}

# ------------------------------------------------------------------ environment setup (restored in finally)
$regPath = 'HKCU:\Software\FileConverter'
$regExisted = Test-Path $regPath
$regOldPath = if ($regExisted) { (Get-ItemProperty $regPath -ErrorAction SilentlyContinue).Path } else { $null }
$userDir = Join-Path $env:LOCALAPPDATA 'FileConverter'
$userDirExisted = Test-Path $userDir
$userSettings = Join-Path $userDir 'Settings.user.xml'
$settingsBackup = "$userSettings.bench-backup"
$hadUserSettings = Test-Path $userSettings

$commit = (& git -C $here rev-parse --short HEAD 2>$null)
$gpu = (@(Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }) -join ' + ')
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()

$rows = New-Object System.Collections.Generic.List[object]
try {
    if ($hadUserSettings) { Copy-Item -Force $userSettings $settingsBackup }
    New-Item -Force $regPath | Out-Null
    Set-ItemProperty $regPath -Name Path -Value $Exe

    [xml]$settings = Get-Content -Raw -Encoding UTF8 (Join-Path (Split-Path $Exe) 'Settings.default.xml')
    Set-XmlElement $settings 'ExitApplicationWhenConversionsFinished' 'true'
    Set-XmlElement $settings 'DurationBetweenEndOfConversionsAndApplicationExit' '0'
    Set-XmlElement $settings 'CheckUpgradeAtStartup' 'false'
    Set-XmlElement $settings 'CopyFilesInClipboardAfterConversion' 'false'
    if ($MaxSimultaneous) { Set-XmlElement $settings 'MaximumNumberOfSimultaneousConversions' $MaxSimultaneous }
    if ($HardwareAcceleration) { Set-XmlElement $settings 'HardwareAccelerationMode' $HardwareAcceleration }
    New-Item -ItemType Directory -Force $userDir | Out-Null
    $settings.Save($userSettings)
    $effMax = $settings.Settings.MaximumNumberOfSimultaneousConversions
    $effHw = if ($settings.Settings.HardwareAccelerationMode) { $settings.Settings.HardwareAccelerationMode } else { 'Off' }

    Write-Host "Exe: $Exe (commit $commit)"
    Write-Host "Machine: $cpu | $gpu"
    Write-Host "Settings: MaximumNumberOfSimultaneousConversions=$effMax HardwareAccelerationMode=$effHw"

    foreach ($c in $cases) {
        $inputs = @(foreach ($pattern in $c.Inputs) { Get-ChildItem (Join-Path $Samples $pattern) -File -ErrorAction SilentlyContinue }) | Sort-Object FullName -Unique
        if ($inputs.Count -eq 0) {
            Write-Warning "[$($c.Name)] no input found ($($c.Inputs -join ', ')): skipped. Run bench\make-corpus.ps1."
            $rows.Add([pscustomobject]@{ case = $c.Name; preset = $c.Preset; inputs = 0; input_mb = 0; outputs_ok = 0; outputs_expected = 0; output_mb = 0; seconds = ''; status = 'skipped'; notes = 'missing samples' })
            continue
        }

        $work = Join-Path $WorkRoot $c.Name
        if (Test-Path $work) { Remove-Item -Recurse -Force $work }
        New-Item -ItemType Directory -Force $work | Out-Null
        $linked = foreach ($f in $inputs) {
            $dst = Join-Path $work $f.Name
            try { New-Item -ItemType HardLink -Path $dst -Target $f.FullName | Out-Null } catch { Copy-Item $f.FullName $dst }
            $dst
        }
        $listFile = Join-Path $WorkRoot "$($c.Name).list.txt"
        [IO.File]::WriteAllLines($listFile, [string[]]$linked)
        $before = @{}; Get-ChildItem $work -File | ForEach-Object { $before[$_.Name] = $true }
        $diagBefore = @(Get-ChildItem $userDir -Directory -Filter 'Diagnostics-*' -ErrorAction SilentlyContinue | ForEach-Object FullName)

        Write-Host ("[{0}] {1} input(s), preset '{2}' ..." -f $c.Name, $inputs.Count, $c.Preset) -NoNewline
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Process -FilePath $Exe -ArgumentList @('--conversion-preset', "`"$($c.Preset)`"", '--input-files', "`"$listFile`"") -PassThru
        $lastCpu = -1.0; $lastBytes = -1L; $lastActivity = 0.0; $status = $null; $childAlive = $false
        while ($true) {
            if ($proc.WaitForExit(1000)) { break }   # returns as soon as the app exits: exact wall time
            $tree = Get-Tree $proc.Id
            $cpuNow = (($tree | ForEach-Object { [double]$_.KernelModeTime + [double]$_.UserModeTime } | Measure-Object -Sum).Sum) / 1e7
            $bytes = Get-DirBytes $work
            $childAlive = @($tree | Where-Object { $_.ProcessId -ne $proc.Id }).Count -gt 0
            if (($cpuNow - $lastCpu) -gt 0.05 -or $bytes -ne $lastBytes) { $lastActivity = $sw.Elapsed.TotalSeconds }
            $lastCpu = $cpuNow; $lastBytes = $bytes
            if (($sw.Elapsed.TotalSeconds - $lastActivity) -gt $StallSeconds) { $status = if ($childAlive) { 'hung' } else { 'failed' }; break }
            if ($sw.Elapsed.TotalSeconds -gt $TimeoutSeconds) { $status = 'timeout'; break }
        }
        $sw.Stop()
        if ($status) { Stop-Tree $proc.Id; Start-Sleep -Milliseconds 500; $seconds = $lastActivity } else { $seconds = $sw.Elapsed.TotalSeconds }

        # Validate outputs.
        $outputs = @(Get-ChildItem $work -File | Where-Object { -not $before.ContainsKey($_.Name) })
        $okCount = 0; $issues = New-Object System.Collections.Generic.List[string]; $infos = @{}
        $refDuration = $null
        if ($inputs.Count -eq 1) { $refDuration = (Test-Media $inputs[0].FullName).Duration }
        foreach ($o in $outputs) {
            $m = Test-Media $o.FullName
            if (-not $m.Ok -or $o.Length -eq 0) { $issues.Add("$($o.Name): $($m.Info)"); continue }
            $infos[$m.Info] = $true
            if ($refDuration -and $refDuration -gt 2 -and $m.Duration -and [math]::Abs($m.Duration - $refDuration) -gt 1.0) {
                # Raw streams (ADTS .aac, ...) have no duration header: ffprobe only estimates it. Decode to be sure.
                $m.Duration = Get-DecodedDuration $o.FullName
            }
            if ($refDuration -and $refDuration -gt 2 -and $m.Duration -and [math]::Abs($m.Duration - $refDuration) -gt 1.0) {
                $issues.Add(('{0}: duration {1:N1}s vs {2:N1}s' -f $o.Name, $m.Duration, $refDuration)); continue
            }
            $okCount++
        }
        if (-not $status) {
            $status = if ($okCount -eq $inputs.Count -and $outputs.Count -eq $inputs.Count) { 'ok' } elseif ($okCount -gt 0) { 'partial' } else { 'failed' }
        } elseif ($status -eq 'failed' -and $okCount -eq $inputs.Count) {
            $status = 'ok'   # Everything converted; the app just did not exit.
        }

        # Failure reasons from the app's diagnostics logs.
        $newDiag = @(Get-ChildItem $userDir -Directory -Filter 'Diagnostics-*' -ErrorAction SilentlyContinue | Where-Object { $diagBefore -notcontains $_.FullName })
        $fails = @($newDiag | ForEach-Object { Get-ChildItem $_.FullName -Filter *.log } | Select-String -Pattern '^Fail: (.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() } | Select-Object -Unique -First 2)
        $notes = @()
        if ($infos.Count) { $notes += ($infos.Keys | Sort-Object) -join ' / ' }
        if ($status -eq 'hung') { $notes += 'ffmpeg blocked, no progress for {0}s' -f $StallSeconds }
        if ($fails.Count) { $notes += 'app: ' + ($fails -join ' | ') }
        if ($issues.Count) { $notes += ($issues | Select-Object -First 2) }

        $row = [pscustomobject]@{
            case = $c.Name; preset = $c.Preset; inputs = $inputs.Count
            input_mb = [math]::Round((($inputs | Measure-Object Length -Sum).Sum) / 1MB, 1)
            outputs_ok = $okCount; outputs_expected = $inputs.Count
            output_mb = [math]::Round((($outputs | Measure-Object Length -Sum).Sum) / 1MB, 1)
            seconds = [math]::Round($seconds, 2); status = $status; notes = ($notes -join '; ')
        }
        $rows.Add($row)
        Write-Host (' {0} in {1:N1}s ({2}/{3} ok, {4} MB)' -f $status, $seconds, $okCount, $inputs.Count, $row.output_mb)
        if ($row.notes) { Write-Host "    $($row.notes)" }

        if (-not $KeepOutputs) { Remove-Item -Recurse -Force $work; Remove-Item -Force $listFile }
        # Diagnostics folders are named by h/m/s: wait so the next run gets its own folder.
        Start-Sleep -Milliseconds 1100
    }
}
finally {
    Get-Process FileConverter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | ForEach-Object { Stop-Tree $_.Id }
    if ($hadUserSettings) { Move-Item -Force $settingsBackup $userSettings }
    elseif (Test-Path $userSettings) { Remove-Item -Force $userSettings }
    if (-not $userDirExisted -and (Test-Path $userDir)) {
        # Keep the diagnostics of this run next to the CSV for inspection, then remove the folder we created.
        $diagOut = Join-Path $here "diagnostics-$Label"
        New-Item -ItemType Directory -Force $diagOut | Out-Null
        Get-ChildItem $userDir -Directory -Filter 'Diagnostics-*' | Move-Item -Destination $diagOut -Force -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $userDir -ErrorAction SilentlyContinue
    }
    if ($regExisted) { if ($regOldPath) { Set-ItemProperty $regPath -Name Path -Value $regOldPath } }
    else { Remove-Item -Recurse -Force $regPath -ErrorAction SilentlyContinue }
}

$stamp = Get-Date -Format 's'
$out = $rows | ForEach-Object {
    [pscustomobject]([ordered]@{
        timestamp = $stamp; label = $Label; commit = $commit; max_simultaneous = $effMax; hw_accel = $effHw
        case = $_.case; preset = $_.preset; inputs = $_.inputs; input_mb = $_.input_mb
        outputs_ok = $_.outputs_ok; outputs_expected = $_.outputs_expected; output_mb = $_.output_mb
        seconds = $_.seconds; status = $_.status; notes = $_.notes
    })
}
$out | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $Output
Write-Host "Wrote $Output ($($rows.Count) cases). Machine: $cpu | $gpu"
