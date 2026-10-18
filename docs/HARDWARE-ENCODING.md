# Hardware video encoding (Phase 1)

How MP4/MKV conversions pick an encoder, how they fall back, and how the quality settings were calibrated.

## Settings

| Setting | Where | Default |
|---|---|---|
| Hardware acceleration mode: `Auto`, `Off`, `CUDA`, `AMF` | Settings → Application (`Settings.HardwareAccelerationMode`) | **Auto**. Settings v5 migrates the old default `Off` to `Auto`; explicit CUDA/AMF are kept. |
| Video codec: H.264, HEVC, AV1 | Mp4/Mkv preset editor, next to "Video" (preset setting `VideoCodec`) | H.264. AV1 is only listed when a hardware AV1 encoder is detected. |
| Concurrent GPU encodes | `Settings.MaximumNumberOfSimultaneousGpuConversions` (no UI until Phase 5) | 4 |

`Auto` uses `IHardwareEncoderDetector.GetPreferredEncoder(codec)` (NVENC > AMF > QSV). `CUDA` and `AMF` force that vendor even
when detection didn't confirm it; a failure then falls back to the CPU like any other.

## Detection (`Hardware/HardwareEncoderDetector.cs`)

- For each of `{h264,hevc,av1}_{nvenc,amf,qsv}`, it runs `ffmpeg -f lavfi -i color=black:s=256x256:d=0.1 -frames:v 1 -c:v <enc> -f null -` in parallel, with a 5 s timeout each. Exit code 0 means available. It takes about 0.9 s on the test machine.
- The result is cached in `%LOCALAPPDATA%\FileConverter\hw-cache.json`, keyed by a fingerprint: `Win32_VideoController` name, driver version and PNP id for every adapter, plus the size and timestamp of `ffmpeg.exe`. A driver update, a new GPU or a new ffmpeg triggers re-detection. `Redetect()` ignores the cache (the "Re-detect" button comes in Phase 5).
- `RunConversions` starts detection in the background as soon as an Mp4/Mkv conversion is requested in Auto mode, so it overlaps with UI startup.

Test machine (RTX 5070 Ti, Radeon iGPU): `h264_nvenc, hevc_nvenc, av1_nvenc, h264_amf, hevc_amf`. `av1_amf` and all QSV fail, as expected.

## Encode attempts and fallback

Each Mp4/Mkv conversion is an ordered list of attempts (`BuildVideoEncodeAttempts` in `ConversionJob_FFMPEG.Converters.cs`).
An attempt fails on a non-zero ffmpeg exit code or an ffmpeg error line. The partial output is then deleted and the next attempt
runs. The job only fails if the last attempt (CPU) fails. Every attempt and fallback is logged (`Encoding path: …`,
`Encoding path 'nvenc' failed (…). Falling back to …`), and the path that succeeded is exposed as `ConversionJob_FFMPEG.EncodingPath`.

| Selected encoder | Attempts, in order |
|---|---|
| NVENC | `nvenc`: `-hwaccel cuda -hwaccel_output_format cuda`, `scale_cuda=…:format=yuv420p` (skipped for rotation presets) → `nvenc-cpu-filters`: `-hwaccel cuda`, CPU filters → `cpu-fallback` |
| AMF / QSV | `amf`/`qsv`: `-hwaccel auto` → `amf-cpu-decode`/`qsv-cpu-decode`: CPU decode → `cpu-fallback` |
| none / Off | `cpu` |

Why two hardware attempts:
- The full CUDA pipeline fails at filter initialization when NVDEC can't decode the source (ProRes) or `scale_cuda` can't convert its pixel format (4:2:2). This failure is immediate.
- AMF fails to initialize when the decoder ran on another adapter ("Failed to create derived AMF device context"): on an NVIDIA + AMD machine, `-hwaccel auto` decodes on the NVIDIA card. This failure is also immediate.

GPU attempts hold a `GpuSessionLimiter` slot. With 6 simultaneous conversions allowed and 2 GPU sessions, at most 2 ffmpeg
processes ran at once (checked with `bench/try-case.ps1 -WatchProcesses`).

### Pixel format and 10-bit

H.264 output is always 8-bit 4:2:0 (`yuv420p`), as before: 10-bit H.264 barely plays anywhere. For **HEVC and AV1**, the source
is probed with ffprobe (`Media/MediaProbe`), and a 10-bit source is encoded as 10-bit 4:2:0: `p010le` for NVENC/AMF/QSV
and `scale_cuda`, `yuv420p10le` for libx265/libaom. HDR color metadata is preserved (checked with a BT.2020/HLG source on
HEVC NVENC, AV1 NVENC and libx265). An 8-bit source stays 8-bit, and so does everything when ffprobe is missing.

### Target resolution

Video presets (Avi, Mkv, Mp4, Ogv, Webm) have a `VideoResolution` setting: 0 uses the scale factor, otherwise the
**shorter side** is resized to that size, keeping the aspect ratio (`scale=w='if(gte(iw,ih),-2,720)':h='if(gte(iw,ih),720,-2)'`,
or `scale_cuda` on the GPU path). The default "Scale 720p/1080p" presets use it now. They used to be ffmpeg custom commands
(`scale=1280:720`), which bypassed GPU encoding and stretched non-16:9 videos:

| Source | Old "Scale 720p" | Now |
|---|---|---|
| 1920×1080 | 1280×720 | 1280×720 (`nvenc`) |
| Sintel 1920×818 (2.35:1) | 1280×720, stretched | 1690×720 |
| Portrait 1080×1920 | 1280×720, distorted | 720×1280 |

### Verified paths (`bench/try-case.ps1`, test machine)

