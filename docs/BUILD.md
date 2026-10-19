# Building File Converter (fast-convert fork)

These are the exact steps verified on Windows 11 (2026-10-18). A full Release x64 build of the solution, including the MSI, takes about 40 s.

## Prerequisites

| Requirement | Why | Verified with |
|---|---|---|
| Visual Studio 2022 (Community or Build Tools) with the **.NET desktop development** workload | The two C# projects are legacy (non-SDK) .NET Framework WPF projects. They need VS's MSBuild and the WPF markup compiler. `dotnet build` does **not** work (it fails with MSB3822/MSB3823 on the resources, and it has no WPF markup compile for legacy projects). | VS Community 17.14, MSBuild 17.14.60 |
| .NET Framework 4.8 targeting pack | `TargetFrameworkVersion` is `v4.8` in both csproj files | `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8` |
| WiX Toolset 5 | Installer (`Installer.wixproj` uses `WixToolset.Sdk/5.0.2`) | Restored automatically from NuGet. No separate install is needed. |
| Windows SDK signing tools (`signtool.exe`) | Only needed to **sign** the MSI and binaries | Windows SDK 10.0.26100 |
| Internet access on first build | NuGet restore (Magick.NET, SharpShell, NetOffice, CommunityToolkit.Mvvm, WiX…) | |

If VS is missing, install the Build Tools:

```powershell
winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.Net.Component.4.8.TargetingPack --includeRecommended"
```

Find MSBuild (VS may be installed on another drive. On the dev machine it is on `D:`):

```powershell
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"
```

## One-time setup: `Installer/Installer.sign`

`Installer.wixproj` imports `Installer\Installer.sign` unconditionally in Release. That file holds the upstream maintainer's signing commands and is **gitignored**, so a fresh clone fails the Release installer build. Create a local no-op stub (it stays untracked):

```powershell
Set-Content -Encoding utf8 Installer\Installer.sign "<Project>`n  <!-- Local no-op stub: no signing. -->`n</Project>"
```

To sign, put your own `<Target Name="SignMsi" ...>` / `signtool` invocations in this file instead.

## Build

From the repository root:

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
& $msbuild FileConverter.sln -restore -m -p:Configuration=Release -p:Platform=x64 -v:m
```

Outputs:

- `Application\FileConverter\bin\x64\Release\FileConverter.exe`, with `ffmpeg.exe`, `ffprobe.exe`, `gswin64c.exe`, `gsdll64.dll`, the Visual C++ runtime DLLs (`Middleware\vcruntime`) and `Settings.default.xml` copied next to it by the post-build event (paths relative to the project, so building `FileConverter.csproj` alone works too), and satellite assemblies moved to `Languages\`
- `Application\FileConverterExtension\bin\x64\Release\FileConverterExtension.dll` (the shell extension, strong-named with `FileConverterExtensionKey.snk`)
- `Installer\bin\x64\Release\FileConverter-setup.msi` (unsigned)

Notes:

- The solution only defines `Debug|x64` and `Release|x64`. The x86 configurations still present in `FileConverter.csproj` are not wired into the solution.
- Magick.NET-Q16-AnyCPU is 14.17.2: 14.10.2 had 144 known advisories (`NU1901–NU1903` warnings at restore); 14.17.2 has none at the time of writing (2026-10-19).

## Running a dev build without installing

`FileConverter.exe` locates `Settings.default.xml` through the registry value `HKCU\Software\FileConverter\Path`, which the installer normally writes. Without it the app throws "Can't retrieve file converter registry entry." For a dev build:

```powershell
New-Item -Force HKCU:\Software\FileConverter | Out-Null
Set-ItemProperty HKCU:\Software\FileConverter Path "$PWD\Application\FileConverter\bin\x64\Release\FileConverter.exe"
```

User settings live in `%LOCALAPPDATA%\FileConverter\Settings.user.xml`, and diagnostics logs go in `%LOCALAPPDATA%\FileConverter\Diagnostics*`.

Command line (the same one the shell extension uses):

```
FileConverter.exe --conversion-preset "To Mp4" [--verbose] file1 file2 ...
FileConverter.exe --conversion-preset "To Mp4" --input-files list.txt
```

`bench/run-bench.ps1` sets up (and afterwards restores) the registry value and a temporary `Settings.user.xml` automatically.

The shell extension (right-click menu) needs an elevated `FileConverter.exe --register-shell-extension <path-to-FileConverterExtension.dll>`. Installing the MSI is the simpler way to test it.
