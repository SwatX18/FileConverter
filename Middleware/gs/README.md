# Ghostscript (Windows x64)

Used by ImageMagick (Magick.NET) to read PDF, PostScript and EPS files (`ConversionJob_ImageMagick`, which points
Magick.NET to the application folder with `MagickNET.SetGhostscriptDirectory`).

- Version: GPL Ghostscript 10.08.0 (2026-09-08), from the Artifex release
  https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/tag/gs10080, asset `gs10080w64.exe`
  (SHA-256 `52a91b8bf09298788d7a57b9206127026c23eacd75405f0a131e26dc381dce50`). `gsdll64.dll` and `gswin64c.exe` are
  the unmodified files of its `bin` folder, extracted with 7-Zip (the installer is not run):
  - `gsdll64.dll` SHA-256 `a134ed0d3af749bdab93c66029180e676d36d9cc624c192ea287f73c891fb0af`
  - `gswin64c.exe` SHA-256 `9b29d3b5128c6d53bb4e9eee77e3919d05038cdfdd8e07697af4d9c319f9d5ad`
  The fonts and resources are built into `gsdll64.dll`; nothing else from the installer is needed.
- License: GNU Affero General Public License version 3 (`LICENSE`, the installer's `doc/COPYING`, installed as
  `ghostscript-LICENSE.txt`).
- Source: https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/tag/gs10080 (`ghostscript-10.08.0.tar.gz`),
  git tag `ghostpdl-10.08.0` (https://github.com/ArtifexSoftware/ghostpdl/tree/ghostpdl-10.08.0).
- Runtime: both files need the Visual C++ 2015-2022 runtime (`msvcp140.dll`, `vcruntime140.dll`,
  `vcruntime140_1.dll`, version 14.29 or later). It is not part of Windows, so it is installed next to them
  (`Middleware/vcruntime`).

Previous version: 10.02.1, affected by CVE-2024-29510 (format string, sandbox escape from a crafted PDF / EPS,
exploited in the wild, fixed in 10.03.1) and the later fixed CVEs.

To update: download `gs<version>w64.exe` from the Artifex releases, extract `bin/gsdll64.dll`, `bin/gswin64c.exe` and
`doc/COPYING` (as `LICENSE`) with 7-Zip, update this file, check the runtime version the DLL needs
(`dumpbin /dependents`), and run the `doc_pdf_to_png_paged` integration case.
