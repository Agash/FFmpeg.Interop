# FFmpeg.Interop.Native

The FFmpeg 9 shared libraries [FFmpeg.Interop](https://github.com/Agash/FFmpeg.Interop) is generated from,
for Windows, Linux and macOS on x64 and arm64. Reference `FFmpeg.Interop.Native` for every runtime, or one
`FFmpeg.Interop.Native.<rid>` package for a single one.

Built in CI from pinned, unmodified upstream sources (`eng/build-ffmpeg.sh`, `eng/native-inputs.json`):

- **Windows**: D3D11VA, D3D12VA, DXVA2, Media Foundation, NVENC/NVDEC, AMF, Vulkan, and QSV on x64.
- **Linux**: VA-API with DRM displays, Vulkan, NVENC/NVDEC, AMF, V4L2 M2M, QSV on x64, and Rockchip MPP on
  arm64; FFmpeg's DMA-BUF synchronisation compiled in; glibc 2.28 and later.
- **macOS**: VideoToolbox and AudioToolbox, AVFoundation capture, the VideoToolbox and Metal filters,
  macOS 14 and later.
- **Software**: OpenH264, SVT-AV1, libaom, dav1d, libvpx, kvazaar, Opus, LAME, zlib (PNG).
- **TLS** for https, TLS and DTLS: Schannel on Windows, OpenSSL 3 elsewhere.

Licensing: LGPL 2.1 or later, except `linux-arm64`, which is LGPL 3 or later because of Rockchip's MPP.
No GPL or non-free component is included, and the libraries are shared, so they can be replaced. The
exact source of each build is the tagged commit of this repository. Codec patents are not covered by the
LGPL; Cisco's OpenH264 patent coverage applies to Cisco's own binaries only, not to this build.
