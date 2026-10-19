# Parallelism and scheduling (Phase 4)

`Services/ConversionService.cs` runs the jobs of a batch. Before this phase it prepared every job serially (output
paths, ffmpeg arguments and, since Phase 2, an ffprobe launch per video), then started at most one job every ~70 ms
(polling loop with `Sleep(20)` + `Sleep(50)`), under a single limit of 2 simultaneous conversions.

## Now

- **Preparation thread**: prepares the jobs in order (same serial semantics as before) while the queue thread starts
  each prepared job as soon as the limits allow. A batch of videos no longer waits for every file to be probed.
- **Event-driven queue**: the queue thread sleeps until a job is prepared or finishes (`AutoResetEvent`, 500 ms
  safety timeout), then starts every job it can, in list order.
- **Lanes** (`ConversionJob.Lane`, `ConversionJobs/ConversionLane.cs`), each with its own limit inside the total:

| Lane | Jobs | Limit |
|---|---|---|
| Light | images, audio, stream copies (remux), GIF/ICO, CD extraction | total |
| CpuVideo | software video encodes (x264, x265, VP9, Theora, MPEG-4) | `MaximumNumberOfSimultaneousCpuVideoConversions`, Auto = physical cores / 4, within 2..4 (4 here) |
| Gpu | video jobs whose first attempt is a hardware encode | `MaximumNumberOfSimultaneousGpuConversions` (4) |
| Documents | Word / Excel / PowerPoint automation | 2 (the previous global default) |

  An ffmpeg job's lane comes from its first attempt (remux → Light, NVENC/AMF/QSV → Gpu, other video → CpuVideo). A
  GPU job that falls back to the CPU keeps its GPU slot. The CD-drive rule (one extraction at a time) is unchanged.
- **Total**: `MaximumNumberOfSimultaneousConversions`; **0 = Auto = one job per physical core** (16 on the test
  machine, WMI `Win32_Processor.NumberOfCores`), the new default (see Settings).
- **Hard disk drives**: a job whose input is ≥ 100 MB and that reads or writes a hard disk drive (WMI
  `MSFT_PhysicalDisk.MediaType` = 3, mapped to drive letters with `MSFT_Partition`) counts against a limit of
  **2 such jobs per disk**. Smaller files are not limited (see measurements).
- **Thread budget** (`ConversionJob.ThreadBudget`): when a job starts, it gets logical processors ÷ the number of jobs
  expected to run alongside it in its lane (running + prepared and waiting + not yet prepared, capped by the lane and
  hard disk limits); 0 (no limit) for a job alone. Applied to:
  - libvips `Concurrency` and ImageMagick's thread limit (both process-wide: the jobs of a batch share one budget);
  - `-threads N` (encoder option) for software video encodes, including the CPU fallback of a GPU job.
- **Diagnostics**: `Conversion limits: total 16, GPU 4, CPU video 4, documents 2, 2 per hard disk (16 cores, 32 threads).`
  and for each job `Start job 5/8 (Gpu, 8 threads), running: 4 (Gpu: 4): <file>`.

## Settings

| Setting | Before | Now |
|---|---|---|
| `MaximumNumberOfSimultaneousConversions` | 2 | **0 = Auto** (one per physical core). Settings v6 migration: the old default 2, written to every settings file, becomes Auto; other values are kept. The slider goes from 0 ("Auto (16)") to the number of logical processors. |
| `MaximumNumberOfSimultaneousGpuConversions` | — (Phase 1, default 4) | GPU lane limit (Settings → Performance since Phase 5) |
| `MaximumNumberOfSimultaneousCpuVideoConversions` | — | Phase 5: CpuVideo lane limit, **0 = Auto** (physical cores / 4, within 2..4). A missing element means Auto. |

Verified: settings v5 with 2 → total 16; v5 with 6 → 6; v6 with 2 → 2.

## Measurements (`bench/try-case.ps1`, test machine)

Lanes, 8 × ProRes 1080p → To Mp4 (`-WatchProcesses` counts ffmpeg processes):

| Mode | Lane | Peak ffmpeg processes | Time |
|---|---|---:|---:|
| Auto (NVENC) | Gpu | 4 | 7.4 s |
| Off (x264) | CpuVideo | 4, `-threads 8` each | 26.1 s |

Two jobs alone get `-threads 16` (32 threads / 2).

Hard disk limit (F:, Seagate ST4000DM004, 7200 rpm; inputs copied there, outputs written there):

| Batch | 2 jobs per disk | No disk limit |
|---|---:|---:|
| 16 × 24 MP JPEG → To Png (768 MB written) | 22.3 s | **3.0 s** |
| 4 × Sintel 1.1 GB → To Mp4 (remux, 4.5 GB written) | **92.6 s** | 107.8 s |

PLAN.md asked for a limit of 2 per hard disk. Measured, it only helps when large files stream to the disk; with
files that fit the Windows file cache (64 GB RAM here) it serializes work for nothing (7× slower). Hence the 100 MB
input threshold. (With the threshold, the 16 JPEGs on the hard disk: 3.5 s.)

Scheduler alone (200 × 24 MP JPEG → Scale 25%/To Webp, libvips):

| Total limit | Phase 3 (polling loop) | Phase 4 |
|---|---:|---:|
| 2 | 20.7 s | 17.5 s |
| Auto (16) | — | **3.3 s** |

## Benchmark (`bench/phase4.csv`, all cases, median of 3, commit 9b7513a, Auto = 16)

| Case | Baseline | Phase 3 | Phase 4 |
|---|---:|---:|---:|
| 200 × 24 MP JPEG → Scale 25%/To Webp | 55.8 s | 20.7 s | **3.4 s** (16×) |
| 10 WebP → To Png | 22.4 s | 18.1 s | **5.2 s** |
| 20 × 24 MP JPEG → To Jpg | 3.6 s | 2.5 s | **1.1 s** |
| 10 PNG → To Jpg | 1.8 s | 1.5 s | **0.8 s** |
| 5 TIFF → To Jpg | 1.9 s | 1.4 s | **0.8 s** |
| 4 HEIC → To Jpg (ImageMagick) | 3.6 s | 3.7 s | **2.8 s** |
| 20 × 24 MP JPEG → To Avif | 24.9 s | 9.6 s | 10.4 s |
| 5 RAW → To Jpg (ImageMagick) | 9.4 s | 10.3 s | 10.6 s |
| single video / audio files | | | unchanged (± 0.1 s) |

Every case succeeds (25/25). Single files can't gain from parallelism (one job gets every thread, as before).
AVIF is 8 % slower with 16 jobs × 2 threads than with 2 jobs × 16 threads: the AV1 encoder of each image has
sequential phases and a large memory footprint, so many simultaneous encodes don't add throughput. RAW (5 files,
ImageMagick) gains nothing: the batch is dominated by its slowest file.

## Not changed / notes

- Output-name collisions inside a batch are a pre-existing issue: `PathHelpers.GenerateUniquePath` only checks files
  that exist, so `a.mkv` and `a.avi` → To Mp4 in one batch both target `a.mp4` (the second fails, ffmpeg runs with
  `-n`). Unchanged (the preparation is still serial).
- Office jobs keep a limit of 2 (PLAN puts documents with images at core count): each job drives a Word / Excel /
  PowerPoint instance through COM, and running 16 of them at once was never tested upstream.
- The state change of a job still does a synchronous `Dispatcher.Invoke` to the UI thread (§4 of ARCHITECTURE.md);
  not a bottleneck at these job rates.
