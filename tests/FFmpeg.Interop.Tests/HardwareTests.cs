namespace FFmpeg.Interop.Tests;

/// <summary>
/// Hardware devices, surface pools, transfers and hardware decoding. Tests that need a real GPU are
/// tagged RequiresGpu; the Vulkan ones are tagged RequiresVulkan instead, because a software Vulkan
/// driver (Mesa's lavapipe) is enough for them and a CI runner can install one.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class HardwareTests
{
    public TestContext TestContext { get; set; } = null!;

    private static HardwareDeviceType PlatformType =>
        OperatingSystem.IsWindows() ? HardwareDeviceType.D3D11VA
        : OperatingSystem.IsMacOS() ? HardwareDeviceType.VideoToolbox
        : HardwareDeviceType.Vaapi;

    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    public void Compiled_IncludesThisPlatformsVideoApi() =>
        Assert.Contains(PlatformType, HardwareDeviceType.Compiled.ToList());

    [TestMethod]
    public void Create_DeviceThatDoesNotExist_ThrowsAndTryCreateReturnsFalse()
    {
        // D3D11VA falls back to the default adapter for an index that does not exist, so CUDA is used: it
        // fails for a missing device index, a missing driver, and a build without it alike.
        FFmpegException error = Assert.ThrowsExactly<FFmpegException>(() =>
            HardwareDevice.Create(HardwareDeviceType.Cuda, "99")
        );
        Assert.AreEqual("av_hwdevice_ctx_create", error.Operation);
        Assert.IsFalse(HardwareDevice.TryCreate(HardwareDeviceType.None, out HardwareDevice? none));
        Assert.IsNull(none);
    }

    [TestMethod]
    [TestCategory("RequiresGpu")]
    public void PlatformDevice_UploadAndDownload_RoundTripsThePixels()
    {
        using HardwareDevice device = HardwareDevice.Create(PlatformType);
        AssertTypedAccessors(device);
        RoundTrip(device, PlatformHardwareFormat());
    }

    [TestMethod]
    [TestCategory("RequiresVulkan")]
    public void VulkanDevice_UploadAndDownload_RoundTripsThePixels()
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vulkan);

        Assert.IsTrue(device.TryGetVulkan(out VulkanDevice handles));
        Assert.AreNotEqual(0, handles.Instance);
        Assert.AreNotEqual(0, handles.PhysicalDevice);
        Assert.AreNotEqual(0, handles.Device);
        Assert.AreEqual(PixelFormat.Vulkan, device.SurfaceFormat);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsFalse(device.TryGetD3D11(out _));
            Assert.IsFalse(device.TryGetD3D12(out _));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.IsFalse(device.TryGetVaapiDisplay(out _));
            Assert.IsFalse(device.TryGetDrmFileDescriptor(out int fd));
            Assert.AreEqual(-1, fd);
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.IsFalse(device.TryGetCudaContext(out _));
        }

        RoundTrip(
            device,
            PixelFormat.Vulkan,
            frame =>
            {
                Assert.IsTrue(frame.TryGetVulkanFrame(out VulkanFrame vulkan));
                Assert.IsGreaterThanOrEqualTo(1, vulkan.ImageCount);
                Assert.AreNotEqual(0, vulkan.GetImage(0));
                Assert.AreNotEqual(0, vulkan.GetSemaphore(0));
                _ = vulkan.Layout(0);
                _ = vulkan.SemaphoreValue(0);
                _ = vulkan.Tiling;
                unsafe
                {
                    Assert.IsTrue(vulkan.NativePointer is not null);
                }

                bool threw = false;
                try
                {
                    _ = vulkan.GetImage(vulkan.ImageCount);
                }
                catch (ArgumentOutOfRangeException)
                {
                    threw = true;
                }

                Assert.IsTrue(threw);
            }
        );
    }

    [TestMethod]
    [TestCategory("RequiresGpu")]
    public async Task HardwareDecode_H264_IsBitExactWithSoftwareDecode()
    {
        string path = await TestMedia.ClipAsync("h264", TestContext.CancellationToken);
        List<string> software = CodingTests.DecodeFrameMd5s(path, "h264", PixelFormat.Yuv420P);

        using HardwareDevice device = HardwareDevice.Create(PlatformType);
        List<string> hardware = [];
        using (MediaReader reader = MediaReader.Open(path))
        {
            MediaStream stream = reader.FindBestStream(MediaType.Video)!;
            using Decoder decoder = stream.CreateDecoder(
                options: new DecoderOptions { HardwareDevice = device }
            );
            using Packet packet = new();
            using Frame frame = new();
            using Frame downloaded = new();
            using Frame planar = new();
            using Scaler scaler = new(
                new ScalerOptions { Algorithm = ScaleAlgorithm.Point, AccurateRounding = true }
            );

            void Collect(Frame decoded)
            {
                Assert.IsTrue(decoded.IsHardwareFrame);
                Assert.AreEqual(PlatformHardwareFormat(), decoded.PixelFormat);
                downloaded.Reset();
                decoded.TransferTo(downloaded);
                Assert.AreEqual(PixelFormat.Nv12, downloaded.PixelFormat);

                // NV12 to planar 4:2:0 at the same size only moves chroma samples, so the comparison
                // stays exact.
                planar.Width = downloaded.Width;
                planar.Height = downloaded.Height;
                planar.PixelFormat = PixelFormat.Yuv420P;
                scaler.Scale(downloaded, planar);
                byte[] image = new byte[planar.GetImageSize()];
                _ = planar.CopyImageTo(image);
                hardware.Add(
                    Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(image))
                );
            }

            while (reader.TryReadPacket(packet))
            {
                foreach (Frame decoded in decoder.Decode(packet, frame))
                {
                    Collect(decoded);
                }
            }

            foreach (Frame decoded in decoder.Decode(null, frame))
            {
                Collect(decoded);
            }
        }

        CollectionAssert.AreEqual(software, hardware);
    }

    [TestMethod]
    [TestCategory("RequiresGpu")]
    public void Decoder_CodecWithoutHardwareSupport_RefusesTheDevice()
    {
        using HardwareDevice device = HardwareDevice.Create(PlatformType);

        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            Decoder.Create(
                Codec.FindDecoder("pcm_s16le"),
                new DecoderOptions { HardwareDevice = device }
            )
        );
    }

    [TestMethod]
    [TestCategory("RequiresGpu")]
    [TestCategory("RequiresVaapi")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Vaapi_MapToDrmPrime_ExposesDmaBufDescriptors()
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vaapi);
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.Vaapi,
            PixelFormat.Nv12,
            64,
            64
        );
        using Frame surface = new();
        pool.GetFrame(surface);
        Assert.IsTrue(surface.TryGetVaapiSurface(out _));

        using Frame drm = new();
        drm.PixelFormat = PixelFormat.DrmPrime;
        surface.MapTo(drm, HardwareMapAccess.Read);

        Assert.IsTrue(drm.TryGetDrmFrame(out DrmFrameDescriptor descriptor));
        Assert.IsGreaterThanOrEqualTo(1, descriptor.ObjectCount);
        Assert.IsGreaterThanOrEqualTo(1, descriptor.LayerCount);
        DrmObject dmaBuf = descriptor.GetObject(0);
        Assert.IsGreaterThanOrEqualTo(0, dmaBuf.FileDescriptor);
        Assert.IsGreaterThan(0L, dmaBuf.Size);
        Assert.AreNotEqual(0u, descriptor.GetLayerFormat(0));
        Assert.IsGreaterThanOrEqualTo(1, descriptor.GetPlaneCount(0));
        DrmPlane plane = descriptor.GetPlane(0, 0);
        Assert.IsGreaterThanOrEqualTo(64L, plane.Pitch);
        Assert.IsTrue(plane.ObjectIndex < descriptor.ObjectCount);
    }

    private static PixelFormat PlatformHardwareFormat() =>
        OperatingSystem.IsWindows() ? PixelFormat.D3D11
        : OperatingSystem.IsMacOS() ? PixelFormat.VideoToolbox
        : PixelFormat.Vaapi;

    private static void AssertTypedAccessors(HardwareDevice device)
    {
        Assert.AreEqual(PlatformType, device.Type);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(device.TryGetD3D11(out D3D11Device d3d11));
            Assert.AreNotEqual(0, d3d11.Device);
            Assert.AreNotEqual(0, d3d11.VideoDevice);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsTrue(device.TryGetVaapiDisplay(out nint display));
            Assert.AreNotEqual(0, display);
        }

        Assert.IsFalse(device.TryGetVulkan(out _));
        HardwareFrameConstraints constraints = device.GetFrameConstraints();
        Assert.Contains(PixelFormat.Nv12, constraints.SoftwareFormats.ToList());
        Assert.IsGreaterThan(64, constraints.MaxWidth);
    }

    // A D3D11 texture array with no bind flags cannot be created for NV12; decoder output is the use
    // FFmpeg's own D3D11 pools are made for.
    private static unsafe void UseAsDecoderTarget(HardwareFramePool pool)
    {
        const uint D3D11BindDecoder = 0x200;
        ((Native.AVD3D11VAFramesContext*)pool.Context->hwctx)->BindFlags = D3D11BindDecoder;
    }

    private static void RoundTrip(
        HardwareDevice device,
        PixelFormat hardwareFormat,
        Action<Frame>? inspect = null
    )
    {
        const int Width = 64;
        const int Height = 48;
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            hardwareFormat,
            PixelFormat.Nv12,
            Width,
            Height,
            initialSize: hardwareFormat == PixelFormat.D3D11 ? 4 : 0,
            configure: hardwareFormat == PixelFormat.D3D11 ? UseAsDecoderTarget : null
        );
        Assert.AreEqual(hardwareFormat, pool.Format);
        Assert.AreEqual(PixelFormat.Nv12, pool.SoftwareFormat);
        Assert.AreEqual(Width, pool.Width);
        Assert.AreEqual(Height, pool.Height);

        using Frame source = new();
        source.AllocateVideo(Width, Height, PixelFormat.Nv12);
        byte[] pixels = new byte[source.GetImageSize()];
        new Random(1234).NextBytes(pixels);
        source.CopyImageFrom(pixels);

        using Frame surface = new();
        pool.Upload(source, surface);
        Assert.IsTrue(surface.IsHardwareFrame);
        Assert.AreEqual(hardwareFormat, surface.PixelFormat);
        if (OperatingSystem.IsWindows() && hardwareFormat == PixelFormat.D3D11)
        {
            Assert.IsTrue(surface.TryGetD3D11Texture(out D3D11Texture texture));
            Assert.AreNotEqual(0, texture.Texture);
        }
        else if (OperatingSystem.IsMacOS() && hardwareFormat == PixelFormat.VideoToolbox)
        {
            Assert.IsTrue(surface.TryGetCVPixelBuffer(out nint buffer));
            Assert.AreNotEqual(0, buffer);
        }

        inspect?.Invoke(surface);

        using Frame downloaded = new();
        surface.TransferTo(downloaded);
        byte[] back = new byte[downloaded.GetImageSize()];
        _ = downloaded.CopyImageTo(back);
        CollectionAssert.AreEqual(pixels, back);
    }
}
