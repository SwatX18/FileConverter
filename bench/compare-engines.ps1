<#
.SYNOPSIS
    Converts the same images with the libvips engine and with ImageMagick only (setting ImageEngine), and compares
    time, output size, dimensions and SSIM (ffmpeg's ssim filter, yuv444p) of each output against the source (scaled to
    the output size), plus the SSIM between the two outputs.

.EXAMPLE
    .\bench\compare-engines.ps1                       # default cases, writes bench\work\compare-engines.csv
    .\bench\compare-engines.ps1 -Output bench\image-engines.csv
#>
[CmdletBinding()]
param(
    [string]$Output = '',
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Output) { $Output = Join-Path $here 'work\compare-engines.csv' }
$exeArgs = if ($Exe) { @('-Exe', $Exe) } else { @() }
$samples = Join-Path $here 'samples'

$cases = @(
    @{ Preset = 'To Jpg';            Input = 'jpg_24mp\img_001.jpg' }
    @{ Preset = 'To Webp';           Input = 'jpg_24mp\img_001.jpg' }
    @{ Preset = 'Scale 25%/To Webp'; Input = 'jpg_24mp\img_001.jpg' }
    @{ Preset = 'Scale 75%/To Jpg';  Input = 'jpg_24mp\img_002.jpg' }
    @{ Preset = 'To Png';            Input = 'jpg_24mp\img_003.jpg' }
    @{ Preset = 'To Avif';           Input = 'jpg_24mp\img_004.jpg' }
    @{ Preset = 'To Jpg';            Input = 'png\image_01.png' }
    @{ Preset = 'To Png';            Input = 'webp\image_01.webp' }
    @{ Preset = 'To Jpg';            Input = 'tiff\scan_01.tif' }
)

$compareDir = Join-Path $here 'work\compare-engines'
if (Test-Path $compareDir) { Remove-Item -Recurse -Force $compareDir }
New-Item -ItemType Directory $compareDir | Out-Null
$ffmpeg = Join-Path $here '..\Application\FileConverter\bin\x64\Release\ffmpeg.exe'

function Resolve-Input([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $samples $path }
}

# The synthetic JPEGs (gradients + strong noise) are a worst case for resampler differences: also use real photos,
# made by converting the corpus' HEIC photos to JPEG (q90) with ImageMagick.
$photoDir = Join-Path $here 'work\compare-photos'
New-Item -ItemType Directory -Force $photoDir | Out-Null
foreach ($heic in 'meadow', 'big_tree') {
    $photo = Join-Path $photoDir "$heic.jpg"
    if (-not (Test-Path $photo)) {
        & (Join-Path $here 'try-case.ps1') -Preset 'To Jpg' -InputFile (Join-Path $samples "heic\$heic.heic") -Settings 'ImageEngine=ImageMagick' -KeepHardwareCache -KeepOutput @exeArgs *>&1 | Out-Null
        Move-Item (Join-Path $here "work\try-case\$heic.jpg") $photo
    }
    $cases += @{ Preset = 'Scale 75%/To Jpg'; Input = $photo }
    $cases += @{ Preset = 'Scale 25%/To Webp'; Input = $photo }
    $cases += @{ Preset = 'To Avif'; Input = $photo }
}

function Invoke-Case($case, [string]$engine, [string]$tag) {
    $lines = & (Join-Path $here 'try-case.ps1') -Preset $case.Preset -InputFile (Resolve-Input $case.Input) -Settings "ImageEngine=$engine" -KeepHardwareCache -KeepOutput @exeArgs *>&1 | ForEach-Object { "$_" }
    $seconds = [double](($lines | Select-String 'exit: .* in ([0-9.,]+)s').Matches[0].Groups[1].Value -replace ',', '.')
    $path = ($lines | Select-String 'Image engine: (\S+)').Matches | ForEach-Object { $_.Groups[1].Value } | Select-Object -First 1
    $work = Join-Path $here 'work\try-case'
    $out = Get-ChildItem $work -File | Where-Object { $_.Name -ne (Split-Path $case.Input -Leaf) } | Select-Object -First 1
    $dest = $null
    if ($out) { $dest = Join-Path $compareDir ("{0}_{1}{2}" -f $tag, $engine, $out.Extension); Move-Item $out.FullName $dest }
    [pscustomobject]@{ Seconds = $seconds; Path = $path; File = $dest }
}

function Get-Dimensions([string]$file) {
    $ErrorActionPreference = 'Continue'
    $wh = & $ffmpeg -hide_banner -i $file 2>&1 | Select-String ', (\d{2,5})x(\d{2,5})' | Select-Object -First 1
    if ($wh) { '{0}x{1}' -f $wh.Matches[0].Groups[1].Value, $wh.Matches[0].Groups[2].Value } else { '?' }
}

function Get-Ssim([string]$a, [string]$b) {
    # Scale the second image to the first one's size in case of a 1 px rounding difference, compare in yuv444p.
    $ErrorActionPreference = 'Continue'
    $log = & $ffmpeg -hide_banner -i $a -i $b -lavfi '[0:v]format=yuv444p[a];[1:v][a]scale2ref=flags=bicubic[b][a2];[b]format=yuv444p[b2];[a2][b2]ssim' -f null - 2>&1 | Select-String 'All:([0-9.]+)'
    if ($log) { [double]$log.Matches[0].Groups[1].Value } else { $null }
}

$rows = @()
$index = 0
foreach ($case in $cases) {
    $index++
    $tag = '{0:D2}' -f $index
    Write-Host "[$tag] $($case.Preset) <- $($case.Input)"
    $magick = Invoke-Case $case 'ImageMagick' $tag
    $vips = Invoke-Case $case 'Auto' $tag
    $source = Resolve-Input $case.Input
    $ssim = if ($magick.File -and $vips.File) { Get-Ssim $magick.File $vips.File } else { $null }
    $ssimMagick = if ($magick.File) { Get-Ssim $magick.File $source } else { $null }
    $ssimVips = if ($vips.File) { Get-Ssim $vips.File $source } else { $null }
    $row = [pscustomobject]@{
        preset = $case.Preset; input = $case.Input
        magick_s = $magick.Seconds; vips_s = $vips.Seconds; vips_path = $vips.Path
        magick_kb = if ($magick.File) { [math]::Round((Get-Item $magick.File).Length / 1KB) } else { $null }
        vips_kb = if ($vips.File) { [math]::Round((Get-Item $vips.File).Length / 1KB) } else { $null }
        magick_dims = if ($magick.File) { Get-Dimensions $magick.File } else { '' }
        vips_dims = if ($vips.File) { Get-Dimensions $vips.File } else { '' }
        ssim_magick_vs_source = $ssimMagick
        ssim_vips_vs_source = $ssimVips
        ssim_between = $ssim
    }
    $row | Format-List | Out-String | Write-Host
    $rows += $row
}
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 $Output
Write-Host "Written $Output"
