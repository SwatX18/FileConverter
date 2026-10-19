# Smart remuxing (Phase 2)

When the output container accepts the source's streams and the preset asks for no change, FileConverter++ copies the
streams (`-c copy`) instead of re-encoding them. The conversion is then limited by disk speed (a 15-minute 1080p film in
2.4 s) and lossless. If anything goes wrong, the normal conversion runs instead.

## Setting

| Setting | Default | Notes |
|---|---|---|
| `CopyStreamsWhenPossible` | `true` | Settings → Performance → "Copy streams without re-encoding when possible". A missing element (settings from older versions) means `true`; no migration needed. |

While a remux runs, the conversion list shows **"Remux (no re-encoding)"** instead of "Conversion". The diagnostics log
has a `Remux plan: …` line listing each stream's decision, or `Remux: no, <reason>.`

## Pieces

- `Media/MediaProbe.cs`: `ffprobe -show_streams -show_format -of json` → `MediaInfo` (streams with codec, pixel format,
  bitrate, language, duration, cover-art flag; container and duration). The job probes its input once and also uses the
  result for the 10-bit decision (Phase 1).
- `Media/remux-compat.json` (embedded resource) + `Media/RemuxCompatibility.cs`: which codecs each output container
  accepts as a copy. Extend the JSON to support more codecs.
- `Media/RemuxPlanner.cs`: the per-stream decision (below), as a pure function of the preset, the input path and the
  probe result, so it can be unit-tested (Phase 6).
- `ConversionJob_FFMPEG.AddRemuxAttempt`: turns a plan into an ffmpeg command and puts it **first** in the job's attempt
  list. The existing attempts (GPU, then CPU for video; the single ffmpeg command for audio) become its fallback.

## Compatibility table

| Output | Copyable video | Copyable audio | Subtitles |
|---|---|---|---|
| Mp4 | h264, hevc, av1, mpeg4 | aac, mp3, ac3, eac3, alac, opus | mov_text copied; other text subtitles converted to mov_text; bitmap (PGS, DVD) dropped |
| Mkv | any | any | srt, ass, ssa, webvtt, PGS, DVD, DVB copied; mov_text converted to srt |
| Webm | vp8, vp9, av1 | opus, vorbis | webvtt copied; other text converted to webvtt; bitmap dropped |
| Ogv | theora | vorbis, opus | dropped |
| Aac (ADTS) | — | aac | — |
| Mp3 | — | mp3 (+ jpeg/png cover art) | — |
| Ogg | — | vorbis, opus | — |

Avi, Flac, Wav, Gif and image outputs are never remuxed. **Flac is deliberately absent**: a flac stream copied out of
another container (e.g. Matroska) keeps no total sample count, so the file has no duration and can't be seeked
(output validation caught it). Re-encoding flac is fast and lossless anyway.

## Cost of probing

Every launch of the bundled ffmpeg/ffprobe (99 MB static builds) costs about 0.32 s on the test machine. To keep that
off jobs that can't be remuxed, only inputs whose extension is listed in `sourceExtensions` (mp4, m4v, m4a, m4b, mov,
3gp, 3g2, mkv, mka, webm, avi, ogg, oga, ogv, opus, ts, mts, m2ts, flv) are probed; wav, flac, mp3… go straight to the
normal conversion. Everything else that doesn't need the streams is checked before probing too (same format,
resize, rotation, lower quality: `RemuxPlanner.GetBlockerBeforeProbe`). Single-stream outputs are validated with ffmpeg's own report instead of a second ffprobe, for the
same reason (and because ADTS `.aac` files store no duration: ffprobe only estimates it from the bitrate). An audio
extraction therefore costs one ffprobe launch more than before (a 20 s track: about +0.3 s, but lossless); a video
remux costs two, which is paid back many times over.

## Decision rules

A **video output** (Mp4, Mkv, Webm, Ogv) is remuxed only if its main video stream can be copied:

1. the container accepts the codec (table above);
2. the preset doesn't resize (`VideoScale` = 1, and `VideoResolution` = 0 or equal to the source's shorter side) or rotate;
3. the preset's `VideoQuality` is at least the default of a new preset of that type (Mp4/Mkv 28, Webm 40, Ogv 7). So
   "To Mp4" (30) remuxes and "To Mp4 (low quality)" (20) re-encodes: a lower quality is an explicit request for a
   smaller file;
4. Mp4/Mkv codec: **H.264, the default, means "most compatible": any codec the container accepts is kept** (an iPhone
   HEVC MOV becomes an HEVC MP4, as PLAN's "iPhone MOV → MP4 losslessly" requires). HEVC or AV1 are explicit requests:
   the source must already use that codec;
5. the video is 4:2:0 (`yuv420p`, `yuv420p10le`, `nv12`, `p010le`...), and 8-bit if it is H.264. The presets always
   write 4:2:0, 8-bit for H.264: a copied 4:2:2 / 4:4:4 stream, or 10-bit H.264 (High 10), would give a file most
   hardware decoders, browsers, phones and TVs can't play. HEVC and AV1 10-bit 4:2:0 (iPhone) are copied. An unknown
   pixel format doesn't block the copy.

Then, stream by stream:

- every audio stream is copied if the container accepts it and the preset's `AudioBitrate` is at least the default
  (Mp4/Mkv 128, Webm/Ogv 160), otherwise re-encoded with the preset's audio encoder (partial remux: e.g. MKV H.264 + FLAC
  → MP4 with the video copied and the FLAC converted to AAC). `EnableAudio = false` drops audio;
