using System.Security.Cryptography;
using System.Text.Json;

namespace FFmpeg.Interop.Tests;

/// <summary>How an encoder test feeds the encoder.</summary>
public enum EncoderInput
{
    /// <summary>System-memory frames, the encoder pinned to the GPU by a device on it.</summary>
    SystemMemory,

    /// <summary>Surfaces uploaded into a frame pool on the device.</summary>
    Surfaces,
}

/// <summary>
/// Hardware decoders and encoders on real GPUs. No CI runner has one, so each machine's tests carry a
/// category and are run there:
/// <list type="bullet">
/// <item>RequiresNvidia: NVDEC/NVENC through CUDA, D3D11, D3D12 and Vulkan (Windows).</item>
/// <item>RequiresAmf: an AMD GPU through D3D11 and AMF (Windows).</item>
/// <item>RequiresVaapi: an AMD GPU through VA-API, Vulkan and DRM (Linux).</item>
/// <item>RequiresVideoToolbox: Apple's media engine (macOS).</item>
/// </list>
/// GPUs are chosen by vendor through <see cref="GpuAdapter"/>, never by an API-specific index. A missing
/// GPU is a failure, not a skip: filtering a run to a category states that the hardware is there.
/// Decoding is checked bit-exact against FFmpeg's software decoder, which the H.264, HEVC and AV1
/// specifications make possible; encoding is checked by reading the output back with ffprobe and
/// measuring it against the source with the ffmpeg tool.
/// </summary>
/// <remarks>
/// Left out, with the reason, so a gap is never mistaken for a pass:
/// AV1 encoding on the RTX 3060 (Ampere's NVENC has none, and D3D12 and Vulkan report the same);
/// D3D12 encoding with B-frames on NVIDIA (FFmpeg 9's D3D12 encoder fails the first frame; low-latency
/// encoding has no B-frames, and every encode here disables them);
/// D3D12 and Vulkan video on the Radeon Vega iGPU (its driver exposes neither: FFmpeg reports D3D12
/// decode tier 1 only and no VK_KHR_video_decode_queue).
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresGpu")]
public sealed class HardwareCodecTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [DataRow("h264", "d3d11va")]
    [DataRow("hevc", "d3d11va")]
    [DataRow("av1", "d3d11va")]
    [DataRow("h264", "d3d12va")]
    [DataRow("hevc", "d3d12va")]
    [DataRow("av1", "d3d12va")]
    [DataRow("hevc-720p-nvenc", "d3d12va")]
    [DataRow("hevc-720p-nvenc", "d3d11va")]
    [DataRow("h264", "cuda")]
    [DataRow("hevc", "cuda")]
    [DataRow("av1", "cuda")]
    [DataRow("h264", "vulkan")]
    [DataRow("hevc", "vulkan")]
    [DataRow("av1", "vulkan")]
    public Task NvidiaDecode_IsBitExactWithSoftwareDecode(string clip, string deviceType) =>
        AssertDecodeMatchesSoftwareAsync(clip, GpuVendor.Nvidia, deviceType, decoder: null);

    [TestMethod]
    [TestCategory("RequiresAmf")]
    [DataRow("h264", "d3d11va", null)]
    [DataRow("hevc", "d3d11va", null)]
    [DataRow("h264", "amf", "h264_amf")]
    [DataRow("hevc", "amf", "hevc_amf")]
    public Task AmfDecode_IsBitExactWithSoftwareDecode(
        string clip,
        string deviceType,
        string? decoder
    ) => AssertDecodeMatchesSoftwareAsync(clip, GpuVendor.Amd, deviceType, decoder);

    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [DataRow("h264", "vaapi")]
    [DataRow("hevc", "vaapi")]
    [DataRow("av1", "vaapi")]
    [DataRow("h264", "vulkan")]
    [DataRow("hevc", "vulkan")]
    [DataRow("av1", "vulkan")]
    public Task VaapiDecode_IsBitExactWithSoftwareDecode(string clip, string deviceType) =>
        AssertDecodeMatchesSoftwareAsync(clip, GpuVendor.Amd, deviceType, decoder: null);

    [TestMethod]
    [TestCategory("RequiresVideoToolbox")]
    [DataRow("h264")]
    [DataRow("hevc")]
    public Task VideoToolboxDecode_IsBitExactWithSoftwareDecode(string clip) =>
        AssertDecodeMatchesSoftwareAsync(clip, GpuVendor.Apple, "videotoolbox", decoder: null);

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [DataRow("h264_nvenc", "cuda", EncoderInput.SystemMemory)]
    [DataRow("hevc_nvenc", "d3d11va", EncoderInput.SystemMemory)]
    [DataRow("h264_nvenc", "cuda", EncoderInput.Surfaces)]
    [DataRow("hevc_nvenc", "cuda", EncoderInput.Surfaces)]
    [DataRow("h264_nvenc", "d3d11va", EncoderInput.Surfaces)]
    [DataRow("hevc_nvenc", "d3d11va", EncoderInput.Surfaces)]
    [DataRow("h264_d3d12va", "d3d12va", EncoderInput.Surfaces)]
    [DataRow("hevc_d3d12va", "d3d12va", EncoderInput.Surfaces)]
    [DataRow("h264_vulkan", "vulkan", EncoderInput.Surfaces)]
    [DataRow("hevc_vulkan", "vulkan", EncoderInput.Surfaces)]
    public Task NvidiaEncode_ProducesAStreamFFmpegReadsBack(
        string encoder,
        string deviceType,
        EncoderInput input
    ) => AssertEncodeAsync(encoder, GpuVendor.Nvidia, deviceType, input);

    [TestMethod]
    [TestCategory("RequiresAmf")]
    [DataRow("h264_amf", "d3d11va", EncoderInput.SystemMemory)]
    [DataRow("hevc_amf", "amf", EncoderInput.SystemMemory)]
    [DataRow("h264_amf", "d3d11va", EncoderInput.Surfaces)]
    [DataRow("hevc_amf", "d3d11va", EncoderInput.Surfaces)]
    public Task AmfEncode_ProducesAStreamFFmpegReadsBack(
        string encoder,
        string deviceType,
        EncoderInput input
    ) => AssertEncodeAsync(encoder, GpuVendor.Amd, deviceType, input);

    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [DataRow("h264_vaapi", "vaapi", EncoderInput.Surfaces)]
    [DataRow("hevc_vaapi", "vaapi", EncoderInput.Surfaces)]
    public Task VaapiEncode_ProducesAStreamFFmpegReadsBack(
        string encoder,
        string deviceType,
        EncoderInput input
    ) => AssertEncodeAsync(encoder, GpuVendor.Amd, deviceType, input);

    [TestMethod]
    [TestCategory("RequiresVideoToolbox")]
    [DataRow("h264_videotoolbox", EncoderInput.SystemMemory)]
    [DataRow("hevc_videotoolbox", EncoderInput.SystemMemory)]
    [DataRow("h264_videotoolbox", EncoderInput.Surfaces)]
    public Task VideoToolboxEncode_ProducesAStreamFFmpegReadsBack(
        string encoder,
        EncoderInput input
    ) => AssertEncodeAsync(encoder, GpuVendor.Apple, "videotoolbox", input);

    // The transcodes never leave the GPU: the encoder takes the decoder's surfaces through the decoder's
    // own frame pool.
    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [DataRow("h264", "cuda", "hevc_nvenc")]
    [DataRow("av1", "cuda", "h264_nvenc")]
    [DataRow("h264", "d3d11va", "hevc_nvenc")]
    [DataRow("h264", "d3d12va", "hevc_d3d12va")]
    [DataRow("hevc", "vulkan", "h264_vulkan")]
    public Task NvidiaTranscode_StaysOnTheGpu(string clip, string deviceType, string encoder) =>
        AssertGpuTranscodeAsync(clip, GpuVendor.Nvidia, deviceType, encoder);

    [TestMethod]
    [TestCategory("RequiresAmf")]
    [DataRow("h264", "d3d11va", "hevc_amf")]
    public Task AmfTranscode_StaysOnTheGpu(string clip, string deviceType, string encoder) =>
        AssertGpuTranscodeAsync(clip, GpuVendor.Amd, deviceType, encoder);

    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [DataRow("h264", "vaapi", "hevc_vaapi")]
    public Task VaapiTranscode_StaysOnTheGpu(string clip, string deviceType, string encoder) =>
        AssertGpuTranscodeAsync(clip, GpuVendor.Amd, deviceType, encoder);

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NvidiaAdapter_ResolvesToTheSameGpuThroughEveryApi()
    {
        GpuAdapter nvidia = Adapter(GpuVendor.Nvidia);
        Assert.IsNotNull(nvidia.Luid);
        Assert.IsGreaterThan(0L, nvidia.DedicatedVideoMemory);

        using HardwareDevice vulkan = HardwareDevice.Create(HardwareDeviceType.Vulkan, nvidia);
        Assert.IsTrue(vulkan.TryGetLuid(out long luid));
        Assert.AreEqual(nvidia.Luid, luid, "Vulkan opened another GPU.");

        using HardwareDevice cuda = HardwareDevice.Create(HardwareDeviceType.Cuda, nvidia);
        Assert.IsTrue(cuda.TryGetCudaContext(out nint context));
        Assert.AreNotEqual(0, context);

        using HardwareDevice d3d12 = HardwareDevice.Create(HardwareDeviceType.D3D12VA, nvidia);
        Assert.IsTrue(d3d12.TryGetD3D12(out D3D12Device d3d12Device));
        Assert.AreNotEqual(0, d3d12Device.Device);
        Assert.AreNotEqual(0, d3d12Device.VideoDevice);
        Assert.AreEqual(PixelFormat.D3D12, d3d12.SurfaceFormat);
    }

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void CudaSurfaces_ExposeDevicePointers()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.Cuda,
            Adapter(GpuVendor.Nvidia)
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            device.SurfaceFormat,
            PixelFormat.Nv12,
            64,
            32
        );
        using Frame surface = new();
        pool.GetFrame(surface);

        Assert.IsTrue(surface.TryGetCudaPlane(0, out nint luma, out int pitch));
        Assert.IsTrue(surface.TryGetCudaPlane(1, out nint chroma, out _));
        Assert.AreNotEqual(0, luma);
        Assert.AreNotEqual(luma, chroma);
        Assert.IsGreaterThanOrEqualTo(64, pitch);
    }

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void D3D12Surfaces_RoundTripAndExposeTheirFence()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            device.SurfaceFormat,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(64, 48, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(7).NextBytes(pixels);
        source.CopyImageFrom(pixels);

        using Frame surface = new();
        pool.Upload(source, surface);
        Assert.IsTrue(surface.TryGetD3D12Texture(out D3D12Texture texture));
        Assert.AreNotEqual(0, texture.Resource);
        Assert.AreNotEqual(0, texture.Fence);

        using Frame back = new();
        surface.TransferTo(back);
        byte[] downloaded = new byte[back.GetImageSize()];
        _ = back.CopyImageTo(downloaded);
        CollectionAssert.AreEqual(pixels, downloaded);
    }

    // The capture-to-encode path StreamTransport takes: the application owns the D3D device (a Windows
    // Graphics Capture session), and the encoder is handed that device rather than opening its own.
    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ApplicationOwnedD3D11Device_EncodesItsSurfaces()
    {
        using HardwareDevice opened = HardwareDevice.Create(
            HardwareDeviceType.D3D11VA,
            Adapter(GpuVendor.Nvidia)
        );
        Assert.IsTrue(opened.TryGetD3D11(out D3D11Device application));
        using HardwareDevice wrapped = HardwareDevice.FromD3D11Device(application.Device);
        Assert.IsTrue(wrapped.TryGetD3D11(out D3D11Device shared));
        Assert.AreEqual(application.Device, shared.Device);

        await AssertEncodeOnDeviceAsync("h264_nvenc", wrapped, EncoderInput.Surfaces);
    }

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ApplicationOwnedD3D12Device_EncodesItsSurfaces()
    {
        using HardwareDevice opened = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        Assert.IsTrue(opened.TryGetD3D12(out D3D12Device application));
        using HardwareDevice wrapped = HardwareDevice.FromD3D12Device(application.Device);

        await AssertEncodeOnDeviceAsync("h264_d3d12va", wrapped, EncoderInput.Surfaces);
    }

    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void VaapiAdapter_DerivesDrmAndVulkanOnTheSameGpu()
    {
        GpuAdapter amd = Adapter(GpuVendor.Amd);
        Assert.IsNotNull(amd.RenderNode);
        using HardwareDevice vaapi = HardwareDevice.Create(HardwareDeviceType.Vaapi, amd);
        using HardwareDevice drm = vaapi.Derive(HardwareDeviceType.Drm);
        using HardwareDevice vulkan = HardwareDevice.Create(HardwareDeviceType.Vulkan, amd);

        Assert.IsTrue(vaapi.TryGetVaapiDisplay(out nint display));
        Assert.AreNotEqual(0, display);
        Assert.IsTrue(drm.TryGetDrmFileDescriptor(out int fd));
        Assert.IsGreaterThanOrEqualTo(0, fd);
        Assert.IsTrue(vulkan.TryGetVulkan(out VulkanDevice handles));
        Assert.AreNotEqual(0, handles.Device);
    }

    internal static GpuAdapter Adapter(GpuVendor vendor)
    {
        IReadOnlyList<GpuAdapter> adapters = GpuAdapter.Enumerate();
        GpuAdapter? adapter = adapters.FirstOrDefault(a => a.Vendor == vendor && !a.IsSoftware);
        Assert.IsNotNull(adapter, $"No {vendor} GPU among: {string.Join(", ", adapters)}.");
        return adapter;
    }

    private static HardwareDeviceType DeviceType(string name) =>
        HardwareDeviceType.TryParse(name, out HardwareDeviceType type)
            ? type
            : throw new ArgumentException($"Unknown device type {name}.");

    private async Task AssertDecodeMatchesSoftwareAsync(
        string clip,
        GpuVendor vendor,
        string deviceType,
        string? decoder
    )
    {
        string path = await TestMedia.ClipAsync(clip, TestContext.CancellationToken);
        List<string> software = CodingTests.DecodeFrameMd5s(
            path,
            Codec.FindDecoder(ClipCodec(clip)).Name,
            PixelFormat.Yuv420P
        );
        Assert.HasCount(TestMedia.FrameCount, software);

        using HardwareDevice device = HardwareDevice.Create(
            DeviceType(deviceType),
            Adapter(vendor)
        );
        List<string> hardware = [];
        using (MediaReader reader = MediaReader.Open(path))
        using (
            Decoder hardwareDecoder = reader
                .Streams[0]
                .CreateDecoder(
                    decoder is null ? null : Codec.FindDecoder(decoder),
                    new DecoderOptions { HardwareDevice = device }
                )
        )
        using (Packet packet = new())
        using (Frame frame = new())
        using (Frame downloaded = new())
        using (Frame planar = new())
        using (
            Scaler scaler = new(
                new ScalerOptions { Algorithm = ScaleAlgorithm.Point, AccurateRounding = true }
            )
        )
        {
            void Collect(Frame decoded)
            {
                Assert.IsTrue(decoded.IsHardwareFrame, $"{deviceType} produced a software frame.");
                downloaded.Reset();
                decoded.TransferTo(downloaded);

                // Surfaces come back as NV12; rearranging chroma planes is lossless.
                planar.Width = downloaded.Width;
                planar.Height = downloaded.Height;
                planar.PixelFormat = PixelFormat.Yuv420P;
                scaler.Scale(downloaded, planar);
                hardware.Add(Md5(planar));
            }

            while (reader.TryReadPacket(packet))
            {
                foreach (Frame decoded in hardwareDecoder.Decode(packet, frame))
                {
                    Collect(decoded);
                }
            }

            foreach (Frame decoded in hardwareDecoder.Decode(null, frame))
            {
                Collect(decoded);
            }
        }

        CollectionAssert.AreEqual(
            software,
            hardware,
            $"{clip} on {deviceType} differs from the software decoder."
        );
    }

    private async Task AssertEncodeAsync(
        string encoder,
        GpuVendor vendor,
        string deviceType,
        EncoderInput input
    )
    {
        using HardwareDevice device = HardwareDevice.Create(
            DeviceType(deviceType),
            Adapter(vendor)
        );
        await AssertEncodeOnDeviceAsync(encoder, device, input);
    }

    private async Task AssertEncodeOnDeviceAsync(
        string encoderName,
        HardwareDevice device,
        EncoderInput input
    )
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string raw = await TestMedia.RawVideoAsync("nv12", cancellationToken);
        byte[] video = await File.ReadAllBytesAsync(raw, cancellationToken);
        int frameSize = TestMedia.Width * TestMedia.Height * 3 / 2;
        using Scratch scratch = new();
        // MPEG-TS carries parameter sets in band, as the D3D12 encoders produce them: FFmpeg 9's D3D12
        // encoders never fill extradata for a global header, so Matroska and MP4 refuse their output.
        string output = scratch["hardware.ts"];

        using HardwareFramePool? pool =
            input == EncoderInput.Surfaces
                ? HardwareFramePool.Create(
                    device,
                    device.SurfaceFormat,
                    PixelFormat.Nv12,
                    TestMedia.Width,
                    TestMedia.Height
                )
                : null;

        using (MediaWriter writer = MediaWriter.Create(output))
        using (
            Encoder encoder = Encoder.Create(
                Codec.FindEncoder(encoderName),
                new VideoEncoderOptions
                {
                    Width = TestMedia.Width,
                    Height = TestMedia.Height,
                    PixelFormat = pool?.Format ?? PixelFormat.Nv12,
                    TimeBase = new(1, TestMedia.FrameRate),
                    FrameRate = new(TestMedia.FrameRate, 1),
                    BitRate = 4_000_000,
                    MaxBFrames = 0,
                    HardwareFrames = pool,
                    HardwareDevice = pool is null ? device : null,
                    GlobalHeader = writer.RequiresGlobalHeader,
                }
            )
        )
        using (Packet packet = new())
        using (Frame surface = new())
        {
            int stream = writer.AddStream(encoder);
            writer.WriteHeader();
            for (int i = 0; i < TestMedia.FrameCount; i++)
            {
                using Frame frame = Frame.WrapImage(
                    video.AsMemory(i * frameSize, frameSize),
                    TestMedia.Width,
                    TestMedia.Height,
                    PixelFormat.Nv12
                );
                Frame fed = frame;
                if (pool is not null)
                {
                    pool.Upload(frame, surface);
                    fed = surface;
                }

                fed.PresentationTimestamp = i;
                foreach (Packet encoded in encoder.Encode(fed, packet))
                {
                    writer.Write(encoded, stream);
                }
            }

            foreach (Packet encoded in encoder.Encode(null, packet))
            {
                writer.Write(encoded, stream);
            }

            writer.Complete();
        }

        JsonElement probed = (
            await FFmpegCli.FFprobeAsync(
                cancellationToken,
                "-count_frames",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=codec_name,width,height,nb_read_frames",
                output
            )
        ).GetProperty("streams")[0];
        Assert.AreEqual(
            encoderName[..encoderName.IndexOf('_', StringComparison.Ordinal)],
            probed.GetProperty("codec_name").GetString()
        );
        Assert.AreEqual(TestMedia.Width, probed.GetProperty("width").GetInt32());
        Assert.AreEqual(TestMedia.Height, probed.GetProperty("height").GetInt32());
        Assert.AreEqual(
            $"{TestMedia.FrameCount}",
            probed.GetProperty("nb_read_frames").GetString()
        );

        double psnr = await FFmpegCli.PsnrAsync(
            ["-i", output],
            FFmpegCli.RawVideoInput(raw, "nv12", TestMedia.Width, TestMedia.Height),
            cancellationToken
        );
        Assert.IsGreaterThan(35.0, psnr, $"{encoderName}: PSNR {psnr:F1} dB against the source.");
    }

    private async Task AssertGpuTranscodeAsync(
        string clip,
        GpuVendor vendor,
        string deviceType,
        string encoderName
    )
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string path = await TestMedia.ClipAsync(clip, cancellationToken);
        using Scratch scratch = new();
        string output = scratch["transcoded.ts"];

        using HardwareDevice device = HardwareDevice.Create(
            DeviceType(deviceType),
            Adapter(vendor)
        );
        int frames = 0;
        using (MediaReader reader = MediaReader.Open(path))
        using (
            Decoder decoder = reader
                .Streams[0]
                .CreateDecoder(options: new DecoderOptions { HardwareDevice = device })
        )
        using (MediaWriter writer = MediaWriter.Create(output))
        using (Packet input = new())
        using (Packet encoded = new())
        using (Frame frame = new())
        {
            Encoder? encoder = null;
            HardwareFramePool? pool = null;
            int stream = -1;
            try
            {
                void Encode(Frame? decoded)
                {
                    if (decoded is not null && encoder is null)
                    {
                        // The encoder is opened on the first decoded surface, sharing the decoder's pool.
                        pool = HardwareFramePool.Of(decoded);
                        encoder = Encoder.Create(
                            Codec.FindEncoder(encoderName),
                            new VideoEncoderOptions
                            {
                                Width = decoded.Width,
                                Height = decoded.Height,
                                PixelFormat = decoded.PixelFormat,
                                TimeBase = reader.Streams[0].TimeBase,
                                FrameRate = new(TestMedia.FrameRate, 1),
                                BitRate = 4_000_000,
                                MaxBFrames = 0,
                                HardwareFrames = pool,
                                GlobalHeader = writer.RequiresGlobalHeader,
                            }
                        );
                        stream = writer.AddStream(encoder);
                        writer.WriteHeader();
                    }

                    if (decoded is not null)
                    {
                        Assert.IsTrue(
                            decoded.IsHardwareFrame,
                            "The decoder handed back a system-memory frame."
                        );
                        frames++;
                    }

                    foreach (Packet packet in encoder!.Encode(decoded, encoded))
                    {
                        writer.Write(packet, stream);
                    }
                }

                while (reader.TryReadPacket(input))
                {
                    foreach (Frame decoded in decoder.Decode(input, frame))
                    {
                        Encode(decoded);
                    }
                }

                foreach (Frame decoded in decoder.Decode(null, frame))
                {
                    Encode(decoded);
                }

                Encode(null);
                writer.Complete();
            }
            finally
            {
                encoder?.Dispose();
                pool?.Dispose();
            }
        }

        Assert.AreEqual(TestMedia.FrameCount, frames);
        JsonElement probed = (
            await FFmpegCli.FFprobeAsync(
                cancellationToken,
                "-count_frames",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=codec_name,nb_read_frames",
                output
            )
        ).GetProperty("streams")[0];
        Assert.AreEqual(
            encoderName[..encoderName.IndexOf('_', StringComparison.Ordinal)],
            probed.GetProperty("codec_name").GetString()
        );
        Assert.AreEqual(
            $"{TestMedia.FrameCount}",
            probed.GetProperty("nb_read_frames").GetString()
        );

        double psnr = await FFmpegCli.PsnrAsync(["-i", output], ["-i", path], cancellationToken);
        Assert.IsGreaterThan(35.0, psnr, $"PSNR {psnr:F1} dB of the transcode against its input.");
    }

    private static CodecId ClipCodec(string clip) =>
        clip switch
        {
            "h264" => CodecId.H264,
            "hevc" or "hevc-720p-nvenc" => CodecId.Hevc,
            _ => CodecId.Av1,
        };

    private static string Md5(Frame frame)
    {
        byte[] image = new byte[frame.GetImageSize()];
        _ = frame.CopyImageTo(image);
        return Convert.ToHexStringLower(MD5.HashData(image));
    }
}
