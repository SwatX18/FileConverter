# Tests

## Unit tests (`FileConverter.Tests`, xUnit, .NET Framework 4.8 x64)

```powershell
MSBuild Tests\FileConverter.Tests\FileConverter.Tests.csproj -restore /p:Configuration=Release /p:Platform=x64
& "<Visual Studio>\Common7\IDE\Extensions\TestPlatform\vstest.console.exe" Tests\FileConverter.Tests\bin\x64\Release\net48\FileConverter.Tests.dll /Platform:x64
```

Visual Studio's Test Explorer runs them too (the project is in `FileConverter.sln`).

| File | What it checks |
|---|---|
| `RemuxPlannerTests` | Source codecs x output container x preset settings -> copy / encode / convert per stream, or a full re-encode (embedded `remux-compat.json`) |
| `VideoEncoderArgumentsTests` | Preset quality and speed -> NVENC / AMF / QSV / CPU encoder parameters (calibrated offsets, clamping) |
| `ImageRoutingTests` | Job type per input/output, libvips or ImageMagick per format and setting, rotations, output sizes (ImageMagick's computation) |
| `HardwareEncoderDetectorTests` | Detection with a fake process runner (success, failure, timeout, exception), cache by fingerprint, Re-detect, vendor priority, encoder summary |
| `SchedulingAndSettingsTests` | Limits per lane, thread budgets, settings migrations (v5 hardware acceleration, v6 simultaneous conversions), defaults |

The tests don't touch the registry, the user's settings or the GPU. The libvips routing cases load the libvips DLLs
copied next to `FileConverterPlusPlus.exe` (xUnit shadow copying is disabled for that, `xunit.runner.json`). Logs go to a
`Diagnostics-*` folder in `%LOCALAPPDATA%\FileConverter`, like the application's.

## Integration tests (`integration.ps1`)

```powershell
powershell -ExecutionPolicy Bypass -File Tests\integration.ps1          # all cases -> Tests\integration.csv
powershell -ExecutionPolicy Bypass -File Tests\integration.ps1 -Case video_*
```

Converts the benchmark corpus (`bench/samples`, created by `bench/make-corpus.ps1`) with the built application, the
way the shell extension does, and checks each case: the application exits by itself (all conversions succeeded),
each file's log line names the expected conversion path (remux, hardware encoder, CPU fallback, vips, ImageMagick...),
and each output is readable (ffprobe and a full decode report no error) with the expected streams, dimensions and
duration (within 0.5 s of the source). 37 cases: stream copies, NVENC / AMF fallback / CPU encodes, every video
container, audio formats, image formats through libvips and ImageMagick, ICO, PDF. `-NoGpu` makes the hardware
cases expect the CPU path on a machine without a hardware encoder.

Known issue (upstream, not caused by this fork): the `To Ogv` output's Theora video does not decode cleanly with
ffmpeg 8 ("error in unpack_block_qpis", most frames lost), a long-standing problem of ffmpeg's libtheora encoder on
64-bit builds (FFmpeg trac #8040, #8775). The case is reported as `known`.
