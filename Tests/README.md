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
copied next to `FileConverter.exe` (xUnit shadow copying is disabled for that, `xunit.runner.json`). Logs go to a
`Diagnostics-*` folder in `%LOCALAPPDATA%\FileConverter`, like the application's.
