# libvips (shared "web" build, Windows x64)

Used by `ConversionJob_Vips` through NetVips (images, see `docs/IMAGES.md`).

- Source: https://github.com/libvips/build-win64-mxe/releases/tag/v8.18.7, asset `vips-dev-x64-web-8.18.7.zip`
  (SHA-256 `3a122eb3d588690008216f786338e2f9329dba3c3c2768b5933cf7299d549241`).
- Contents: the `bin/*.dll` files of the archive, unmodified, plus `LICENSE` (libvips: LGPL-2.1-or-later) and
  `versions.json` (versions of the bundled libraries: libjpeg (mozjpeg), libpng, libwebp, libtiff, libheif + aom,
  glib...). Each library keeps its own license (LGPL, BSD, MIT, zlib...); see the upstream project pages.
- The "web" build loads and saves JPEG, PNG, WebP, TIFF, GIF, AVIF (AV1 through aom) and SVG. It has no HEVC decoder:
  HEIC files are converted by ImageMagick.

Why the shared build and not the `NetVips.Native.win-x64` NuGet package: that package only contains a static
`libvips-42.dll` (GLib linked in) and only declares net6.0. NetVips on .NET Framework imports
`libglib-2.0-0.dll` and `libgobject-2.0-0.dll` by name and has no DLL import resolver to redirect them, so with the
static build Windows loads whatever GLib it finds on PATH (on the test machine: Meld's), which crashes. The shared build
ships those DLLs; the application loads `libvips\libvips-42.dll` by full path at startup so its dependencies resolve from
this folder.

To update: download the matching `vips-dev-x64-web-<version>.zip`, replace the DLLs, `LICENSE` and `versions.json`,
and keep the NetVips package version compatible.
