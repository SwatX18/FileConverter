<#
.SYNOPSIS
    Runs one preset on one file with optional setting overrides, and prints the encoding-path lines of the app's log.
    For checking fallback paths by hand (see run-bench.ps1 for timing). Environment handling is the same as
    run-bench.ps1: registry and Settings.user.xml are set up for the run and restored afterwards.

.EXAMPLE
    .\bench\try-case.ps1 -Preset "To Mp4" -InputFile bench\samples\video\prores422_1080p.mov
    .\bench\try-case.ps1 -Preset "To Mp4" -InputFile x.mp4 -Settings 'HardwareAccelerationMode=AMF' -PresetSettings 'VideoCodec=Av1'
    .\bench\try-case.ps1 ... -KeepHardwareCache    # reuse %LOCALAPPDATA%\FileConverter\hw-cache.json from the previous run
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Preset,
    # One file, or several separated by ';'.
    [Parameter(Mandatory = $true)][string]$InputFile,
    # 'Key=Value;Key2=Value2' (powershell -File can't pass hashtables).
    [string]$Settings = '',
    [string]$PresetSettings = '',
    [string]$Exe = '',
    [int]$TimeoutSeconds = 600,
    [switch]$KeepHardwareCache,
    [switch]$KeepOutput,
    # Sample the number of concurrent ffmpeg child processes (checks the GPU session limiter).
    [switch]$WatchProcesses,
    # Write Settings.user.xml with this SerializationVersion (e.g. 4 to exercise the settings migration).
    [int]$SettingsVersion = 0,
    # Print the whole diagnostics log instead of the encoding-path lines.
    [switch]$AllLog,
    # Folder for the inputs (hard links, or copies on another volume) and outputs; default bench/work/try-case.
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'
function ConvertTo-Map([string]$Text) {
    $map = @{}
    foreach ($pair in ($Text -split ';' | Where-Object { $_ })) { $k, $v = $pair -split '=', 2; $map[$k.Trim()] = $v }
    $map
}
# New names: the [string]-typed parameters would turn the maps back into strings.
$settingsMap = ConvertTo-Map $Settings
$presetSettingsMap = ConvertTo-Map $PresetSettings
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Exe) { $Exe = Join-Path $here '..\Application\FileConverter\bin\x64\Release\FileConverter.exe' }
$Exe = (Resolve-Path $Exe).Path
$inputFiles = @($InputFile -split ';' | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_).Path })

$regPath = 'HKCU:\Software\FileConverter'
$userDir = Join-Path $env:LOCALAPPDATA 'FileConverter'
$userSettings = Join-Path $userDir 'Settings.user.xml'
$hwCache = Join-Path $userDir 'hw-cache.json'
$hwCacheKeep = Join-Path $env:TEMP 'FileConverter-hw-cache.json'
if (Test-Path $regPath) { throw "$regPath exists (FileConverter installed?): refusing to modify it." }
if ((Test-Path $userDir) -and @(Get-ChildItem $userDir -Exclude 'hw-cache.json', 'Diagnostics-*').Count -gt 0) { throw "$userDir is in use: refusing to modify it." }

$work = if ($WorkDir) { $WorkDir } else { Join-Path $here 'work\try-case' }
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
$linkedFiles = @()
for ($i = 0; $i -lt $inputFiles.Count; $i++) {
    # Prefix with the index so the same sample can be listed several times.
    $name = if ($inputFiles.Count -gt 1) { '{0:D2}_{1}' -f $i, (Split-Path $inputFiles[$i] -Leaf) } else { Split-Path $inputFiles[$i] -Leaf }
    $dst = Join-Path $work $name
    try { New-Item -ItemType HardLink -Path $dst -Target $inputFiles[$i] | Out-Null } catch { Copy-Item $inputFiles[$i] $dst }
    $linkedFiles += $dst
}
$listFile = Join-Path $here 'work\try-case.list.txt'
[IO.File]::WriteAllLines($listFile, [string[]]$linkedFiles)

