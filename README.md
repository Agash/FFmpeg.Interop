# FFmpeg.Interop

.NET 11 bindings for FFmpeg 9: a managed API for encoding, decoding, hardware acceleration, scaling
and resampling, over complete source-generated bindings to the native libraries. Native AOT clean.

- **Managed layer** (`FFmpeg.Interop`): frames and packets as reusable, SafeHandle-owned containers;
  planes and samples as spans; zero-copy wrapping of managed memory in both directions; allocation-free
  send/receive loops; hardware devices chosen by GPU, typed views of D3D11, D3D12, VA-API, DRM PRIME,
  Vulkan, CUDA and VideoToolbox surfaces; options checked by name.
- **Native layer** (`FFmpeg.Interop.Native`): libavutil, libavcodec, libavformat, libavdevice,
  libswscale and libswresample, plus the hardware context headers, generated with ClangSharp from the
  pinned FFmpeg release. Every managed type exposes its native pointer, so anything the managed layer
  does not cover is one step away.

The package contains bindings only. Bring FFmpeg 9 shared libraries (`avcodec-63` and friends): the
loader finds them in `FFmpegLibraries.SearchDirectory`, `runtimes/<rid>/native`, the application
directory or the OS search path, and refuses a library of another ABI major.

## Decoding

```csharp
using FFmpeg.Interop;

using MediaReader reader = MediaReader.Open("clip.mkv");
MediaStream video = reader.FindBestStream(MediaType.Video)!;
using Decoder decoder = video.CreateDecoder();
using Packet packet = new();
using Frame frame = new();

while (reader.TryReadPacket(packet))
{
    foreach (Frame decoded in decoder.Decode(packet, frame))
    {
        ReadOnlyImagePlane luma = decoded.GetPlane(0);
        // luma.GetRow(y) is a ReadOnlySpan<byte> into FFmpeg's buffer.
    }
}

foreach (Frame decoded in decoder.Decode(null, frame)) { /* drain */ }
```

## Hardware

FFmpeg picks a GPU differently for every API (a DXGI index, a Vulkan index, a CUDA index, a render
node, or the default adapter). `GpuAdapter` is one identity for all of them:

```csharp
GpuAdapter gpu = GpuAdapter.Enumerate().First(a => a.Vendor == GpuVendor.Nvidia);

using HardwareDevice cuda = HardwareDevice.Create(HardwareDeviceType.Cuda, gpu);
using Decoder decoder = video.CreateDecoder(options: new DecoderOptions { HardwareDevice = cuda });
```

An application that already owns a device (a capture session, a renderer) hands it over with
`HardwareDevice.FromD3D11Device` or `FromD3D12Device`, and encoders then take its textures without a
copy. `HardwareFramePool.Of(frame)` lets an encoder take a hardware decoder's surfaces directly.

## Building

```bash
dotnet tool restore --disable-parallel
./eng/fetch-ffmpeg.ps1          # pinned FFmpeg 9 build for this machine into native/<rid>
dotnet build FFmpeg.Interop.slnx
dotnet test --solution FFmpeg.Interop.slnx --filter "TestCategory!=RequiresGpu"
./eng/test-machine.ps1          # everything this machine's GPUs can run, with coverage
```

`generate/Generate.cs` regenerates the bindings from the pinned headers in `generate/inputs.json`;
see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT for this repository. FFmpeg itself is LGPL 2.1 or later (GPL when built with GPL components);
the libraries you ship with your application carry their own license.
