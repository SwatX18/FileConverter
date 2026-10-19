# Visual C++ runtime (x64)

`msvcp140.dll`, `vcruntime140.dll` and `vcruntime140_1.dll` from the Microsoft Visual C++ 2015-2022 Redistributable,
version 14.44.35211, copied unmodified from Visual Studio 2022
(`VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT`).

Ghostscript (`gsdll64.dll`, `gswin64c.exe`) needs them, and they are not part of Windows: without them, PDF conversions
only work on computers where another program installed the Visual C++ Redistributable. They are installed next to
`FileConverter.exe` ("app-local" deployment), where Windows finds them first; the Universal CRT
(`api-ms-win-crt-*.dll`) is part of Windows 10 and 11. Nothing else in File Converter uses them: ffmpeg, ffprobe,
ImageMagick and libvips bring their own runtimes.

These files are redistributable under the Visual Studio license (https://aka.ms/vs/17/redist.txt, "Visual C++ Runtime Files": any file of `VC\redist`, unmodified);
see https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files.

| File | SHA-256 |
|---|---|
| `msvcp140.dll` | `0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9` |
| `vcruntime140.dll` | `d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066` |
| `vcruntime140_1.dll` | `1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f` |