try {
    New-Item -Force $regPath | Out-Null
    Set-ItemProperty $regPath -Name Path -Value $Exe
    New-Item -ItemType Directory -Force $userDir | Out-Null
    if ($KeepHardwareCache -and (Test-Path $hwCacheKeep)) { Copy-Item -Force $hwCacheKeep $hwCache }

    [xml]$doc = Get-Content -Raw -Encoding UTF8 (Join-Path (Split-Path $Exe) 'Settings.default.xml')
    $all = @{ ExitApplicationWhenConversionsFinished = 'true'; DurationBetweenEndOfConversionsAndApplicationExit = '0'; CheckUpgradeAtStartup = 'false'; CopyFilesInClipboardAfterConversion = 'false' }
    foreach ($k in $settingsMap.Keys) { $all[$k] = [string]$settingsMap[$k] }
    foreach ($k in $all.Keys) {
        $node = $doc.Settings.SelectSingleNode($k)
        if (-not $node) { $node = $doc.CreateElement($k); [void]$doc.Settings.PrependChild($node) }
        $node.InnerText = $all[$k]
    }
    $presetNode = $doc.Settings.SelectSingleNode("ConversionPreset[@Name='$Preset']")
    if (-not $presetNode) { throw "Preset '$Preset' not found in Settings.default.xml." }
    foreach ($k in $presetSettingsMap.Keys) {
        $s = $presetNode.SelectSingleNode("Settings[@Key='$k']")
        if (-not $s) { $s = $doc.CreateElement('Settings'); $s.SetAttribute('Key', $k); [void]$presetNode.AppendChild($s) }
        $s.SetAttribute('Value', [string]$presetSettingsMap[$k])
    }
    if ($SettingsVersion -gt 0) { $doc.Settings.SetAttribute('SerializationVersion', [string]$SettingsVersion) }
    $doc.Save($userSettings)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $Exe -ArgumentList @('--conversion-preset', "`"$Preset`"", '--input-files', "`"$listFile`"") -PassThru
    $maxFfmpeg = 0
    if ($WatchProcesses) {
        while (-not $proc.WaitForExit(250) -and $sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            $n = @(Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe' AND ParentProcessId=$($proc.Id)").Count
            if ($n -gt $maxFfmpeg) { $maxFfmpeg = $n }
        }
    }
    $exited = $proc.WaitForExit([Math]::Max(0, ($TimeoutSeconds - $sw.Elapsed.TotalSeconds)) * 1000)
    $sw.Stop()
    if ($WatchProcesses) { Write-Host "max concurrent ffmpeg processes: $maxFfmpeg" }
    if (-not $exited) { & taskkill.exe /T /F /PID $proc.Id 2>&1 | Out-Null; [void]$proc.WaitForExit(5000); Start-Sleep 1 }
    Write-Host ('exit: {0} in {1:N1}s' -f $(if ($exited) { 'auto-exit (all conversions succeeded)' } else { 'did not exit (failure or timeout), killed' }), $sw.Elapsed.TotalSeconds)

    $patterns = 'imported from version|Hardware encoders|unavailable|Conversion limits|Start job|Hard disk|Image engine|libvips|Remux|Output validation|Encoding path|Falling back|fallback encoding path|exited with code|^Fail: |hardware encoder cache'
    if ($AllLog) { $patterns = '.' }
    Get-ChildItem $userDir -Recurse -Filter *.log | Get-Content | Select-String -Pattern $patterns | ForEach-Object { '  log> ' + $_.Line }
    Get-ChildItem $userDir -Recurse -Filter *.log | Get-Content | Select-String -Pattern '^Execute command: ' | ForEach-Object { '  cmd> ' + ($_.Line -replace '^Execute command: \S+ ', '') }

    $outputs = @(Get-ChildItem $work -File | Where-Object { $linkedFiles -notcontains $_.FullName })
    foreach ($o in $outputs) {
        # ffprobe warnings on stderr must not stop the script (ErrorActionPreference is Stop).
        $ErrorActionPreference = 'Continue'
        $info = & ffprobe -v error -show_entries 'stream=codec_type,codec_name,pix_fmt,width,height:format=duration' -of compact=p=0:nk=1 -- $o.FullName 2>&1
        $ErrorActionPreference = 'Stop'
        Write-Host ("  out> {0} ({1:N1} MB): {2}" -f $o.Name, ($o.Length / 1MB), (($info | ForEach-Object { "$_" }) -join ' / '))
    }
    if ($outputs.Count -eq 0) { Write-Host '  out> (no output file)' }
}
finally {
    if (Test-Path $hwCache) { Copy-Item -Force $hwCache $hwCacheKeep }
    Remove-Item -Recurse -Force $regPath -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 5 -and (Test-Path $userDir); $i++) { Remove-Item -Recurse -Force $userDir -ErrorAction SilentlyContinue; if (Test-Path $userDir) { Start-Sleep 1 } }
    if (-not $KeepOutput) { Remove-Item -Recurse -Force $work, $listFile -ErrorAction SilentlyContinue }
}
