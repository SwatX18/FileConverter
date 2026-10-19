# Images with libvips (Phase 3)

JPEG, PNG, WebP, TIFF and AVIF images converted to Jpg, Png, Webp or Avif go through **libvips** (via NetVips) instead
of ImageMagick: the image is decoded at a fraction of its size when it is reduced (shrink-on-load), processed in a
streaming pipeline, and encoded with the same libraries (libjpeg, libpng, libwebp, aom). Every other case, and every
libvips failure, uses the existing ImageMagick conversion unchanged.

## Setting

| Setting | Default | Notes |
|---|---|---|
| `ImageEngine` | `Auto` | Settings → Performance → "Image engine": **Auto (libvips, then ImageMagick)** or **ImageMagick only**. A missing element (older settings files) means Auto. |

The diagnostics log shows the engine of each image: `Image engine: vips.`, `Image engine: imagemagick (<reason>).` or
`Image engine: imagemagick-fallback (libvips failed (<error>)).`, and once per run
`libvips 8.18.7 loaded, operations: …` (or why libvips is unavailable). libvips warnings go to the same log.

## Pieces

- `Middleware/libvips/`: the official shared "web" build of libvips 8.18.7 for Windows x64 (38 DLLs, ~23 MB,
  unmodified; source, checksum and licenses in its README). Copied to `libvips\` next to the executable and installed
  there (harvested by the WiX project, component group `Libvips`). NetVips 3.2.0 comes from NuGet.
- `Imaging/VipsEngine.cs`: loads libvips once, configures it, and answers "can libvips handle this output type / input
  extension" from the operations the loaded build actually reports (`jpegload`, `heifsave`…), not from a fixed list.
- `ConversionJobs/ConversionJob_Vips.cs`: derives from `ConversionJob_ImageMagick`. `Convert()` tries libvips and calls
  `base.Convert()` (the ImageMagick conversion) when libvips is not applicable or throws. `ConversionJobFactory` creates
  it for Avif/Jpg/Png/Webp outputs instead of `ConversionJob_ImageMagick`.

### Loading libvips on .NET Framework

The `NetVips.Native.win-x64` NuGet package was tried first and rejected: it only declares net6.0 (NU1202 on net48) and
contains a static `libvips-42.dll` with GLib linked in. NetVips for .NET Framework imports `libglib-2.0-0.dll` and
`libgobject-2.0-0.dll` **by name** and has no import resolver, so with the static build Windows loads whatever GLib is
on PATH; on the test machine that was Meld's, and the process crashed (access violation in `g_free`). The shared build
ships those DLLs, and `VipsEngine` loads `libvips\libvips-42.dll` by full path (`LoadLibraryEx`,
`LOAD_WITH_ALTERED_SEARCH_PATH`) before the first NetVips call: its dependencies resolve from the `libvips` folder and
NetVips' imports by name reuse the loaded modules (verified: GLib/GObject load from `libvips\` with Meld on PATH).

NetVips initializes libvips in a module initializer, which .NET runs as soon as a method that uses NetVips types is
compiled. The method that loads the DLL therefore must not reference NetVips types (it calls a separate non-inlined
method afterwards, which checks `ModuleInitializer.VipsInitialized`); otherwise libvips initializes before the DLL is
loaded, fails silently, and the next call crashes (seen during development).

Configuration: `Cache.Max = 0` (no operation cache: each file is used once), `BlockUntrusted = true` (libvips' less
hardened loaders are disabled), `Concurrency = max(1, logical cores / simultaneous conversions)` (set per job, so
parallel jobs don't oversubscribe the CPU).

## Routing

libvips converts the image when all of these hold, otherwise ImageMagick does (reason in the log):

| Check | ImageMagick when |
|---|---|
| Setting | `ImageEngine` = ImageMagick only |
| Output type | not Jpg, Png, Webp or Avif (Pdf, Ico, Gif outputs keep their jobs) |
| Input extension | not jpg/jpeg/jfif, png, webp, tif/tiff, avif, heic/heif |
| libvips | not loaded, or this build lacks the loader / saver |
| Rotation setting | not a multiple of 90° |
| File header (opened) | HEIF not AV1-compressed (this build has no HEVC decoder: iPhone HEIC go to ImageMagick), CMYK |
| Conversion | libvips throws (e.g. a BMP named `.png`): partial output deleted, ImageMagick converts (`imagemagick-fallback`) |

Deliberately left to ImageMagick:

- **RAW** (cr2, nef, dng, arw, raf…): they are TIFF based, and libvips' TIFF loader would read only the small
  embedded preview. Excluded by extension.
- **GIF** (PLAN says static GIFs via libvips): libvips decodes GIFs to RGB, so GIF → PNG was 2.3× larger than
  ImageMagick's palette PNG, and animated GIFs need ImageMagick anyway. GIF conversions are small; nothing to gain.
- **CMYK**: converting CMYK to RGB properly needs colour management choices; ImageMagick keeps its behaviour.

## Conversion

Same preset settings as ImageMagick, same results:

- **Size**: the output size is computed with ImageMagick's steps and rounding (scale factor, power-of-2 clamp,
  maximum size, each keeping the aspect ratio), then a single `Image.Thumbnail(…, size: Force)` produces it:
  shrink-on-load for JPEG/WebP, then Lanczos3 (ImageMagick's `Scale` averages pixels; Lanczos is slightly sharper).
  Verified equal dimensions on scale 25/75 %, maximum size 1000 (6000×4000 → 1000×667) and power-of-2 clamp
  (5472×3648 → 2048×1365).
- **EXIF orientation**: libvips applies it to the pixels and removes the tag; ImageMagick keeps the pixels and the tag.
  Viewers that honour the tag display the same image; the libvips output also displays correctly in viewers that
  don't (e.g. PNG). A preset rotation applies to the image as displayed.
- **Rotation**: multiples of 90° with `Rot` (clockwise, like ImageMagick); other angles use ImageMagick.
- **Metadata**: EXIF, XMP, IPTC and ICC are kept, like ImageMagick. EXIF is only written if the source had some
  (libvips would otherwise create one).
- **Encoders**: JPEG `Q` = preset quality with optimized Huffman tables (4:4:4 chroma at Q ≥ 90 like ImageMagick);
  WebP `Q`; PNG zlib level 9 with adaptive filtering (ImageMagick's "quality 95"); AVIF see calibration.

### AVIF calibration

At the same quality setting the two AV1 encoders don't give the same result. Measured in-process on real photos
(20–24 MP, quality 50, SSIM of the output against the source):

| Encoder | Time | Size vs ImageMagick | SSIM |
|---|---:|---:|---:|
| ImageMagick q50 | 1.4–2.3 s | reference | 0.976–0.986 |
| libvips q50, effort 4 (default) | 1.3–1.5 s | +28–46 % | 0.990–0.995 |
| libvips q50, effort 2 | 0.3–0.4 s | +40–52 % | 0.975–0.987 |
| **libvips q45, effort 3** (chosen) | **0.4–0.5 s** | **+11–22 %** | **0.976–0.985** |

libvips uses effort 3 and quality − 5: ImageMagick's quality, 3–5× faster, slightly larger files.

## Quality check (`bench/compare-engines.ps1` → `bench/image-engines.csv`)

Each case is converted with both engines (`ImageEngine` = Auto / ImageMagick only); SSIM is ffmpeg's, in yuv444p,
against the source scaled to the output size, and between the two outputs.

| Preset | Input | Size IM → vips | SSIM vs source IM / vips | SSIM between |
|---|---|---:|---:|---:|
| To Jpg | synthetic 24 MP JPEG | 1084 → 1079 KB | 0.6187 / 0.6187 | 0.995 |
| To Webp | synthetic 24 MP JPEG | 1677 → 1677 KB | identical | 1.000 |
| Scale 25%/To Webp | synthetic 24 MP JPEG | 7 → 7 KB | 0.8666 / 0.8666 | 0.996 |
| Scale 75%/To Jpg | synthetic 24 MP JPEG | 361 → 421 KB | 0.6354 / 0.6389 | 0.978 |
| To Png | synthetic 24 MP JPEG | 48165 → 48287 KB | identical pixels | 1.000 |
| To Avif | synthetic 24 MP JPEG | 1798 → 1249 KB | 0.6758 / 0.6460 | 0.860 |
| To Jpg | PNG | 230 → 230 KB | 0.9417 / 0.9416 | 0.999 |
| To Png | WebP | 69988 → 70164 KB | identical pixels | 1.000 |
| To Jpg | TIFF | 10488 → 10488 KB | 0.4683 / 0.4683 | 0.994 |
| Scale 75%/To Jpg | photo (meadow) | 419 → 438 KB | 0.9680 / 0.9685 | 0.994 |
| Scale 25%/To Webp | photo (meadow) | 43 → 44 KB | 0.9308 / 0.9327 | 0.984 |
| Scale 75%/To Jpg | photo (big tree) | 887 → 918 KB | 0.9327 / 0.9332 | 0.992 |
| Scale 25%/To Webp | photo (big tree) | 131 → 138 KB | 0.8807 / 0.8829 | 0.982 |
| To Avif | photo (meadow) | 414 → 502 KB | 0.9863 / 0.9848 | 0.983 |
| To Avif | photo (big tree) | 847 → 939 KB | 0.9755 / 0.9756 | 0.982 |

Except AVIF, libvips is equal or closer to the source in every case, and between the two engines SSIM is ≥ 0.98
everywhere but the 75 % resize of the synthetic noise image (0.978): the corpus JPEGs are gradients with strong noise,
the worst case for two different resampling filters, and there libvips' output is the closer one to the source.

AVIF: on real photos libvips matches ImageMagick's quality (SSIM against the source within 0.0015) with 11–21 %
larger files. On the synthetic noise image the AV1 encoder at effort 3 smooths more of the noise: 31 % smaller file,
SSIM 0.646 instead of 0.676 (this is why the AVIF benchmark output is smaller than the baseline's). Noise-like images
encoded to AVIF therefore lose a little more detail than before at the same quality setting. The low absolute SSIM of the synthetic images comes from that noise (the q90 JPEG source is
itself far from a clean image), not from the conversion.

## Benchmark (`bench/phase3.csv` vs `bench/baseline.csv`)

Median of 3 runs, 2 simultaneous conversions (the default), times include starting the application.

| Case | Baseline | Phase 3 | Speed-up | Output | Engine |
|---|---:|---:|---:|---|---|
| 200 × 24 MP JPEG → Scale 25%/To Webp | 55.8 s | **20.7 s** | 2.7× | 1.8 MB = | vips |
| 20 × 24 MP JPEG → To Avif | 24.9 s | **9.6 s** | 2.6× | 35.1 → 24.4 MB (see AVIF note) | vips |
| 20 × 24 MP JPEG → To Jpg | 3.6 s | 2.5 s | 1.5× | = | vips |
| 10 WebP → To Png | 22.4 s | 18.1 s | 1.2× | = (zlib 9 dominates) | vips |
| 5 TIFF → To Jpg | 1.9 s | 1.4 s | 1.3× | = | vips |
| 10 PNG → To Jpg | 1.8 s | 1.5 s | 1.2× | = | vips |
| 4 HEIC → To Jpg | 3.6 s | 3.7 s | — | = | imagemagick (HEVC) |
| 5 RAW → To Jpg | 9.4 s | 10.3 s | — | = | imagemagick (by extension; baseline runs 9.4–10.3 s, same code path) |

The 200-image batch now spends most of its time outside the conversions: about 100 ms per image with two in
parallel, while libvips' shrink-on-load resize of one 24 MP JPEG takes a few tens of milliseconds. The job scheduler
starts at most one job every 50–70 ms and runs two at a time (ARCHITECTURE.md §4 and §7.10): that is Phase 4.

## Verified cases (`bench/try-case.ps1`)

| Input → preset | Engine | Result |
|---|---|---|
| JPEG, PNG, WebP, TIFF → Jpg / Png / Webp / Avif | vips | ok, same dimensions as ImageMagick |
| EXIF orientation 6 JPEG (1200×800 stored) → Png | vips | 800×1200 pixels, no tag (ImageMagick: 1200×800 + tag) |
| same, rotation 90 | vips | 1200×800 (ImageMagick: 800×1200 + tag 6: same display) |
| rotation 45 | imagemagick | 1416×1416, same as ImageMagick only |
| 16-bit PNG → Jpg | vips | ok |
| CMYK JPEG → Png | imagemagick | "CMYK images are converted by ImageMagick" |
| iPhone HEIC (HEVC) → Jpg | imagemagick | "hevc HEIF images are read by ImageMagick" |
| RAW (CR2…) → Jpg | imagemagick | ".cr2 files are read by ImageMagick" |
| static / animated GIF → Png | imagemagick | palette kept |
| BMP named `.png` → Jpg | imagemagick-fallback | libvips "unable to load", ImageMagick converts it |
| `libvips` folder missing | imagemagick | "libvips not found (…)", every image converted |
| `ImageEngine` = ImageMagick | imagemagick | as before |

Not tested: cancelling a conversion while libvips runs (the progress callback cancels the libvips computation through
its cancellation token; the job is already marked failed by `Cancel()`).

## Deviations from PLAN.md

- **libvips binaries come from the official libvips release, not `NetVips.Native.win-x64`** (see "Loading libvips"),
  committed under `Middleware/` like ffmpeg.
- **GIF stays on ImageMagick** (palette, animation).
- **No "upscale mode" option**: the presets have none; upscaling (scale factor > 1) uses the same Thumbnail call.
- **`access: Sequential`** is used through `Thumbnail` (which streams); images rotated by the preset are copied to
  memory first (rotation needs random access).
- **TIFF `compression` mapping**: there is no TIFF output (TIFF is input-only).
