# FileConverter++

**FileConverter++ is a faster version of [File Converter](https://github.com/Tichau/FileConverter)**, the tool by Adrien Allard that converts and compresses files from the Windows Explorer right-click menu. It works exactly the same way, with the same presets and settings, but uses your graphics card for videos, avoids re-encoding when it isn't needed, uses a much faster image engine and converts many files at once. On the benchmark below it is **2.5 times faster on average, and up to 35 times faster**.

![FileConverter++ Usage](Resources/FileConverterUsage.gif)

**[Download FileConverter++ (latest release)](https://github.com/SwatX18/FileConverterPlusPlus/releases/latest)** · Windows 10/11, 64-bit · free and open source (GPL-3.0)

- [FileConverter++ vs. File Converter at a glance](#fileconverter-vs-file-converter-at-a-glance)
- [How much faster?](#how-much-faster)
- [What changed, in detail](#what-changed-in-detail)
- [Install, upgrade, uninstall](#install-upgrade-uninstall)
- [For developers](#for-developers)

## FileConverter++ vs. File Converter at a glance

| | File Converter 2.2 (original) | FileConverter++ 2.3 |
|---|---|---|
| Video encoding | CPU only by default (x264, x265) | **Graphics card** (NVIDIA NVENC, AMD AMF, Intel Quick Sync), detected automatically, with the CPU as fallback |
| mkv → mp4, mov → mp4... | Always re-encodes the video | **Copies the streams** when they fit the new container (seconds instead of minutes, no quality loss) |
| Images (JPEG, PNG, WebP, TIFF, AVIF) | ImageMagick | **libvips** (1.2 to 2.7 times faster per image, much more in batches), ImageMagick for every other format and as fallback |
| Several files at once | 2 conversions at a time | **One per CPU core**, with separate limits for GPU encodes, CPU video encodes and Office documents |
| Long video conversions | Can **hang forever** (ffmpeg blocked) | Fixed |
| Two files giving the same output name (a.mp4 + a.mkv → Mp3) | One conversion fails | Second output named `a (2).mp3` |
| PDF conversions | Need the Visual C++ Redistributable installed by another program | Everything included in the installer |
| Security | Magick.NET 14.10.2 (144 known advisories), Ghostscript 10.02.1 (CVE-2024-29510) | Magick.NET 14.17.2, Ghostscript 10.08.0 |
| Settings | Application tab | New **Performance** tab: detected GPUs, image engine, limits, diagnostics |
| Logs | Conversion log | Plus the path each file took (remux, nvenc, cpu-fallback, vips...) and its duration |
| Video codecs for Mp4 / Mkv presets | H.264 | **H.264, HEVC or AV1** (AV1 when your GPU can encode it) |
| Tests | — | 168 unit tests, 40 integration tests, a benchmark suite |

Your presets, settings and right-click menu are the same. Every new behavior can be turned off in Settings → Performance, and every optimization **falls back automatically** to the original behavior: if a faster path can't be used for a file, or fails, the file is converted the original way.

## How much faster?

Measured on a Ryzen 9 7950X3D (16 cores) with an RTX 5070 Ti, NVMe SSD, both versions at their default settings, median of 3 runs, including the application start ([full report](bench/REPORT.md)):

| Conversion | File Converter | FileConverter++ | Speed-up | How |
|---|---:|---:|---:|---|
| Movie, 15 min 1080p MKV → To Mp4 | 81.5 s | **2.3 s** | 35× | stream copy |
| 200 photos (24 MP JPEG) → Scale 25% / To Webp | 55.8 s | **3.6 s** | 16× | libvips + parallel |
| 1080p H.264 MP4 → To Mkv | 10.7 s | **1.8 s** | 6× | stream copy |
| iPhone HEVC 10-bit MOV → To Mp4 | 9.0 s | **1.5 s** | 6× | stream copy |
| 10 WebP images (24 MP) → To Png | 22.4 s | **4.9 s** | 4.6× | libvips + parallel |
| 20 photos → To Jpg | 3.6 s | **1.0 s** | 3.6× | libvips + parallel |
| 4K H.264 30 s → To Mp4 | 20.2 s | **7.1 s** | 2.9× | NVENC |
| 1080p H.264 MP4 → To Mp4 (re-encode) | 10.7 s | **4.3 s** | 2.5× | NVENC |
| 20 photos → To Avif | 24.9 s | **10.7 s** | 2.3× | libvips + parallel (and 31 % smaller) |
| Movie, 15 min MKV → To Mkv (re-encode) | 80.9 s | **36.4 s** | 2.2× | NVENC |
| 1080p → Scale 720p / To Mp4 | 5.4 s | **2.9 s** | 1.9× | NVENC |

**Over the 24 benchmark cases: 2.5× faster on average (geometric mean), all 25 cases successful.** The gain depends on your hardware: no supported graphics card means CPU video encoding as before (still with stream copy, libvips and parallel batches), and fewer CPU cores means less parallelism.

**Not faster** (to be honest about it):
- **Audio conversions** (FLAC → MP3, WAV → FLAC...): unchanged, the encoders are the same.
- **RAW and HEIC photos**: still converted by ImageMagick; 8 to 14 % slower for RAW and about 5 % for HEIC since the Magick.NET security update.
- **Extracting the audio of a video** (video → To Mp3 / To Aac): about 0.3 s slower on short files, because the file is first analyzed to see whether its audio can be copied without re-encoding (which saves the whole encode on long files).

**File sizes**: GPU encodes are calibrated to the same visual quality as the CPU encoders (measured with VMAF) and are usually smaller. Stream copies keep the source's quality and bitrate, so they can be larger than a re-encode; pick a "low quality" preset if you want a smaller file.

## What changed, in detail

### Videos: graphics card encoding

MP4 and MKV conversions use the hardware encoder of your graphics card: **NVIDIA NVENC, AMD AMF or Intel Quick Sync**, for H.264, HEVC and AV1 (tested on NVIDIA and AMD; Intel Quick Sync is supported but wasn't available on the test machine). FileConverter++ tests which encoders actually work on your PC with a tiny test encode (under a second, cached until the driver or GPU changes), shows them in Settings → Performance, and picks the best one. The quality slider is mapped so that a GPU encode looks the same as the CPU encode the original would have made.

If the GPU can't decode the source (ProRes, some 4:2:2 files) or the encode fails, it retries with CPU decoding, then with the original CPU encoder. Mp4 / Mkv presets also get a **video codec** choice (H.264, HEVC, AV1). Details: [docs/HARDWARE-ENCODING.md](docs/HARDWARE-ENCODING.md).

### Videos and audio: stream copy instead of re-encoding

When a conversion only changes the container (MKV → MP4, MOV → MP4, MP4 → MKV, M4A → AAC...) and the preset asks for nothing else (no resize, rotation, lower quality or other codec), FileConverter++ **copies the video and audio streams** instead of re-encoding them: it takes seconds, and there is no quality loss.
- Audio the new container can't hold is re-encoded next to the copied video; text subtitles are converted (SRT → MP4 subtitles).
- Only video that plays everywhere is copied: 4:2:0, and 8-bit for H.264. Professional formats (4:2:2, 4:4:4, 10-bit H.264) are re-encoded so the result plays on any device.
- The result is checked (duration of every stream); if anything is wrong, the normal conversion runs instead.
- Converting a file to its own format (MP4 → To Mp4) still re-encodes it, since that is the only reason to do it.

Option: Settings → Performance → "Copy streams without re-encoding when possible". Details: [docs/REMUXING.md](docs/REMUXING.md).

### Images: libvips

JPEG, PNG, WebP, TIFF and AVIF images go through **libvips**, an image library built for speed: it decodes a photo at the reduced size directly when scaling down and processes it in a streaming pipeline. Output files are the same size and quality as with ImageMagick (AVIF 31 % smaller at the same quality). All other formats (RAW, HEIC, PSD, PDF, ICO, GIF...) and any libvips failure use ImageMagick as before. Option: Settings → Performance → "Image engine". Details: [docs/IMAGES.md](docs/IMAGES.md).

### Batches: one conversion per CPU core

The original converts 2 files at a time. FileConverter++ runs **one conversion per physical core** by default, with separate limits so that heavy jobs don't fight each other: GPU video encodes (4), CPU video encodes (cores ÷ 4, between 2 and 4), Office documents (2), and at most 2 large files at a time per hard disk drive. Jobs start as soon as they are ready instead of waiting for the whole batch to be prepared. Details: [docs/SCHEDULING.md](docs/SCHEDULING.md).

### Settings → Performance and diagnostics

![Settings → Performance](docs/images/settings-performance.png)

A new tab groups the new settings: hardware acceleration mode and the detected encoders (with a Re-detect button), stream copy, image engine and the simultaneous conversion limits. The diagnostics logs now record, for every file, the path it took (`remux`, `nvenc`, `cpu-fallback`, `vips`, `imagemagick-fallback`...) and how long it took, and a summary per batch. Details: [docs/SETTINGS.md](docs/SETTINGS.md).

### Fixes

- **Long video conversions could hang forever**: ffmpeg's progress output was never read, so ffmpeg blocked once the buffer was full (any re-encode longer than about 10 s on the test machine). Fixed.
- **Opus / WebM conversions reported as failed** because of a harmless ffmpeg 8 message. Fixed.
- **Two files of a batch with the same output name** (a.mp4 and a.mkv → To Mp3): the second conversion failed. Now named `a (2).mp3`.
- **PDF conversions needed the Visual C++ Redistributable**, installed separately by some other program: Ghostscript depends on it. It is now included.
- **French translation**: the hardware acceleration, preset folder, import / export, duplicate and clipboard texts were in English.
- The Apps list showed no install location.

### Security and bundled components

- **Magick.NET** (ImageMagick) updated from 14.10.2 to 14.17.2: 144 known security advisories fixed, most of them high severity, in the engine that opens any image you right-click.
- **Ghostscript** (PDF) updated from 10.02.1 to 10.08.0: fixes CVE-2024-29510, a sandbox escape through crafted PDF / EPS files exploited in the wild, and the later fixes.
- **Everything is included**: ffmpeg 8.0.1 and ffprobe, libvips 8.18.7, ImageMagick, Ghostscript and the Visual C++ runtime are installed with FileConverter++, with their licenses and source information. Nothing else needs to be installed (only Microsoft Office, for Word / Excel / PowerPoint conversions, as before).

## Install, upgrade, uninstall

1. Download `FileConverterPlusPlus-<version>-x64-setup.msi` from the [releases](https://github.com/SwatX18/FileConverterPlusPlus/releases/latest) and run it.
2. The installer is not code-signed: Windows shows "Unknown publisher" (and SmartScreen may ask you to confirm).
3. In Explorer, right-click files → **Show more options** (Windows 11) → **FileConverter++**.

- **Coming from File Converter?** Installing FileConverter++ replaces it and keeps your settings and custom presets (`%LOCALAPPDATA%\FileConverter`). The two can't be installed side by side.
- **Updates**: FileConverter++ checks this repository for new versions (Settings → Application → "Automatically check for updates").
- **Uninstall**: Settings → Apps → FileConverter++ → Uninstall.
- How to use the presets and customize them: File Converter's [wiki](https://github.com/Tichau/FileConverter/wiki) applies as is.
- Problems with FileConverter++: [report an issue here](https://github.com/SwatX18/FileConverterPlusPlus/issues).

## For developers

- Build: [docs/BUILD.md](docs/BUILD.md) (Visual Studio 2022, .NET Framework 4.8, WiX 5; one MSBuild command builds the app, the tests and the MSI). Executable: `FileConverterPlusPlus.exe`.
- Tests: 168 xUnit unit tests (`Tests/FileConverter.Tests`, in the solution) and 40 integration tests that convert a generated test corpus and check every output (`Tests/integration.ps1`): [Tests/README.md](Tests/README.md).
- Benchmarks: [bench/README.md](bench/README.md), results in [bench/REPORT.md](bench/REPORT.md).
- Architecture and the design of each change: [docs/](docs/) (ARCHITECTURE, HARDWARE-ENCODING, REMUXING, IMAGES, SCHEDULING, SETTINGS).
- All changes are listed in the [CHANGELOG](CHANGELOG.md). Development happens on the `fast-convert` branch.

## About the original project

FileConverter++ is based on [File Converter](https://github.com/Tichau/FileConverter) by Adrien Allard ([file-converter.io](https://file-converter.io/?from=readme.md)), which did all the hard work: the right-click integration, the presets, the conversions and the translations. The sections below come from its README.

## Donate (to File Converter's author)

File Converter is a personal open source project started in 2014. I have put hundreds of hours adding, refining and tuning File Converter with the goal of making the conversion and compression of files an easy task for everyone.

You can help me by [contributing to the project](https://github.com/Tichau/FileConverter/wiki#contribute), by [making a donation](https://www.paypal.com/donate/?cmd=_donations&business=3BDWQTYTTA3D8&item_name=File+Converter+Donations&currency_code=EUR&Z3JncnB0=) or just by [saying thanks](https://saythanks.io/to/Tichau) :).

## Troubleshooting

If you encounter any problem with FileConverter++, you can:

* See the already known problems in the [troubleshooting section of File Converter's documentation](https://github.com/Tichau/FileConverter/wiki/Troubleshooting).
* Or report an issue on the [FileConverter++ bug tracker](https://github.com/SwatX18/FileConverterPlusPlus/issues).

## Setup development environment

### Requirements

For FileConverter++ and its explorer extension:

* Visual Studio 2022

For the installer:

* [Wix 5](http://wixtoolset.org/) (will be installed by nuget)
  * [Community Visual Studio Extension](https://marketplace.visualstudio.com/items?itemName=FireGiant.FireGiantHeatWaveDev17)
* [Windows SDK Signing Tools for Desktop Apps](https://developer.microsoft.com/fr-fr/windows/downloads/windows-10-sdk)

## Thanks

Thanks to all the contributors of File Converter project.

### Localization

* Thanks to **Khidreal** and **hugok79** for the Portuguese localization.
* Thanks to **Marhc** for the Brazilian localization.
* Thanks to **Chachak** for the Spanish localization.
* Thanks to **Davide** for the Italian localization.
* Thanks to **nikotschierske** for the German localization.
* Thanks to **Snoopy1866** for the Simplified Chinese localization.
* Thanks to **MayaC0re** for the Turkish localization.
* Thanks to **vishveshjain** for the Hindi localization.
* Thanks to **Mahmoud0Sultan** for the Arabic localization.
* Thanks to **Sedimentary-Rock**, **NeKoOuO** and **PeterDaveHello** for the Traditional Chinese localization.
* Thanks to **CrisBalGreece** for the Greek localization.
* Thanks to **AshiVered** for the Hebrew localization.
* Thanks to **MrHero118** and **Mehrdad32** for the Persian localization.
* Thanks to **crnobog69** for the Serbian localizations.
* Thanks to **oogamiyuta** for the Japanese localization.
* Thanks to **AidyTheWeird** for the Czech localization.
* Thanks to **Alanimdeo** for the Korean localization.
* Thanks to **vrykolakas166** and **thaovd** for the Vietnamese localization.
* Thanks to **iliamak** for the Russian localization.
* Thanks to **itsmefdil** for the Indonesian localization.
* Thanks to **hamzaharoon1314** for the Urdu localization.
* Thanks to **Zyvrec7** and **stohlferenc** for the Hungarian localization.
* Thanks to **Maerek** and **MrPrince419** for the Polish localization.
* Thanks to **rkalitta** for the Swedish localization.

## Middlewares

File converter uses the following middlewares:

**ffmpeg** and **ffprobe** (v8.0.1) as file conversion and media analysis software.
Thanks to ffmpeg devs for this awesome open source file conversion tool. [Web site link](https://ffmpeg.org)

**ImageMagick** (Magick.NET v14.17.2) as image edition and conversion software.
Thanks to image magick devs for this awesome open source image edition software suite.  [Web site link](http://imagemagick.net)
And thanks to dlemstra for the C# wrapper of this software. [Github link](https://github.com/ImageMagick/ImageMagick)

**libvips** (v8.18.7, LGPL-2.1-or-later) as fast image conversion library for JPEG, PNG, WebP, TIFF and AVIF.
Thanks to John Cupitt, Kleis Auke Wolthuizen and the libvips contributors. [Web site link](https://www.libvips.org)
And thanks to Kleis Auke Wolthuizen for **NetVips** (v3.2.0, MIT), the C# binding of libvips. [GitHub link](https://github.com/kleisauke/net-vips)

**Ghostscript** (10.08.0, AGPL-3.0) as pdf edition software.
Thanks to ghostscript devs. [Download link](https://www.ghostscript.com/download/gsdnld.html)

**SharpShell** to easily create windows context menu extensions.
Thanks to Dave Kerr for his work on SharpShell. [GitHub link](https://github.com/dwmkerr/sharpshell)

**Ripper** and **yeti.mmedia** for CD Audio extraction.
Thanks to Idael Cardoso for his work on CD Audio ripper. [Code project link](https://www.codeproject.com/Articles/5458/C-Sharp-Ripper)

**Markdown.XAML** for markdown rendering in the wpf application.
Thanks to Bevan Arps for his work on Markdown.XAML. [GitHub link](https://github.com/theunrepentantgeek/Markdown.XAML)

**WpfAnimatedGif** for animated gif rendering in the wpf application.
Thanks to Thomas Levesque for his work on WpfAnimatedGif. [GitHub link](https://github.com/XamlAnimatedGif/WpfAnimatedGif)

## License

FileConverter++, like File Converter, is licensed under the GPL version 3 License (copyright Adrien Allard; FileConverter++ changes by SwatX18).
For more information check the LICENSE.md file in your installation folder or the [gnu website](https://www.gnu.org/licenses/gpl.html).

ffmpeg and ffprobe are the GPLv3 "essentials" build of [gyan.dev](https://www.gyan.dev/ffmpeg/builds/); their license and the location of their source are installed as `ffmpeg-LICENSE.txt` and `ffmpeg-README.md` (`Middleware/ffmpeg`).

Ghostscript is distributed under the AGPL-3.0 as the unmodified Artifex binaries; its license and the location of its source are installed as `ghostscript-LICENSE.txt` and `ghostscript-README.md` (`Middleware/gs`). It needs the Microsoft Visual C++ runtime, which is installed next to it (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`, redistributable under the Visual Studio license, `Middleware/vcruntime`), so File Converter doesn't depend on a separately installed Visual C++ Redistributable.

libvips is distributed under the LGPL-2.1-or-later as unmodified DLLs in the `libvips` folder of the installation, with its license, its source and the versions of every library (`Middleware/libvips`); it is loaded dynamically and can be replaced by another build of the same version. NetVips is under the MIT license.