| Input / setting | Path taken | Result |
|---|---|---|
| H.264 1080p, Auto | `nvenc` | ok |
| ProRes 422 10-bit (no NVDEC) | `nvenc` fails → `nvenc-cpu-filters` | ok |
| H.264 4:2:2 10-bit | `nvenc` fails → `nvenc-cpu-filters` | ok |
| iPhone HEVC Main10 4K | `nvenc` | ok, 8-bit H.264 |
| "Rotate left" preset | `nvenc-cpu-filters` (no `scale_cuda` attempt) | ok, 1080×1920 |
| Codec HEVC → MP4 / AV1 → MKV | `nvenc` | ok, `hevc` (hvc1) / `av1` |
| Explicit AMF, H.264 and HEVC | `amf` fails → `amf-cpu-decode` | ok |
| Explicit AMF + AV1 (no `av1_amf`) | `amf` fails → `cpu-fallback` (libaom) | ok |
| Off | `cpu` | ok |
| Settings v4 with `Off` | migrated to Auto → `nvenc` | ok |
| Settings v5 with `Off` | `cpu` | ok |

## Quality calibration

The preset quality slider maps to a CPU CRF (`crf = 51 − quality`; the default "To Mp4" is 30, so CRF 21). Each hardware encoder gets the
parameter that produces the **same file size as the CPU encoder at that CRF** (PLAN target: ±20 %). Measured on three 40 s
excerpts of Sintel 1080p (real content), CRF 18/21/24/28, video only, ffmpeg 8.0.1:

| Encoder | Parameter | Reference | Size vs reference |
|---|---|---|---|
| h264_nvenc p4 `-tune hq` | `-rc vbr -cq crf+4 -b:v 0` | libx264 medium | −5 % … +5 % |
| hevc_nvenc | `-rc vbr -cq crf+5 -b:v 0` | libx265 medium | −5 % … +6 % |
| h264_amf | `-rc cqp -qp_i/-qp_p crf+4, -qp_b +2` | libx264 medium | −3 % … +5 % |
| hevc_amf | `-rc cqp -qp_i/-qp_p crf+4` | libx265 medium | −5 % … −0 % |

Quality at equal size: at CRF 21, x264 scores VMAF 96.2 (17.7 MB on one excerpt) and NVENC cq 25 scores 97.2 (17.9 MB).

Codec choices, equal VMAF to x264 at the same setting:
- **libx265**: same CRF. At CRF 21, VMAF 96.3 at **54 %** of the x264 size.
- **av1_nvenc**: `cq = crf + 13`. At CRF 21 (cq 34), VMAF 96.4 at **42 %** of the x264 size.
- **libaom-av1** (CPU fallback for AV1): `crf + 4`, estimated from the VMAF slope. About 3× slower than real time at `cpu-used 6`, which is why AV1 is only offered with a hardware encoder.

Not calibrated (no hardware on the test machine): `av1_amf` (q-index scale 0–255, mapped as 4 × the AV1 cq) and all QSV
encoders (`-global_quality crf+4`, by analogy). A wrong mapping changes the file size, not the success of the conversion.

For reference, the previous mappings: upstream `CUDA` (`-rc constqp -qp crf`) happened to match the CPU size (+0 %), so the
switch to `vbr`/`cq` keeps sizes for existing CUDA users. Upstream `AMF` (`-qp crf`) produced files **72 % larger** than the CPU encode.

## Benchmark (`bench/phase1.csv` vs `bench/baseline.csv`)

Default settings (Auto, 2 simultaneous conversions), wall time including about 0.8 s of app startup, warm hardware cache:

| Case | Baseline (CPU) | Phase 1 | Speedup | Size |
|---|---|---|---|---|
| H.264 1080p 60 s → MP4 | 10.7 s | 4.3 s | 2.5× | −54 % (synthetic noise, see below) |
| H.264 4K 30 s → MP4 | 20.2 s | 7.0 s | 2.9× | −52 % |
| **Sintel 14:48 1080p MKV → MP4** | **81.5 s** | **36.4 s** | **2.2×** | **−15 %** |
| HEVC MKV → MP4 | 6.9 s | 4.2 s | 1.6× | +1 % |
| MKV H.264 + FLAC + subs → MP4 | 10.6 s | 4.2 s | 2.5× | −54 % |
| iPhone HEVC 10-bit 4K → MP4 | 8.9 s | 5.0 s | 1.8× | +13 % |
| ProRes 422 → MP4 (fallback `nvenc-cpu-filters`) | 3.9 s | 2.4 s | 1.6× | −26 % |
| H.264 4:2:2 10-bit → MP4 (fallback `nvenc-cpu-filters`) | 5.8 s | 3.2 s | 1.8× | −27 % |
| Scale 720p → MP4 | 5.4 s | **2.7 s** | 2.0× | regular preset since the target-resolution change (`bench/phase1-fixes.csv`, median of 3); `phase1.csv` still shows the old custom-command preset (5.4 s, CPU) |

All 25 cases pass. Audio and image cases are unchanged by Phase 1.

The size differences on the synthetic clips (heavy temporal noise) are not a quality loss. On the 1080p clip, x264 CRF 21 gives
49.3 MB at VMAF 97.3 and NVENC cq 25 gives 23.0 MB at VMAF 97.5: x264 spends its extra bits reproducing the artificial grain.
On real content (Sintel) the difference is −15 %, within the ±20 % target.

Remaining bottlenecks: about 0.8 s of app startup per invocation, and for very fast jobs the CPU AAC audio encode.
The first conversion after a hardware or driver change also pays for detection (about 0.9 s) and CUDA initialization.