- subtitles per the table; attachments (fonts) kept in Mkv only;
- other video streams (cover art, extra angles) and data streams (timecode, camera metadata, which MP4 can't hold)
  are dropped. Chapters and metadata are kept (ffmpeg default).

An **audio output** (Aac, Mp3, Ogg) is remuxed if its first audio stream:

1. has a codec the container accepts;
2. keeps its channel count (`AudioChannelCount` = 0 or equal);
3. has a known bitrate at most 1.5× the preset's `AudioBitrate`. Re-encoding a lossy stream to a lower bitrate is only
   worth its generation loss if it saves at least a third of the size (the default "To Mp3" at 190 kb/s copies a
   256 kb/s mp3, re-encodes a 320 kb/s one). Matroska often stores no per-stream bitrate: for audio-only files the
   container's bitrate is used; unknown means re-encode.

**Same format is always re-encoded**: mp4/m4v → Mp4, mkv → Mkv, webm → Webm, ogv → Ogv, aac → Aac, mp3 → Mp3,
ogg/oga → Ogg. A remux would give back the same file; converting a file to its own format is a request to re-encode it
(smaller file, fixed stream).

## Command

```
ffmpeg -n -progress pipe:1 -i in.mkv -map 0:0 -c:0 copy -map 0:1 -c:1 aac -qscale:1 1.3 -map 0:2 -c:2 mov_text -movflags +faststart out.mp4
```

Streams are mapped one by one (`-map 0:<index>`) with per-output-stream codecs. Mp4 gets `-movflags +faststart` (and
`-tag:v hvc1` for HEVC, for Windows/Apple playback), Mp3 and Aac the same metadata options as their normal conversion.

## Fallback and validation

The remux is the first attempt of the job; the normal conversion runs if:

- ffmpeg fails (exit code, or an error line, as for every attempt); or
- **output validation** fails: the output must exist and last as long as the source streams it contains (± 0.5 s).
  The reference is the longest copied/encoded audio or video stream (an audio track extracted from a video can be
  shorter than the video), or the container's duration if a stream's is unknown. A single-stream output (audio
  extraction) is measured with the duration ffmpeg reports for what it muxed (`-progress` `out_time_us`); other outputs
  are probed with ffprobe, because with many streams ffmpeg's figure is not the file's duration (Sintel with 10
  subtitle tracks: 629.8 s reported for 888 s written).

The partial output is deleted before the fallback runs. The log shows
`Output validation failed: … Falling back to 'nvenc'`.

## Verified cases (`bench/try-case.ps1`, test machine)

| Input → preset | Result |
|---|---|
| MKV H.264 + FLAC + 2 SRT → To Mp4 | remux 2.9 s: video copied, FLAC → AAC, SRT → mov_text |
| Sintel MKV (H.264 + AC3 + 10 SRT, 888 s) → To Mp4 | remux 2.4 s, 1920×818 kept, all subtitles in mov_text, chapters |
| iPhone HEVC 10-bit MOV → To Mp4 / To Mkv | remux 1.5 s, yuv420p10le kept, hvc1 tag |
| HEVC MKV + AC3 → To Mp4 | remux 1.5 s, AC3 copied |
| H.264 MP4 → To Mkv | remux 1.7 s |
| H.264 in AVI → To Mp4 | remux 1.6 s |
| MP4 (AAC 160 kb/s) → To Aac | remux (1.2 MB copy instead of a 128 kb/s re-encode) |
| Opus WebM → To Ogg | remux; → To Mp3 re-encodes (Mp3 can't hold Opus) |
| MP3 in MKV → To Mp3 | remux with ID3 tags |
| ProRes MOV → To Mp4 | no (Mp4 can't contain prores) → GPU encode |
| H.264 4:2:2 10-bit MKV → To Mp4 | no (yuv422p10le is not 4:2:0) → GPU encode to 4:2:0 8-bit, 3.6 s, 32 MB |
| H.264 MP4 → To Mp4 | no (same format) → GPU encode |
| HEVC MKV → To Mp4 (low quality) / Scale 720p | no (lower quality / resize) → GPU encode |
| FLAC album → To Ogg | no (899 kb/s above 160) → Vorbis encode |
| Truncated MKV (header says 60 s, data 30 s) → To Mp4 | remux, validation fails (29.91 s instead of 60.01 s) → falls back to nvenc |
| Truncated MP3-in-MKV → To Mp3 | remux, validation fails (14.99 s instead of 30.03 s) → falls back to the mp3 encode |
| WAV → To Mp3 | not probed (`.wav files are not remuxed`), as fast as before |
| `CopyStreamsWhenPossible=false`, iPhone MOV → To Mp4 | GPU encode, as Phase 1 |

## Benchmark (`bench/phase2.csv` vs `bench/phase1.csv` and `bench/baseline.csv`)

Median of 3 runs, commit 030316c, Auto mode (NVENC), 2 simultaneous conversions.

| Case | Baseline | Phase 1 | Phase 2 | Path | Output MB (P1 → P2) |
|---|---:|---:|---:|---|---:|
| Sintel MKV (888 s) → To Mp4 | 81.5 s | 36.4 s | **2.3 s** | remux | 466 → 1119 |
| HEVC MKV → To Mp4 | 6.9 s | 4.2 s | **1.5 s** | remux | 62 → 51 |
| iPhone HEVC 10-bit MOV → To Mp4 | 9.0 s | 5.0 s | **1.5 s** | remux | 78 → 58 |
| H.264 MP4 → To Mkv | 10.7 s | 4.3 s | **1.8 s** | remux | 66 → 205 |
| MKV H.264 + FLAC + SRT → To Mp4 | 10.6 s | 4.2 s | **2.3 s** | remux (FLAC → AAC) | 66 → 204 |
| H.264 4:2:2 10-bit MKV → To Mp4 | 5.8 s | 3.2 s | **1.7 s** | remux² | 32 → 126 |
| H.264 MP4 → To Mp4 / 4K | 10.7 / 20.2 s | 4.3 / 7.0 s | 4.3 / 7.1 s | same format: nvenc | = |
| Sintel MKV → To Mkv | 80.9 s | 35.7 s | 35.8 s | same format: nvenc | = |
| ProRes MOV → To Mp4 | 3.9 s | 2.4 s | 2.8 s | probed, prores not copyable: nvenc | = |
| H.264 1080p → Scale 720p/To Mp4 | 5.4 s | 2.7 s¹ | 2.8 s | resize: nvenc | = |
| MP4 → To Mp3 | 1.1 s | 1.1 s | 1.5 s | probed, aac not copyable: lame | = |
| iPhone MOV → To Aac | 0.9 s | 0.9 s | 1.2 s | remux (lossless) | 0.2 → 0.3 |
| tiny wav / flac / wav audio cases | — | — | ± 0.1 s | not probed | = |

¹ `phase1-fixes.csv`.
² No longer remuxed since rule 5 (4:2:0 only): re-encoded to 4:2:0 8-bit, 3.6 s, 32 MB (`bench/results.csv`).

Notes:

- **A remux keeps the source's bitrate**, so its output is usually larger than a re-encode (Sintel: 1119 MB, the size
  of the source, instead of 466 MB). It is lossless and plays where the source played; a user who wants a smaller
  file uses a lower-quality preset ("To Mp4 (low quality)" re-encodes) or turns the setting off.
- The remaining slowdowns are the ffprobe launch (~0.35 s) on inputs that *could* hold copyable streams but don't
  (ProRes MOV → Mp4, AAC in MP4 → Mp3), and the lossless AAC extraction (+0.3 s on a 20 s clip).
- Exit criteria: MKV H.264 → MP4 in seconds instead of minutes (Sintel 81.5 s → 2.3 s); iPhone MOV → MP4 losslessly
  (HEVC 10-bit copied); audio extraction via stream copy (To Aac, see deviations).

## Pre-existing bug fixed on the way: Opus in WebM

ffmpeg 8 logs `Error parsing Opus packet header.` for Opus audio in Matroska/WebM files that decode correctly (exit code
0; the same stream in Ogg logs nothing). FileConverter++ treats any stderr line containing "Error" as a failure, so **any
conversion of a WebM/MKV with Opus audio failed**, remux or not (verified: Opus WebM → To Mp3 failed before the fix). The
message is now ignored like the existing transport-stream exception; a real failure still makes ffmpeg exit with an
error code, which is checked since Phase 0.5.

## Deviations from PLAN.md

- **M4A**: there is no M4a output type; "M4A audio extraction via stream copy" is implemented as **To Aac** (ADTS), see
  ARCHITECTURE.md §7.4.
- **"No explicit target quality lower than the source"** has no exact meaning for CRF-style presets. It is implemented
  as "quality slider at or above the default" for video and "source bitrate at most 1.5× the target" for audio-only
  outputs, plus the same-format rule.
- **Audio is copied only next to a copied video.** When the video must be re-encoded, the job runs exactly as before
  (audio re-encoded too): copying audio there saves no measurable time and would double the fallback paths.
- **`-map 0`** is replaced by explicit per-stream maps with exclusions, and only in the remux attempt; normal
  conversions keep ffmpeg's default stream selection (one video and one audio stream, see ARCHITECTURE.md §7.5).
  A remux therefore keeps more than a re-encode (all audio tracks, subtitles): a visible but intended difference.
- **`remux-compat.json` is embedded** in the executable rather than shipped as a loose file: same "extend the data file"
  workflow, nothing to install or lose.
- **Flac** is not remuxed (see above).
- **Single-stream outputs are validated with ffmpeg's muxed duration, not an ffprobe of the output** (see "Cost of
  probing"). Both validation paths catch truncated sources (verified: truncated MKV → Mp4, truncated MP3-in-MKV → Mp3).
