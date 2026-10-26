# FFmpeg.Interop

.NET 11 bindings for FFmpeg 9: a managed API for encoding, decoding, hardware acceleration, scaling
and resampling, over complete source-generated bindings to the native libraries. Native AOT clean.

- **Managed layer** (`FFmpeg.Interop`): frames and packets as reusable, SafeHandle-owned containers;
  planes and samples as spans; zero-copy wrapping of managed memory in both directions; allocation-free
  send/receive loops; hardware devices chosen by GPU, typed views of D3D11, D3D12, VA-API, DRM PRIME,
  Vulkan, CUDA and VideoToolbox surfaces; options checked by name.
- **Native layer** (`FFmpeg.Interop.Native`): libavutil, libavcodec, libavformat, libavfilter,
  libavdevice, libswscale and libswresample, plus the hardware context headers, generated with ClangSharp from the
  pinned FFmpeg release. Every managed type exposes its native pointer, so anything the managed layer
  does not cover is one step away.

The package contains bindings only. Bring FFmpeg 9 shared libraries (`avcodec-63` and friends): the
loader finds them in `FFmpegLibraries.SearchDirectory`, `runtimes/<rid>/native`, the application
directory or the OS search path, and refuses a library of another ABI major.

## Getting FFmpeg 9

- **Windows and Linux:** BtbN's shared builds, `ffmpeg-n9.0-latest-<os>64-lgpl-shared-9.0` from
  [FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases) (LGPL; `gpl` adds x264 and x265). The
  libraries are in `bin/` on Windows and `lib/` on Linux; point `FFmpegLibraries.SearchDirectory` there or
  ship them next to the application. `eng/fetch-ffmpeg.ps1 -Rids <rid>` fetches the build this repository
  tests against, checksum-verified, into `native/<rid>`.
- **macOS:** Homebrew's `ffmpeg` (VideoToolbox enabled): `brew install ffmpeg`, then
  `$(brew --prefix ffmpeg)/lib`.
- **Linux distributions:** a distribution's `ffmpeg` package works when it is FFmpeg 9
  (`libavcodec.so.63`); the loader refuses another major.

On Linux, libraries loaded from a private directory find each other without `LD_LIBRARY_PATH`: they are
loaded in link order, so each dependency is already in the process.

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

## Remuxing and tags

Stream copy moves packets between containers without decoding. A copied stream keeps its tags and
disposition, as ffmpeg's does, and the writer's own tags go out with the header:

```csharp
using MediaReader reader = MediaReader.Open("in.mp4");
using MediaWriter writer = MediaWriter.Create("out.mkv");
int[] streams = [.. reader.Streams.Select(writer.AddStream)];
writer.SetMetadata("title", "Remuxed");
writer.WriteHeader();

using Packet packet = new();
while (reader.TryReadPacket(packet))
{
    writer.Write(packet, streams[packet.StreamIndex]);
}

writer.Complete();
```

`MediaReader.OpenStream` and `MediaWriter.CreateStream` do the same over any `Stream`, every open and read
takes a `CancellationToken` that interrupts a blocked network input, and `MediaReader.Seek` lands on the
keyframe at or before a time. `BitstreamFilter` rewrites packets on the way (`h264_mp4toannexb` for a
raw or MPEG-TS output), and `AudioFifo` turns any frame sizes into the fixed size an encoder takes.

## Filtering

`FilterGraph` runs FFmpeg's filter syntax between buffer sources and a buffer sink, converting into the
formats the sink is given:

```csharp
using FilterGraph graph = FilterGraph.Video(
    "yadif,scale=1280:720",
    [new VideoFilterInput(1920, 1080, PixelFormat.Yuv420P, new Rational(1, 30))],
    [PixelFormat.Nv12]
);

graph.Send(decoded);
while (graph.Receive(scaled) == CodecStatus.Available)
{
    // scaled is deinterlaced 1280x720 NV12, timed in graph.OutputTimeBase.
}
```

Several inputs are labelled `[in0]`, `[in1]` and so on, as in `[in0][in1]overlay=8:8`; `FilterGraph.Audio`
does the same for `aresample`, `amix` and the other audio filters.

## Hardware

FFmpeg picks a GPU differently for every API (a DXGI index, a Vulkan index, a CUDA index, a render
node, or the default adapter). `GpuAdapter` is one identity for all of them:

