# Plan: "Fast" File Converter fork

> Implementation plan for Claude Code. Put it at the root of the fork, then: "Read PLAN.md and execute it phase by phase."

## Goal

Fork [Tichau/FileConverter](https://github.com/Tichau/FileConverter) (C#, WPF, SharpShell shell extension, WiX 5 installer) and make it significantly faster without changing the user experience (right-click → "Convert to…"). Three workstreams:

1. **Video**: automatic GPU detection and hardware encoding by default (NVENC > AMF > Quick Sync > CPU).
2. **Remuxing**: stream copy without re-encoding (`-c copy`) whenever the target container accepts the source codecs.
3. **Images**: libvips (via NetVips) instead of ImageMagick for common formats, with fallback to ImageMagick for everything else.

Primary target machine (for testing): Windows 11, Ryzen 9 7950X3D (16c/32t), **RTX 5070 Ti** (Blackwell: NVENC H.264 / HEVC / AV1) + AMD Radeon iGPU (AMF), 64 GB RAM.

## Constraints

- **License**: the project is GPL-3.0 and the fork stays GPL-3.0. Keep existing credits, and add NetVips (MIT) and libvips (LGPL) to the README's Middlewares section.
- Work on a branch derived from `integration` (the upstream default branch), so upstream updates can be rebased in.
- Do not break users' existing presets (`Settings.user.xml`): new options get default values and settings migration stays backward compatible.
- Every optimization must have an **automatic fallback** to the current code path on failure (no functional regressions).

---

## Phase 0: Setup and discovery

1. Fork the repo, clone it, create branch `fast-convert`.
2. Verify a command-line build works (`msbuild FileConverter.sln /p:Configuration=Release /p:Platform=x64`). Document any missing prerequisites (Visual Studio 2022 Build Tools, WiX 5 via NuGet, Windows SDK Signing Tools) in `docs/BUILD.md`.
3. Read and summarize in `docs/ARCHITECTURE.md`:
   - `Application/FileConverter/ConversionJobs/`: the `ConversionJob_*` hierarchy (especially `ConversionJob_FFMPEG.cs`, `ConversionJob_FFMPEG.Converters.cs`, `ConversionJob_ImageMagick.cs`) and how a job is selected from input/output types.
   - `Helpers.cs` and `SettingsViewModel.cs`: where hardware acceleration options live (AMF mode was added recently; use it as the model).
   - The job queue manager: how the number of simultaneous conversions is handled.
   - `Middleware/`: which binaries are bundled (check whether `ffprobe.exe` is present).
   - The targeted .NET framework (check compatibility with NetVips: .NET Framework ≥ 4.6.1 or .NET 6+).
4. **Create a benchmark corpus** in `bench/samples/` (not committed if large; provide a download/generation script): H.264 1080p and 4K videos, HEVC, MKV with subtitles, iPhone MOV, FLAC/WAV audio, 200 × 24 MP JPEGs, PNG, WebP, TIFF, HEIC, a few RAW files.
5. Write `bench/run-bench.ps1` that runs each representative conversion through File Converter's command line (or directly through the job classes) and records duration, output size and success into a CSV. **Benchmark the unmodified version before any change**: this is the baseline.

**Exit criteria**: the project builds; the baseline benchmark is saved in `bench/baseline.csv`.

---

## Phase 1: Automatic GPU detection and hardware encoding

### 1.1 Detection service (`HardwareEncoderDetector`)

- New file `Application/FileConverter/Hardware/HardwareEncoderDetector.cs`.
- Use a **reliable** detection method: do not rely on the GPU name. For each candidate encoder, run a tiny test encode:
  ```
  ffmpeg -hide_banner -loglevel error -f lavfi -i color=black:s=256x256:d=0.1 -frames:v 1 -c:v <encoder> -f null -
  ```
  Exit code 0 = encoder available.
- Encoders to test:
  - NVIDIA: `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`
  - AMD: `h264_amf`, `hevc_amf`, `av1_amf`
  - Intel: `h264_qsv`, `hevc_qsv`, `av1_qsv`
- Run the tests **in parallel**, with a timeout (5 s each).
- Cache the result in `%APPDATA%\FileConverter\hw-cache.json`, keyed by a hardware fingerprint (WMI `Win32_VideoController`: names + driver versions). Re-run detection only when the fingerprint changes, or when the user clicks "Re-detect" in settings.
- For each target codec (H.264, HEVC, AV1), expose the chosen encoder using the priority **NVENC > AMF > QSV > CPU**. On the test machine, NVENC (the RTX) must win over the iGPU's AMF.

### 1.2 Integration into ffmpeg command generation

- In `ConversionJob_FFMPEG.Converters.cs`, add an **"Auto"** acceleration mode (the new default) alongside the existing modes (Off / CUDA / AMF…).
- In Auto mode, pick the encoder via `HardwareEncoderDetector`. Mapping of existing settings (quality slider and encoding speed):
  - **NVENC**: `-preset p1…p7` (speed), `-rc vbr -cq <N>` (quality), `-b:v 0`.
  - **AMF**: reuse the existing mapping (`-usage transcoding -quality … -qp_i/-qp_p/-qp_b`).
  - **QSV**: `-preset veryfast…veryslow`, `-global_quality <N>`.
  - Calibrate quality values so that, at equal settings, output size stays close to the current CPU encode (± 20%); document the mapping table in code.
- **Hardware decoding**: add `-hwaccel auto` on input when a hardware encoder is used and no incompatible CPU filter is needed. If filters (resize, rotate) are required with NVENC, use `scale_cuda`; otherwise fall back to CPU decoding.
- New output formats offered when the encoder exists: MP4/MKV in **HEVC** and **AV1** (extra presets, disabled by default, enableable in settings).

### 1.3 Fallback and limits

- If ffmpeg fails with a hardware encoder (non-zero exit, or errors like "No capable devices found", "OpenEncodeSessionEx failed"), **automatically re-run the job on CPU** and note it in the job diagnostics.
- Consumer GPUs limit the number of concurrent encode sessions. Add a dedicated `SemaphoreSlim` for GPU jobs (default: 4, configurable), separate from the global simultaneous-conversion limit.
- Explicitly test 10-bit and 4:2:2 inputs (add `-pix_fmt` when the encoder does not accept them).

**Exit criteria**: on the corpus, video conversions go through NVENC; no job fails that previously succeeded; time savings are recorded in the CSV.

---

## Phase 2: Smart remuxing (no re-encoding)

### 2.1 Source analysis

- Use `ffprobe` (add it to `Middleware/` if missing, same version as ffmpeg):
  ```
  ffprobe -v error -show_streams -show_format -of json <file>
  ```
- New `MediaProbe.cs` returning the list of streams (type, codec, pix_fmt, profile, bitrate, language), the container and the duration.

### 2.2 "Copy or re-encode" decision

- New `RemuxPlanner.cs`, deciding **per stream**:
  - A stream is **copied** (`-c:v copy` / `-c:a copy` / `-c:s copy`) if:
    1. the output container accepts this codec (compatibility table below), **and**
    2. the preset requests no transformation for this stream type (no resize, rotation, non-keyframe trim, frame rate or sample rate change, and no explicit target quality lower than the source).
  - Otherwise the stream is re-encoded (with the Phase 1 encoder for video).
  - Partial remuxing is allowed: copied video with re-encoded audio, e.g. MKV (H.264 + FLAC) → MP4 (copied H.264 + AAC).
- Initial compatibility table (in an easily extendable data file, `remux-compat.json`):

  | Container | Copyable video | Copyable audio | Subtitles |
  |---|---|---|---|
  | MP4 / M4V / MOV | h264, hevc, av1, mpeg4 | aac, mp3, ac3, eac3, alac, opus | mov_text only (otherwise convert or drop) |
  | MKV | almost everything | almost everything | srt, ass, pgs… |
  | WebM | vp8, vp9, av1 | opus, vorbis | webvtt |
  | M4A | — | aac, alac | — |
  | MP3 | — | mp3 | — |
  | OGG | — | vorbis, opus, flac | — |

- For MP4 with copied HEVC: add `-tag:v hvc1` (playback on Windows/Apple). Always add `-map 0`, then exclude incompatible streams, and `-movflags +faststart` for MP4.
- Settings option: "Copy streams without re-encoding when possible" (enabled by default), plus a "Remux" label in the in-progress conversion list so the user understands why it is instant.

### 2.3 Fallback

- If copying fails (muxing error, invalid timestamps), automatically re-run with full re-encoding.
- Validate output: output duration ≈ source duration (± 0.5 s) via ffprobe; otherwise re-encode.

**Exit criteria**: MKV H.264 → MP4 in seconds instead of minutes, iPhone MOV → MP4 losslessly, M4A audio extraction via stream copy.

---

## Phase 3: libvips for images

### 3.1 Integration

- Add the NuGet packages `NetVips` and `NetVips.Native.win-x64` (and `win-x86` if the x86 build is kept).
- Make sure the native DLLs are shipped by the WiX installer (add them to the middleware component).
- Initialize libvips once at startup: `NetVips.Cache.Max = 0` (no operation cache for batch processing), and set `NetVips.Concurrency` to avoid oversubscription when several jobs run in parallel (e.g. `max(1, coreCount / simultaneousJobs)`).

### 3.2 New `ConversionJob_Vips`

- Implements the same interface as `ConversionJob_ImageMagick`: progress, cancellation, preset options (quality, resize, rotation, upscale mode).
- Use libvips' fast APIs:
  - resizing via `Image.Thumbnail(...)` (shrink-on-load for JPEG/WebP, much faster than load-then-shrink);
  - sequential access (`access: Enums.Access.Sequential`) when no random-access operation is needed;
  - automatic EXIF rotation (`autorotate`), keeping or stripping metadata according to the preset.
- Output option mapping: JPEG `Q` (+ `optimize_coding`, `strip` per preset), PNG `compression` / `palette`, WebP `Q` / `lossless` / `effort`, AVIF/HEIF `Q` / `effort` (low effort by default for speed), TIFF `compression`.

### 3.3 Routing and fallback

- At startup, query libvips for the formats actually supported by the native build (available loaders/savers) instead of hardcoding them.
- **Routing**: libvips for jpg/jpeg/jfif, png, webp, tif/tiff, gif (static), avif, and heic **only if** the decoder is present in the build. ImageMagick remains in use for: RAW (arw, cr2, dng, nef, raf), dds, ico, psd, svg, pdf→image, animated GIFs, and any format not reported by libvips.
- If the libvips job throws, automatically re-run with ImageMagick and log it in diagnostics.
- Settings option: image engine "Auto (libvips, then ImageMagick)" / "ImageMagick only".

**Exit criteria**: a batch of 200 × 24 MP JPEGs → resized WebP is much faster than the baseline, visually identical output (SSIM ≥ 0.98 on a sample via `ffmpeg -lavfi ssim` or a Python script), no format lost.

---

## Phase 4: Parallelism and scheduling

- Default number of simultaneous conversions: number of physical cores (16 on the test machine), instead of a low fixed value if that is the current behavior.
- Three independent queues with their own limits: **GPU** (Phase 1 semaphore), **CPU video** (software encoding: 2 to 4 jobs, since x264/x265 are already multithreaded) and **images/audio/documents** (core count). A mixed batch then saturates GPU and CPU at the same time.
- For CPU ffmpeg jobs running in parallel, pass `-threads` = cores / simultaneous jobs to avoid contention.
- I/O: if source and destination are on a hard disk drive (the Seagate on the test machine), limit to 2 simultaneous jobs on that disk to avoid random-access performance collapse (SSD/HDD detection via WMI `MSFT_PhysicalDisk.MediaType`).

---

## Phase 5: Settings, UI and diagnostics

- Settings page: a "Performance" section with acceleration mode (Auto by default), detected encoder shown (e.g. "NVIDIA NVENC – RTX 5070 Ti"), "Re-detect" button, remux option, image engine, job limits (GPU / CPU).
- Localization: add the new strings in English **and** French to the existing resource files (other languages fall back to English).
- Diagnostics: each job logs the chosen path ("remux", "nvenc", "cpu-fallback", "vips", "imagemagick-fallback") and its duration, to ease debugging.

---

## Phase 6: Tests, final benchmark and delivery

1. **Unit tests** (xUnit or NUnit test project, create one if none exists):
   - `RemuxPlanner`: case table (source codec × target container × preset → copy / re-encode per stream).
   - Quality → NVENC/AMF/QSV parameter mapping.
   - Image routing (format → engine).
   - `HardwareEncoderDetector` with a mocked process runner (success, failure, timeout).
2. **Integration tests** on the corpus: every conversion produces a readable file (ffprobe without errors, correct duration, expected dimensions).
3. **Final benchmark**: re-run `bench/run-bench.ps1` → `bench/results.csv`, and generate `bench/REPORT.md` comparing against the baseline (time, size, speedup ×).
4. Build the x64 MSI installer, install it on the test machine, then check the context menu on a video file, a batch of images and an audio file.
5. Update the README ("About this fork" section, added middlewares, license) and the CHANGELOG.

## Priority order if time is short

1. Phase 2 (remuxing): the most dramatic gain for limited code.
2. Phase 1 (auto GPU): big gain on every video re-encode.
3. Phase 4 (parallelism): free gain on batches.
4. Phase 3 (libvips): big gain on image batches, but the heaviest integration (native DLLs, installer).

## Watch-outs

- ffmpeg and ffprobe versions must match, and the bundled ffmpeg build must include NVENC/AMF/QSV (true for the "full" Windows builds from gyan.dev or BtbN). Verify with `ffmpeg -encoders`.
- Hardware AV1 requires a recent GPU: only offer it when detection confirms it.
- The Windows native libvips build may not include HEIC (HEVC) decoding for patent reasons: rely on startup detection, not a fixed list.
- Keep commits small and focused (one phase = one series of commits), so some improvements can be proposed upstream as PRs.
