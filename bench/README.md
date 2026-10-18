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

## Baseline notes (`baseline.csv`, 2026-10-18)

- Machine: Ryzen 9 7950X3D, RTX 5070 Ti + Radeon iGPU, 64 GB. Settings defaults: 2 simultaneous conversions, hardware acceleration Off.
- The app code is the unmodified upstream `integration` (6c157a4). The `commit` column (43bc267) differs only by docs and bench files.
- `video_h264_4k_to_mp4` **hangs** on every run (3/3): this is the stdout `-progress pipe:1` deadlock described in
  `docs/ARCHITECTURE.md` §2. `video_h264_1080p_to_mp4` (about 10 s of encoding, right at the threshold) hung in 1 of 4 runs.
  Any longer video job hangs.
