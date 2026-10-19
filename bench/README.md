# Benchmarks

```powershell
# 1. Build Release x64 (see docs/BUILD.md)
# 2. Create the corpus (~3 GB in bench/samples, gitignored; HEIC and RAW are downloaded)
powershell -ExecutionPolicy Bypass -File bench\make-corpus.ps1
# 3. Run all cases (writes bench/baseline.csv by default)
powershell -ExecutionPolicy Bypass -File bench\run-bench.ps1
#    later phases:
powershell -ExecutionPolicy Bypass -File bench\run-bench.ps1 -Output bench\results.csv -Label phase1
```

`run-bench.ps1` drives `FileConverter.exe --conversion-preset <name> --input-files <list>` (the same entry point as the
shell extension). It temporarily sets `HKCU\Software\FileConverter\Path` and `%LOCALAPPDATA%\FileConverter\Settings.user.xml`
(auto-exit on, everything else at the defaults) and restores both afterwards. See the header of the script for the
stall/hang detection and the CSV columns.

| Column | Meaning |
|---|---|
| `seconds` | Wall time launch → exit, including ~1–2 s app startup (`startup_overhead` case). For `hung`/`failed` runs: time until the last observed activity. |
| `status` | `ok`, `partial`, `failed` (app idle with an error), `hung` (ffmpeg child blocked), `timeout`, `skipped` |
| `outputs_ok` | Outputs that ffprobe can read (and, for single-input A/V cases, whose duration matches the source within 1 s) |
| `notes` | Output stream codecs, and the app's own `Fail:` messages from its diagnostics logs |

Sample sources: synthesized with the bundled ffmpeg (videos, audio, JPEG/PNG/WebP/TIFF); RAW from
[raw.pixls.us](https://raw.pixls.us) (CC0); HEIC from [nokiatech/heif](https://github.com/nokiatech/heif) sample
content (downloaded locally for benchmarking only, not redistributed).

## Baseline notes (2026-10-18)

Machine: Ryzen 9 7950X3D, RTX 5070 Ti + Radeon iGPU, 64 GB. Default settings: 2 simultaneous conversions, hardware acceleration Off.

- **`baseline.csv`** is the reference for speedups. It is upstream `integration` plus the ffmpeg stdout-deadlock fix
  (commit e3364fc), with no performance changes. Every case passes.
- **`baseline-v2.2-unfixed.csv`** is the untouched upstream build (6c157a4). `video_h264_4k_to_mp4` **hangs** on every
  run (3/3): this is the stdout `-progress pipe:1` deadlock described in `docs/ARCHITECTURE.md` §2.
  `video_h264_1080p_to_mp4` (about 10 s of encoding, right at the threshold) hung in 1 of 4 runs. Any longer video job hangs.
  The Sintel, ProRes and 4:2:2 cases were added after this run.
- Image batch timings vary between single runs (`img_jpg200_scale25_webp` once measured 39.4 s instead of ~56 s).
  The image rows of `baseline.csv` were therefore re-measured on the baseline build (e3364fc, separate worktree) with
  `-Repeat 3`, and record the median (e.g. 56.1 / 54.5 / 55.8 s → 55.8 s). Use `-Repeat 3` for image comparisons.
- **`phase1.csv`**: Phase 1 (GPU encoding, Auto mode). See `docs/HARDWARE-ENCODING.md` for the comparison.
- **`phase1-fixes.csv`**: cases changed by the post-Phase-1 fixes (Scale presets now regular presets on the GPU).
- **`phase2.csv`**: Phase 2 (remux), video and audio cases, `-Repeat 3`. See `docs/REMUXING.md` for the comparison.
  Cases that convert a file to its own format (`video_h264_1080p_to_mp4`, `video_sintel_mkv_to_mkv`) are re-encoded by
  design, so they measure the same path as Phase 1.
- **`phase3.csv`**: Phase 3 (libvips), image cases, `-Repeat 3`. See `docs/IMAGES.md`.
- **`phase4.csv`**: Phase 4 (scheduler, Auto = one job per physical core), all cases, `-Repeat 3`. See `docs/SCHEDULING.md`.
- **`image-engines.csv`**: `compare-engines.ps1` output: each image case converted with libvips and with ImageMagick
  only, with time, size, dimensions and SSIM against the source and between the two outputs.

`try-case.ps1` runs a single preset on one or more files with setting overrides and prints the encoding path taken
(useful for checking fallbacks), e.g.
`.\bench\try-case.ps1 -Preset "To Mp4" -InputFile bench\samples\video\prores422_1080p.mov -Settings "HardwareAccelerationMode=AMF"`.
