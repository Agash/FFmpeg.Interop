using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Win32.Graphics.Direct3D12;

namespace FFmpeg.Interop.Tests;

/// <summary>How an encoder test feeds the encoder.</summary>
public enum EncoderInput
{
    /// <summary>System-memory frames, the encoder pinned to the GPU by a device on it.</summary>
    SystemMemory,

    /// <summary>Surfaces uploaded into a frame pool on the device.</summary>
    Surfaces,

    /// <summary>
    /// Textures the application produced on the device, wrapped into the encoder's pool without a copy.
    /// </summary>
    WrappedD3D12Textures,

    /// <summary>
    /// DMA-BUFs another API produced on the GPU (VA-API here), imported into the encoder's pool without
    /// a copy and handed over to it and back.
    /// </summary>
    ImportedDmaBufs,
}

/// <summary>
/// Hardware decoders and encoders on real GPUs. No CI runner has one, so each machine's tests carry a
/// category and are run there:
/// <list type="bullet">
/// <item>RequiresNvidia: NVDEC/NVENC through CUDA, D3D11, D3D12 and Vulkan (Windows).</item>
/// <item>RequiresAmf: an AMD GPU through D3D11 and AMF (Windows).</item>
/// <item>RequiresQsv: an Intel GPU through Quick Sync (Windows and Linux).</item>
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
    [TestCategory("RequiresHardwareDecoder")]
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
    [TestCategory("RequiresHardwareDecoder")]
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
    [TestCategory("RequiresHardwareDecoder")]
    [TestCategory("RequiresVaapi")]
    [OSCondition(OperatingSystems.Linux)]
    [DataRow("h264")]
    [DataRow("hevc")]
    [DataRow("av1")]
    public Task VaapiDecode_IsBitExactWithSoftwareDecode(string clip) =>
        AssertDecodeMatchesSoftwareAsync(clip, GpuVendor.Amd, "vaapi", decoder: null);

    // A GPU whose driver lacks Vulkan Video for the codec is skipped; one that has it must decode.
    [TestMethod]
    [TestCategory("RequiresHardwareDecoder")]
    [TestCategory("RequiresVulkan")]
    [DataRow("h264", GpuVendor.Nvidia)]
    [DataRow("hevc", GpuVendor.Nvidia)]
    [DataRow("av1", GpuVendor.Nvidia)]
    [DataRow("h264", GpuVendor.Amd)]
    [DataRow("hevc", GpuVendor.Amd)]
    [DataRow("av1", GpuVendor.Amd)]
    public Task VulkanDecode_IsBitExactWithSoftwareDecode(string clip, GpuVendor vendor) =>
        AssertDecodeMatchesSoftwareAsync(clip, vendor, "vulkan", decoder: null);

    // Linux's zero-copy decode: pictures decoded into DMA-BUFs another device reads in place.
    [TestMethod]
    [TestCategory("RequiresHardwareDecoder")]
    [TestCategory("RequiresVulkan")]
    [OSCondition(OperatingSystems.Linux)]
    [DataRow("h264", GpuVendor.Nvidia)]
    [DataRow("hevc", GpuVendor.Nvidia)]
    [DataRow("av1", GpuVendor.Nvidia)]
    [DataRow("h264", GpuVendor.Amd)]
    [DataRow("hevc", GpuVendor.Amd)]
    [DataRow("av1", GpuVendor.Amd)]
    public Task VulkanDecode_IntoDmaBufs_IsBitExactWhereShared(string clip, GpuVendor vendor) =>
        AssertDecodeMatchesSoftwareAsync(
            clip,
            vendor,
            "vulkan",
            decoder: null,
            sharedAsDmaBufs: true
        );

    [TestMethod]
    [TestCategory("RequiresHardwareDecoder")]
    [TestCategory("RequiresVideoToolbox")]
    [OSCondition(OperatingSystems.OSX)]
    [DataRow("h264")]
    [DataRow("hevc")]
    public Task VideoToolboxDecode_IsBitExactWithSoftwareDecode(string clip) =>
        AssertDecodeMatchesSoftwareAsync(clip, GpuVendor.Apple, "videotoolbox", decoder: null);

    // Linux's zero-copy encode: a DMA-BUF from another producer read by Vulkan Video in place.
    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [TestCategory("RequiresVulkan")]
    [OSCondition(OperatingSystems.Linux)]
    [DataRow("h264_vulkan")]
    [DataRow("hevc_vulkan")]
    public async Task VulkanEncode_FromImportedDmaBufs_ProducesAStreamFFmpegReadsBack(
        string encoder
    )
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.Vulkan,
            Adapter(GpuVendor.Amd)
        );
        CodecId codec = Codec.FindEncoder(encoder).Id;
        if (!device.CanVulkanEncode(codec))
        {
            Assert.Inconclusive($"The AMD GPU's driver has no Vulkan Video encode for {codec}.");
        }

        await AssertEncodeOnDeviceAsync(encoder, device, EncoderInput.ImportedDmaBufs);
    }

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
    [OSCondition(OperatingSystems.Linux)]
    [DataRow("h264_vaapi", "vaapi", EncoderInput.Surfaces)]
    [DataRow("hevc_vaapi", "vaapi", EncoderInput.Surfaces)]
    public Task VaapiEncode_ProducesAStreamFFmpegReadsBack(
        string encoder,
        string deviceType,
        EncoderInput input
    ) => AssertEncodeAsync(encoder, GpuVendor.Amd, deviceType, input);

    [TestMethod]
    [TestCategory("RequiresVideoToolbox")]
    [OSCondition(OperatingSystems.OSX)]
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
    [OSCondition(OperatingSystems.Linux)]
    [DataRow("h264", "vaapi", "hevc_vaapi")]
    public Task VaapiTranscode_StaysOnTheGpu(string clip, string deviceType, string encoder) =>
        AssertGpuTranscodeAsync(clip, GpuVendor.Amd, deviceType, encoder);

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
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

    // FFmpeg's VA-API encoder crashes when it is flushed before its first frame, which is what ending
    // a pipeline whose first frame failed does.
    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [OSCondition(OperatingSystems.Linux)]
    public void VaapiEncoder_EndedBeforeAnyFrame_FinishesWithNoPackets()
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vaapi);
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            device.SurfaceFormat,
            PixelFormat.Nv12,
            TestMedia.Width,
            TestMedia.Height
        );
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder("h264_vaapi"),
            new VideoEncoderOptions
            {
                Width = TestMedia.Width,
                Height = TestMedia.Height,
                PixelFormat = pool.Format,
                TimeBase = new(1, TestMedia.FrameRate),
                FrameRate = new(TestMedia.FrameRate, 1),
                BitRate = 4_000_000,
                MaxBFrames = 0,
                HardwareFrames = pool,
            }
        );
        using Packet packet = new();

        int packets = 0;
        foreach (Packet _ in encoder.Encode(null, packet))
        {
            packets++;
        }

        Assert.AreEqual(0, packets);
    }

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
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
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
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
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
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
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
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

    // FFmpeg's D3D12 uninit does not release the device; a wrapped device must not keep the
    // application's alive.
    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public void FromD3D12Device_ReleasesTheApplicationsDeviceWhenFreed()
    {
        using HardwareDevice opened = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        Assert.IsTrue(opened.TryGetD3D12(out D3D12Device application));
        uint before = References(application.Device);

        HardwareDevice wrapped = HardwareDevice.FromD3D12Device(application.Device);
        // The wrapper holds the device, and FFmpeg's init takes its ID3D12VideoDevice, which is the
        // same object.
        Assert.IsGreaterThan(before, References(application.Device));
        wrapped.Dispose();

        Assert.AreEqual(before, References(application.Device));

        static uint References(nint unknown)
        {
            _ = Dxgi.AddRef(unknown);
            return Dxgi.Release(unknown);
        }
    }

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    [DataRow("h264_d3d12va")]
    [DataRow("hevc_d3d12va")]
    public async Task WrapD3D12Texture_EncodesApplicationTexturesWithoutACopy(string encoder)
    {
        using HardwareDevice opened = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        Assert.IsTrue(opened.TryGetD3D12(out D3D12Device application));
        using HardwareDevice device = HardwareDevice.FromD3D12Device(application.Device);

        await AssertEncodeOnDeviceAsync(encoder, device, EncoderInput.WrappedD3D12Textures);
    }

    // The frame's fence is signalled on the producer's queue behind the work already submitted to it,
    // so the encoder waits for the producer on the GPU. The queue is held back by a gate fence here,
    // standing in for rendering that has not finished yet.
    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public unsafe void WrapD3D12Texture_IsReadyWhenTheProducersQueueGetsThere()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        Assert.IsTrue(device.TryGetD3D12(out D3D12Device objects));
        using HardwareFramePool producer = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            64,
            48
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(64, 48, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(13).NextBytes(pixels);
        source.CopyImageFrom(pixels);
        using Frame rendered = new();
        producer.Upload(source, rendered);
        Assert.IsTrue(rendered.TryGetD3D12Texture(out D3D12Texture texture));

        ID3D12Device* d3d12 = (ID3D12Device*)objects.Device;
        D3D12_COMMAND_QUEUE_DESC description = new()
        {
            Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
        };
        d3d12->CreateCommandQueue(in description, out ID3D12CommandQueue* queue);
        d3d12->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, out ID3D12Fence* gate);
        try
        {
            queue->Wait(gate, 1);
            using Frame wrapped = new();
            pool.WrapD3D12Texture(texture.Resource, (nint)queue, wrapped);
            Assert.IsTrue(wrapped.TryGetD3D12Texture(out D3D12Texture frame));
            Assert.AreEqual(texture.Resource, frame.Resource);
            Assert.AreEqual(1UL, frame.FenceValue);
            ID3D12Fence* ready = (ID3D12Fence*)frame.Fence;
            Assert.AreEqual(
                0UL,
                ready->GetCompletedValue(),
                "The frame was ready before its producer."
            );

            gate->Signal(1);
            SpinWait.SpinUntil(() => ready->GetCompletedValue() >= 1, TimeSpan.FromSeconds(10));
            Assert.AreEqual(1UL, ready->GetCompletedValue());

            // Reading the wrapped frame reads the producer's texture: nothing was copied.
            using Frame back = new();
            wrapped.TransferTo(back);
            byte[] downloaded = new byte[back.GetImageSize()];
            _ = back.CopyImageTo(downloaded);
            CollectionAssert.AreEqual(pixels, downloaded);
        }
        finally
        {
            // The queue is idle once the gate is open and its signal has landed.
            gate->Signal(1);
            _ = gate->Release();
            _ = queue->Release();
        }
    }

    // A texture that is ready when another component's fence reaches a value: the frame's own fence
    // follows the producer's on the GPU, and the producer's timeline is left where it was.
    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public unsafe void WrapD3D12Texture_WithAReadyFence_FollowsItWithoutAdvancingIt()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        Assert.IsTrue(device.TryGetD3D12(out D3D12Device objects));
        using HardwareFramePool producer = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            64,
            48
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(64, 48, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(17).NextBytes(pixels);
        source.CopyImageFrom(pixels);
        using Frame rendered = new();
        producer.Upload(source, rendered);
        Assert.IsTrue(rendered.TryGetD3D12Texture(out D3D12Texture texture));

        ID3D12Device* d3d12 = (ID3D12Device*)objects.Device;
        d3d12->CreateFence(
            0,
            D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE,
            out ID3D12Fence* producerFence
        );
        try
        {
            using Frame wrapped = new();
            pool.WrapD3D12Texture(texture.Resource, (nint)producerFence, 5, wrapped);
            Assert.IsTrue(wrapped.TryGetD3D12Texture(out D3D12Texture frame));
            ID3D12Fence* ready = (ID3D12Fence*)frame.Fence;
            Assert.AreNotEqual(
                (nint)producerFence,
                frame.Fence,
                "The producer's fence was taken over."
            );
            Assert.AreEqual(
                0UL,
                ready->GetCompletedValue(),
                "The frame was ready before its producer."
            );

            producerFence->Signal(5);
            SpinWait.SpinUntil(
                () => ready->GetCompletedValue() >= frame.FenceValue,
                TimeSpan.FromSeconds(10)
            );
            Assert.AreEqual(frame.FenceValue, ready->GetCompletedValue());

            using Frame back = new();
            wrapped.TransferTo(back);
            byte[] downloaded = new byte[back.GetImageSize()];
            _ = back.CopyImageTo(downloaded);
            CollectionAssert.AreEqual(pixels, downloaded);
            Assert.AreEqual(
                5UL,
                producerFence->GetCompletedValue(),
                "The producer's fence was advanced."
            );
        }
        finally
        {
            producerFence->Signal(5);
            _ = producerFence->Release();
        }
    }

    // A decoder pads its surfaces past the picture; the copy takes the picture's region into a surface
    // of the pool's size, after the source's fence, on the GPU.
    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public unsafe void CopyFromD3D12Texture_TakesTheTopLeftOfALargerTexture()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        using HardwareFramePool padded = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            96,
            64
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(96, 64, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(19).NextBytes(pixels);
        source.CopyImageFrom(pixels);
        using Frame rendered = new();
        padded.Upload(source, rendered);
        Assert.IsTrue(rendered.TryGetD3D12Texture(out D3D12Texture texture));

        using Frame copy = new();
        pool.CopyFromD3D12Texture(
            texture.Resource,
            texture.Subresource,
            texture.Fence,
            texture.FenceValue,
            copy
        );
        using Frame back = new();
        copy.TransferTo(back);

        // NV12 at 96x64: luma rows of 96, then interleaved chroma rows of 96.
        for (int y = 0; y < 48; y++)
        {
            CollectionAssert.AreEqual(
                pixels.AsSpan(y * 96, 64).ToArray(),
                back.GetPlane(0).GetRow(y)[..64].ToArray(),
                $"Luma row {y}."
            );
        }

        for (int y = 0; y < 24; y++)
        {
            CollectionAssert.AreEqual(
                pixels.AsSpan((96 * 64) + (y * 96), 64).ToArray(),
                back.GetPlane(1).GetRow(y)[..64].ToArray(),
                $"Chroma row {y}."
            );
        }
    }

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public void WrapD3D12Texture_RefusesTexturesThePoolCannotHold()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.D3D12VA,
            Adapter(GpuVendor.Nvidia)
        );
        using HardwareFramePool producer = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame rendered = new();
        producer.GetFrame(rendered);
        Assert.IsTrue(rendered.TryGetD3D12Texture(out D3D12Texture texture));
        using Frame wrapped = new();

        using HardwareFramePool p010 = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.P010,
            64,
            48
        );
        using HardwareFramePool larger = HardwareFramePool.Create(
            device,
            PixelFormat.D3D12,
            PixelFormat.Nv12,
            128,
            96
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            p010.WrapD3D12Texture(texture.Resource, 0, wrapped)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            larger.WrapD3D12Texture(texture.Resource, 0, wrapped)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            producer.WrapD3D12Texture(0, 0, wrapped)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            producer.WrapD3D12Texture(texture.Resource, 0, null!)
        );

        using HardwareDevice d3d11 = HardwareDevice.Create(
            HardwareDeviceType.D3D11VA,
            Adapter(GpuVendor.Nvidia)
        );
        using HardwareFramePool d3d11Pool = HardwareFramePool.Create(
            d3d11,
            PixelFormat.D3D11,
            PixelFormat.Nv12,
            64,
            48
        );
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            d3d11Pool.WrapD3D12Texture(texture.Resource, 0, wrapped)
        );
    }

    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    // FFmpeg derives VA-API and Vulkan from a DRM device, not the reverse: the DRM render node is the
    // Linux identity of a GPU, and everything else is opened from it.
    public void DrmAdapter_DerivesVaapiAndVulkanOnTheSameGpu()
    {
        GpuAdapter amd = Adapter(GpuVendor.Amd);
        Assert.IsNotNull(amd.RenderNode);
        using HardwareDevice drm = HardwareDevice.Create(HardwareDeviceType.Drm, amd);
        using HardwareDevice vaapi = drm.Derive(HardwareDeviceType.Vaapi);
        using HardwareDevice vulkan = HardwareDevice.Create(HardwareDeviceType.Vulkan, amd);

        Assert.IsTrue(vaapi.TryGetVaapiDisplay(out nint display));
        Assert.AreNotEqual(0, display);
        Assert.IsTrue(drm.TryGetDrmFileDescriptor(out int fd));
        Assert.IsGreaterThanOrEqualTo(0, fd);
        Assert.IsTrue(vulkan.TryGetVulkan(out VulkanDevice handles));
        Assert.AreNotEqual(0, handles.Device);
    }

    // Congestion control lowers the rate mid-stream; NVENC must apply it to the frames that follow.
    [TestMethod]
    [TestCategory("RequiresNvidia")]
    public void Nvenc_SetRateControl_ChangesTheRateOfTheFollowingFrames()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.Cuda,
            Adapter(GpuVendor.Nvidia)
        );
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder("h264_nvenc"),
            new VideoEncoderOptions
            {
                Width = 640,
                Height = 360,
                PixelFormat = PixelFormat.Nv12,
                TimeBase = new(1, 30),
                FrameRate = new(30, 1),
                BitRate = 8_000_000,
                MaxRate = 8_000_000,
                BufferSize = 4_000_000,
                MaxBFrames = 0,
                LowDelay = true,
                HardwareDevice = device,
                CodecOptions = new Dictionary<string, string> { ["rc"] = "cbr", ["tune"] = "ll" },
            }
        );
        Assert.IsTrue(encoder.SupportsRateControlChanges);
        AssertRateFollowsChange(encoder);
    }

    // Quick Sync takes a rate change by resetting its session; the frames after it follow the new rate.
    [TestMethod]
    [TestCategory("RequiresQsv")]
    public void Qsv_SetRateControl_ChangesTheRateOfTheFollowingFrames()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.Qsv,
            Adapter(GpuVendor.Intel)
        );
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder("h264_qsv"),
            new VideoEncoderOptions
            {
                Width = 640,
                Height = 360,
                PixelFormat = PixelFormat.Nv12,
                TimeBase = new(1, 30),
                FrameRate = new(30, 1),
                BitRate = 8_000_000,
                MaxRate = 8_000_000,
                BufferSize = 4_000_000,
                MaxBFrames = 0,
                LowDelay = true,
                HardwareDevice = device,
                CodecOptions = new Dictionary<string, string>
                {
                    ["preset"] = "veryfast",
                    ["low_delay_brc"] = "1",
                    ["async_depth"] = "1",
                },
            }
        );
        Assert.IsTrue(encoder.SupportsRateControlChanges);
        AssertRateFollowsChange(encoder);
    }

    [TestMethod]
    [TestCategory("RequiresNvidia")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public void CopyFromD3D11Texture_CopiesOnTheGpuAndRefusesAnotherDevicesTexture()
    {
        using HardwareDevice nvidia = HardwareDevice.Create(
            HardwareDeviceType.D3D11VA,
            Adapter(GpuVendor.Nvidia)
        );
        using HardwareFramePool capture = HardwareFramePool.Create(
            nvidia,
            PixelFormat.D3D11,
            PixelFormat.Nv12,
            64,
            48
        );
        using HardwareFramePool encoderPool = HardwareFramePool.Create(
            nvidia,
            PixelFormat.D3D11,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(64, 48, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(11).NextBytes(pixels);
        source.CopyImageFrom(pixels);
        using Frame captured = new();
        capture.Upload(source, captured);
        Assert.IsTrue(captured.TryGetD3D11Texture(out D3D11Texture texture));

        using Frame copy = new();
        encoderPool.CopyFromD3D11Texture(texture.Texture, texture.ArraySlice, copy);
        using Frame downloaded = new();
        copy.TransferTo(downloaded);
        byte[] back = new byte[downloaded.GetImageSize()];
        _ = downloaded.CopyImageTo(back);
        CollectionAssert.AreEqual(pixels, back);

        // The same texture handed to a pool on the other GPU: a cross-device copy is undefined in D3D11.
        using HardwareDevice amd = HardwareDevice.Create(
            HardwareDeviceType.D3D11VA,
            Adapter(GpuVendor.Amd)
        );
        using HardwareFramePool otherGpu = HardwareFramePool.Create(
            amd,
            PixelFormat.D3D11,
            PixelFormat.Nv12,
            64,
            48
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            otherGpu.CopyFromD3D11Texture(texture.Texture, texture.ArraySlice, copy)
        );

        // A texture in another format, or smaller than the pool, cannot fill its surfaces.
        using HardwareFramePool p010 = HardwareFramePool.Create(
            nvidia,
            PixelFormat.D3D11,
            PixelFormat.P010,
            64,
            48
        );
        using HardwareFramePool larger = HardwareFramePool.Create(
            nvidia,
            PixelFormat.D3D11,
            PixelFormat.Nv12,
            128,
            96
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            p010.CopyFromD3D11Texture(texture.Texture, texture.ArraySlice, copy)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            larger.CopyFromD3D11Texture(texture.Texture, texture.ArraySlice, copy)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            encoderPool.CopyFromD3D11Texture(0, 0, copy)
        );
    }

    // A PipeWire screencast hands over DMA-BUFs; they are imported as a DRM PRIME frame and mapped into
    // the encoder's VA-API surfaces without a copy. The DMA-BUFs here come from exporting a VA-API
    // surface, which gives descriptors exactly as a producer's would be.
    [TestMethod]
    [TestCategory("RequiresVaapi")]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void DrmPrimeImport_MapsDmaBufsIntoVaapiSurfaces()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.Vaapi,
            Adapter(GpuVendor.Amd)
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.Vaapi,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(64, 48, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(5).NextBytes(pixels);
        source.CopyImageFrom(pixels);
        using Frame producer = new();
        pool.Upload(source, producer);

        using Frame exported = new();
        exported.PixelFormat = PixelFormat.DrmPrime;
        producer.MapTo(exported, HardwareMapAccess.Read);
        Assert.IsTrue(exported.TryGetDrmFrame(out DrmFrameDescriptor descriptor));
        // The descriptor view is a ref struct over FFmpeg's memory, so it is read with plain loops.
        List<DrmObject> objects = [];
        for (int o = 0; o < descriptor.ObjectCount; o++)
        {
            objects.Add(descriptor.GetObject(o));
        }

        List<DrmLayer> layers = [];
        for (int l = 0; l < descriptor.LayerCount; l++)
        {
            List<DrmPlane> planes = [];
            for (int p = 0; p < descriptor.GetPlaneCount(l); p++)
            {
                planes.Add(descriptor.GetPlane(l, p));
            }

            layers.Add(new(descriptor.GetLayerFormat(l), [.. planes]));
        }

        using Frame imported = Frame.FromDrmPrime(
            new DrmPrimeImage([.. objects], [.. layers]),
            64,
            48
        );
        using Frame surface = new();
        imported.MapTo(pool, surface, HardwareMapAccess.Read);
        Assert.IsTrue(surface.TryGetVaapiSurface(out _));

        using Frame downloaded = new();
        surface.TransferTo(downloaded);
        byte[] back = new byte[downloaded.GetImageSize()];
        _ = downloaded.CopyImageTo(back);
        CollectionAssert.AreEqual(pixels, back);
    }

    [TestMethod]
    [TestCategory("RequiresVideoToolbox")]
    [OSCondition(OperatingSystems.OSX)]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void WrapCVPixelBuffer_SharesThePixelBufferWithoutACopy()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.VideoToolbox,
            Adapter(GpuVendor.Apple)
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.VideoToolbox,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(64, 48, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(9).NextBytes(pixels);
        source.CopyImageFrom(pixels);
        using Frame producer = new();
        pool.Upload(source, producer);
        Assert.IsTrue(producer.TryGetCVPixelBuffer(out nint pixelBuffer));

        using Frame wrapped = new();
        pool.WrapCVPixelBuffer(pixelBuffer, wrapped);
        producer.Reset();

        Assert.IsTrue(wrapped.TryGetCVPixelBuffer(out nint shared));
        Assert.AreEqual(pixelBuffer, shared);
        using Frame downloaded = new();
        wrapped.TransferTo(downloaded);
        byte[] back = new byte[downloaded.GetImageSize()];
        _ = downloaded.CopyImageTo(back);
        CollectionAssert.AreEqual(
            pixels,
            back,
            "The wrapped frame keeps the pixel buffer alive after its producer let go."
        );
    }

    [TestMethod]
    [TestCategory("RequiresVideoToolbox")]
    [OSCondition(OperatingSystems.OSX)]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void WrapIOSurface_SharesTheSurfaceWithoutACopy()
    {
        using HardwareDevice device = HardwareDevice.Create(
            HardwareDeviceType.VideoToolbox,
            Adapter(GpuVendor.Apple)
        );
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.VideoToolbox,
            PixelFormat.Nv12,
            64,
            48
        );
        using Frame source = new();
        source.AllocateVideo(64, 48, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(11).NextBytes(pixels);
        source.CopyImageFrom(pixels);
        using Frame producer = new();
        pool.Upload(source, producer);
        Assert.IsTrue(producer.TryGetCVPixelBuffer(out nint pixelBuffer));
        nint surface = CVPixelBufferGetIOSurface(pixelBuffer);
        Assert.AreNotEqual(0, surface, "VideoToolbox pools are IOSurface-backed.");

        using Frame wrapped = new();
        pool.WrapIOSurface(surface, wrapped);
        Assert.IsTrue(wrapped.TryGetCVPixelBuffer(out nint shared));
        Assert.AreEqual(surface, CVPixelBufferGetIOSurface(shared));
        Assert.IsTrue(wrapped.TryGetIOSurface(out nint wrappedSurface));
        Assert.AreEqual(surface, wrappedSurface);
        using Frame downloaded = new();
        wrapped.TransferTo(downloaded);
        byte[] back = new byte[downloaded.GetImageSize()];
        _ = downloaded.CopyImageTo(back);
        CollectionAssert.AreEqual(pixels, back);

        using HardwareFramePool smaller = HardwareFramePool.Create(
            device,
            PixelFormat.VideoToolbox,
            PixelFormat.Nv12,
            32,
            32
        );
        using Frame refused = new();
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() =>
            smaller.WrapIOSurface(surface, refused)
        );
        StringAssert.Contains(error.Message, "64x48 '420v'");
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => pool.WrapIOSurface(0, refused));
    }

    [System.Runtime.InteropServices.DllImport(
        "/System/Library/Frameworks/CoreVideo.framework/CoreVideo"
    )]
    private static extern nint CVPixelBufferGetIOSurface(nint pixelBuffer);

    // Encodes four seconds of noise at the encoder's rate, lowers the rate to 500 kb/s after two, and
    // checks the frames after the change come out at well under a quarter of the size.
    private static void AssertRateFollowsChange(Encoder encoder)
    {
        using Frame frame = new();
        using Packet packet = new();
        Random noise = new(3);
        long before = 0;
        long after = 0;
        for (int i = 0; i < 120; i++)
        {
            if (i == 60)
            {
                encoder.SetRateControl(500_000, 500_000, 250_000);
                Assert.AreEqual(500_000, encoder.BitRate);
            }

            // Noise is incompressible, so the output size follows the rate control, not the content.
            frame.AllocateVideo(640, 360, PixelFormat.Nv12);
            ImagePlane luma = frame.GetWritablePlane(0);
            for (int row = 0; row < luma.Height; row++)
            {
                noise.NextBytes(luma.GetRow(row));
            }

            frame.PresentationTimestamp = i;
            foreach (Packet encoded in encoder.Encode(frame, packet))
            {
                // The first frames after the change still drain the old rate's buffer.
                if (encoded.PresentationTimestamp < 60)
                {
                    before += encoded.Size;
                }
                else if (encoded.PresentationTimestamp >= 75)
                {
                    after += encoded.Size;
                }
            }
        }

        double beforeRate = before * 8.0 / 2.0; // 60 frames: two seconds.
        double afterRate = after * 8.0 / 1.5; // 45 frames: a second and a half.
        Assert.IsGreaterThan(
            4.0,
            beforeRate / afterRate,
            $"{beforeRate / 1e6:F2} Mb/s before, {afterRate / 1e6:F2} Mb/s after."
        );
    }

    internal static GpuAdapter Adapter(GpuVendor vendor)
    {
        ImmutableArray<GpuAdapter> adapters = GpuAdapter.Enumerate();
        GpuAdapter? adapter = adapters.FirstOrDefault(a => a.Vendor == vendor && !a.IsSoftware);
        Assert.IsNotNull(adapter, $"No {vendor} GPU among: {string.Join(", ", adapters)}.");
        return adapter;
    }

    private static bool HasAdapter(GpuVendor vendor) =>
        GpuAdapter.Enumerate().Any(a => a.Vendor == vendor && !a.IsSoftware);

    private static HardwareDeviceType DeviceType(string name) =>
        HardwareDeviceType.TryParse(name, out HardwareDeviceType type)
            ? type
            : throw new ArgumentException($"Unknown device type {name}.");

    private async Task AssertDecodeMatchesSoftwareAsync(
        string clip,
        GpuVendor vendor,
        string deviceType,
        string? decoder,
        bool sharedAsDmaBufs = false
    )
    {
        string path = await TestMedia.ClipAsync(clip, TestContext.CancellationToken);
        List<string> software = CodingTests.DecodeFrameMd5s(
            path,
            Codec.FindDecoder(ClipCodec(clip)).Name,
            PixelFormat.Yuv420P
        );
        Assert.HasCount(TestMedia.FrameCount, software);

        // The Vulkan rows span vendors and run wherever a Vulkan loader is; the vendor-specific suites
        // are picked by the GPU present, so their adapter is required.
        if (deviceType == "vulkan" && !HasAdapter(vendor))
        {
            Assert.Inconclusive($"No {vendor} GPU on this machine.");
        }

        using HardwareDevice device = HardwareDevice.Create(
            DeviceType(deviceType),
            Adapter(vendor)
        );
        if (device.Type == HardwareDeviceType.Vulkan && !device.CanVulkanDecode(ClipCodec(clip)))
        {
            Assert.Inconclusive(
                $"The {vendor} GPU's driver has no Vulkan Video decode for {clip}."
            );
        }

        // A consumer of the shared pictures: another Vulkan device, reading each one from its DMA-BUF.
        using HardwareDevice? consumer = sharedAsDmaBufs
            ? HardwareDevice.Create(HardwareDeviceType.Vulkan, Adapter(vendor))
            : null;
        HardwareFramePool? imports = null;
        List<string> hardware = [];
        using (MediaReader reader = MediaReader.Open(path))
        using (
            Decoder hardwareDecoder = reader
                .Streams[0]
                .CreateDecoder(
                    decoder is null ? null : Codec.FindDecoder(decoder),
                    new DecoderOptions
                    {
                        HardwareDevice = device,
                        DrmModifiers = sharedAsDmaBufs ? [LinearModifier] : [],
                    }
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
                if (consumer is not null && OperatingSystem.IsLinux())
                {
                    using Frame drm = new();
                    drm.PixelFormat = PixelFormat.DrmPrime;
                    decoded.MapTo(drm, HardwareMapAccess.Read);
                    DrmPrimeImage image = HardwareTests.SingleLayer(drm);
                    Assert.AreEqual(LinearModifier, image.Objects[0].Modifier);
                    imports ??= HardwareFramePool.Create(
                        consumer,
                        PixelFormat.Vulkan,
                        PixelFormat.Nv12,
                        decoded.Width,
                        decoded.Height
                    );
                    using Frame mapped = new();
                    drm.MapTo(imports, mapped, HardwareMapAccess.Read);
                    mapped.TransferTo(downloaded);
                }
                else
                {
                    decoded.TransferTo(downloaded);
                }

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

        imports?.Dispose();
        CollectionAssert.AreEqual(
            software,
            hardware,
            $"{clip} on {deviceType} differs from the software decoder."
        );
    }

    private const ulong LinearModifier = 0;

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
            input == EncoderInput.SystemMemory
                ? null
                : HardwareFramePool.Create(
                    device,
                    device.SurfaceFormat,
                    PixelFormat.Nv12,
                    TestMedia.Width,
                    TestMedia.Height
                );

        // Stands in for the application's renderer: its own textures on the encoder's device. Each
        // stays alive, unwritten, until the encoder is done with it.
        using HardwareFramePool? producer =
            input == EncoderInput.WrappedD3D12Textures
                ? HardwareFramePool.Create(
                    device,
                    PixelFormat.D3D12,
                    PixelFormat.Nv12,
                    TestMedia.Width,
                    TestMedia.Height
                )
                : null;
        List<Frame> produced = [];

        // Stands in for another producer of DMA-BUFs on the GPU: VA-API surfaces, exported.
        using HardwareDevice? vaapi =
            input == EncoderInput.ImportedDmaBufs
                ? HardwareDevice.Create(HardwareDeviceType.Vaapi, Adapter(GpuVendor.Amd))
                : null;
        using HardwareFramePool? dmaBufs = vaapi is null
            ? null
            : HardwareFramePool.Create(
                vaapi,
                PixelFormat.Vaapi,
                PixelFormat.Nv12,
                TestMedia.Width,
                TestMedia.Height
            );
        using VulkanDmaBufImporter? importer =
            dmaBufs is not null && pool is not null && OperatingSystem.IsLinux()
                ? new VulkanDmaBufImporter(pool)
                : null;
        List<Frame> imported = [];

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
                if (
                    producer is not null
                    && pool is not null
                    && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
                )
                {
                    Frame texture = new();
                    produced.Add(texture);
                    producer.Upload(frame, texture);
                    Assert.IsTrue(texture.TryGetD3D12Texture(out D3D12Texture rendered));
                    pool.WrapD3D12Texture(rendered.Resource, 0, surface);
                    Assert.IsTrue(surface.TryGetD3D12Texture(out D3D12Texture wrapped));
                    Assert.AreEqual(rendered.Resource, wrapped.Resource, "The texture was copied.");
                    fed = surface;
                }
                else if (importer is not null && dmaBufs is not null && OperatingSystem.IsLinux())
                {
                    Frame vaSurface = new();
                    produced.Add(vaSurface);
                    dmaBufs.Upload(frame, vaSurface);
                    using Frame drm = new();
                    drm.PixelFormat = PixelFormat.DrmPrime;
                    vaSurface.MapTo(drm, HardwareMapAccess.Read);
                    DrmPrimeImage image = HardwareTests.SingleLayer(drm);
                    Assert.IsTrue(
                        importer.Supports(image.Objects[0].Modifier),
                        $"Modifier 0x{image.Objects[0].Modifier:x16} is not importable for encoding."
                    );
                    Frame vulkan = new();
                    imported.Add(vulkan);
                    importer.Import(image, TestMedia.Width, TestMedia.Height, vulkan);
                    importer.Acquire(vulkan);
                    fed = vulkan;
                }
                else if (pool is not null)
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

        // The encoder is drained: every imported picture goes back to its producer.
        foreach (Frame vulkan in imported)
        {
            if (OperatingSystem.IsLinux())
            {
                importer!.Release(vulkan);
            }

            vulkan.Dispose();
        }

        foreach (Frame texture in produced)
        {
            texture.Dispose();
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
