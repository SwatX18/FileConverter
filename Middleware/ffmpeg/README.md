# ffmpeg and ffprobe (Windows x64)

Used for every audio and video conversion (`ConversionJob_FFMPEG`), stream copies and media probing (`Media/MediaProbe.cs`).

- Build: ffmpeg 8.0.1 "essentials" release build by Gyan Doshi, https://www.gyan.dev/ffmpeg/builds/
  (archive `ffmpeg-8.0.1-essentials_build`, also at https://github.com/GyanD/codexffmpeg/releases/tag/8.0.1).
  `ffmpeg.exe` and `ffprobe.exe` are the statically linked executables of that archive (hashes of the files here):
  - `ffmpeg.exe` SHA-256 `5af82a0d4fe2b9eae211b967332ea97edfc51c6b328ca35b827e73eac560dc0d`
  - `ffprobe.exe` SHA-256 `192a1d6899059765ac8c39764fc3148d4e6049955956dc2029f81f4bd6a8972d`
- License: built with `--enable-gpl --enable-version3`, so the executables are distributed under the GNU General Public
  License version 3 (`LICENSE`, installed as `ffmpeg-LICENSE.txt`). The bundled libraries keep their own licenses
  (x264, x265: GPL; libvpx, libaom, opus...: BSD; LAME, libvorbis...: LGPL / BSD).
- Source: FFmpeg 8.0.1, https://ffmpeg.org/releases/ffmpeg-8.0.1.tar.xz (git tag `n8.0.1`,
  https://github.com/FFmpeg/FFmpeg/tree/n8.0.1). The sources of the external libraries and the build scripts are listed
  on the build page above.
- Configuration (`ffmpeg -version`):

  ```
  --enable-gpl --enable-version3 --enable-static --disable-w32threads --disable-autodetect --enable-fontconfig --enable-iconv --enable-gnutls --enable-libxml2 --enable-gmp --enable-bzlib --enable-lzma --enable-zlib --enable-libsrt --enable-libssh --enable-libzmq --enable-avisynth --enable-sdl2 --enable-libwebp --enable-libx264 --enable-libx265 --enable-libxvid --enable-libaom --enable-libopenjpeg --enable-libvpx --enable-mediafoundation --enable-libass --enable-libfreetype --enable-libfribidi --enable-libharfbuzz --enable-libvidstab --enable-libvmaf --enable-libzimg --enable-amf --enable-cuda-llvm --enable-cuvid --enable-dxva2 --enable-d3d11va --enable-d3d12va --enable-ffnvcodec --enable-libvpl --enable-nvdec --enable-nvenc --enable-vaapi --enable-openal --enable-libgme --enable-libopenmpt --enable-libopencore-amrwb --enable-libmp3lame --enable-libtheora --enable-libvo-amrwbenc --enable-libgsm --enable-libopencore-amrnb --enable-libopus --enable-libspeex --enable-libvorbis --enable-librubberband
  ```

To update: download the matching essentials release build, replace both executables and this file's version, hashes and
configuration, then run the unit and integration tests (`Tests/README.md`).
