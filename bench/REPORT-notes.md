## Where the gains come from

| Change | Cases | Effect |
|---|---|---|
| Stream copy (Phase 2, `docs/REMUXING.md`) | mkv / mov to mp4 when the codecs fit | Sintel 888 s 1080p: 81.5 s to 2.3 s. Output keeps the source's bitrate, so it is usually larger than a re-encode at the preset's quality (Sintel 549 to 1119 MB). 4:2:2 / 4:4:4 sources and 10-bit H.264 are re-encoded to 4:2:0 instead, so that the output plays everywhere. |
| Hardware encoding (Phase 1, `docs/HARDWARE-ENCODING.md`) | every re-encode, e.g. same-format conversions, ProRes, scaling | 4K H.264: 20.2 s to 7.1 s at half the size (NVENC at the calibrated quality); Sintel mkv to mkv 80.9 s to 36.4 s. |
| libvips (Phase 3, `docs/IMAGES.md`) | JPEG, PNG, WebP, TIFF, AVIF | 1.2 to 2.7 times faster per image, same output size (AVIF 31 % smaller at the same SSIM). |
| Scheduler (Phase 4, `docs/SCHEDULING.md`) | batches | one job per physical core instead of 2: 200 JPEG to WebP 20.7 s to 3.6 s on top of libvips. |

## Cases that are not faster

- **Audio extraction from a video file** (`video_extract_mp3` +0.4 s, `video_iphone_mov_to_aac` +0.2 s). The source
  must be probed (ffprobe, about 0.3 s to start) to know whether its audio can be copied. Measured back to back with
  the baseline build (5 runs each): 1.11 s to 1.52 s and 0.95 s to 1.17 s; with stream copy disabled, 1.2 s. On a
  short file the copy saves nothing; on a long one it saves the whole encode (10 min of FLAC to AAC: 7.5 s).
- **Audio conversions** (flac / wav to mp3, flac, aac): unchanged, single-threaded encoders on one file.
- **RAW and HEIC images** (ImageMagick): unchanged within noise. The final run's 10.2 s vs 9.4 s for RAW is session
  noise: back to back with the baseline build, 9.1 s (final) vs 9.2 s (baseline). A batch of 5 RAW files is bound by
  the slowest file.
- **AVIF**: 2.3 times faster than the baseline, 8 % slower than Phase 3 alone (16 simultaneous AV1 encodes add no
  throughput over 2 large ones).

## Size changes

Sizes differ where the path changed:
- Stream copies keep the source bitrate, so they are larger than a re-encode (Sintel, the flac/subs file).
- NVENC re-encodes of the synthetic 1080p and 4K clips are 52-54 % smaller at equal quality: the clips have heavy
  artificial grain that x264 spends bits on (1080p: x264 49.3 MB at VMAF 97.3, NVENC 23.0 MB at VMAF 97.5). On real
  content (Sintel) NVENC is 15 % smaller. See `docs/HARDWARE-ENCODING.md`.
- Scale 720p is 28 % larger (NVENC instead of x264 at the same quality setting).
- Images: unchanged, except AVIF (31 % smaller).