```csharp
GpuAdapter gpu = GpuAdapter.Enumerate().First(a => a.Vendor == GpuVendor.Nvidia);

using HardwareDevice cuda = HardwareDevice.CreateForAdapter(HardwareDeviceType.Cuda, gpu);
using Decoder decoder = video.CreateDecoder(options: new DecoderOptions { HardwareDevice = cuda });
```

An application that already owns a device (a capture session, a renderer) hands it over with
`HardwareDevice.FromD3D11Device` or `FromD3D12Device`, so its textures and the encoder share the GPU.
`HardwareFramePool.Of(frame)` lets an encoder take a hardware decoder's surfaces directly.

Each hardware API's interop lives in its own extension class, named after FFmpeg's `hwcontext_*.h`:
`D3D11VAExtensions`, `D3D12VAExtensions`, `VaapiExtensions`, `DrmExtensions`, `VulkanExtensions`,
`CudaExtensions` and `VideoToolboxExtensions`. Each adds the typed native views and imports that API
has to `HardwareDevice`, `HardwareFramePool` and `Frame`, and carries the platforms it runs on.

## Real-time encoding

What a sender needs from an encoder is there without dropping to the native layer:

- **Key frames on demand:** `frame.PictureType = PictureType.I` makes the next frame intra, the answer to
  a receiver's PLI.
- **Rate control:** `BitRate`, `MaxRate`, `BufferSize` and `LowDelay` on open, and
  `Encoder.SetRateControl` mid-stream for congestion control, on the encoders FFmpeg reconfigures
  (NVENC, libx264; `SupportsRateControlChanges` says which, instead of changes being silently ignored).
- **GPU inputs:** without a copy, `Frame.FromDrmPrime` imports DMA-BUFs (a PipeWire screencast, a V4L2
  camera) for `Frame.MapTo(pool, ...)` into VA-API surfaces, and `VulkanDmaBufImporter` imports them as
  images a Vulkan Video encoder reads in place (FFmpeg's own Vulkan mapping cannot be encoder input), with
  `Acquire` and `Release` handing each buffer between its producer and the encoder; a producer that
  synchronises explicitly (PipeWire's DRM syncobj timelines) passes its `DrmSyncPoint` to `Acquire`, and
  the GPU waits for it, so the CPU never does (`WaitsForSyncPoints` says whether the device can);
  `SupportedModifiers` lists the layouts a producer can allocate to be read in place. `HardwareFramePool.WrapD3D12Texture`
  hands an application's D3D12 texture to a D3D12 encoder, ordered after the producer's queue on the
  GPU, and `HardwareFramePool.WrapCVPixelBuffer` and `WrapIOSurface` hand a pixel buffer or a bare IOSurface (a Syphon frame) to VideoToolbox.
  `HardwareFramePool.WrapD3D11Texture` hands an application's D3D11 texture to NVENC or AMF as it is;
  `CopyFromD3D11Texture` copies one that is larger than the picture or in another format on the GPU. `Frame.CopyTo` copies a picture between two Vulkan frames of one
  device on the GPU, or between system-memory frames.
- **GPU outputs:** `DecoderOptions.DrmModifiers` makes a Vulkan decoder decode into images with those
  DRM format modifiers on exportable memory, so `Frame.MapTo` to `PixelFormat.DrmPrime` shares each
  picture as a DMA-BUF without a copy (VA-API surfaces map the same way as they are).
  `DecoderOptions.ExtraHardwareFrames` grows a hardware decoder's fixed surface pool for the frames the
  application keeps (a playout queue, an encoder reading decoded surfaces), which would otherwise stall
  decoding.
- **Defined pictures:** `Frame.AllocateVideo` leaves the picture undefined, as FFmpeg does; `FillBlack`
  sets it to black at the frame's colour range.
- **Capture devices:** `MediaReader.ListDevices("dshow")` lists a format's devices; `MediaReader.OpenDevice("v4l2", "/dev/video0")`
  opens one (`dshow`, `v4l2`, `avfoundation`, `lavfi`).
- **Logging:** `FFmpegLogging.UseLoggerFactory(loggerFactory)` routes `av_log` to `ILogger`, one
  category per FFmpeg component class with the codec or format name as a property. `RouteTo` does
  the same until the returned route is disposed, for a container that owns its factory.
  `FFmpegLogging.Demote()` caps a thread's messages at Debug while it probes, for failures the caller
  expects and reports itself.

## Samples

- `samples/FFmpeg.Interop.Thumbnail`: the first picture of a video, scaled and written as a PNG.
- `samples/FFmpeg.Interop.Smoke`: encodes a clip to a file, reads it back and decodes it; CI publishes
  it with native AOT and runs it.

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
