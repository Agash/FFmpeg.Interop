using System.Collections.Immutable;
using FFmpeg.Interop.Native;

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

    private static HardwareDeviceType PlatformType => TestNatives.PlatformVideoApi;

    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    public void Compiled_IncludesThisPlatformsVideoApi() =>
        Assert.Contains(
            PlatformType,
            HardwareDeviceType.Compiled.ToList(),
            $"Compiled: {string.Join(", ", HardwareDeviceType.Compiled)}."
        );

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
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
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
    [TestCategory("RequiresVulkan")]
    [DataRow("nv12")]
    [DataRow("yuv420p")]
    [DataRow("bgra")]
    public void VulkanFrames_CopyTo_CopiesThePictureOnTheGpu(string format)
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vulkan);
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Parse(format),
            64,
            48
        );
        (Frame source, byte[] pixels) = Uploaded(pool, seed: 21);
        using (source)
        {
            using Frame copy = new();
            pool.GetFrame(copy);
            source.PresentationTimestamp = 42;

            source.CopyTo(copy);

            Assert.AreEqual(42, copy.PresentationTimestamp);
            CollectionAssert.AreEqual(pixels, Downloaded(copy));
            CollectionAssert.AreEqual(pixels, Downloaded(source), "the source is left as it was");
        }
    }

    // The path a DMA-BUF takes into a Vulkan encoder: mapped into Vulkan (an image the encoder cannot
    // read from directly), then copied into a surface of the encoder's own pool.
    [TestMethod]
    [TestCategory("RequiresVulkan")]
    [OSCondition(OperatingSystems.Linux)]
    public void VulkanCopyTo_FromAMappedDmaBuf_CopiesThePicture()
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vulkan);
        RequireDmaBufSharing(device);

        using HardwareFramePool producer = DmaBufProducer(device, 64, 48);
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Nv12,
            64,
            48
        );
        using HardwareFramePool imports = HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Nv12,
            64,
            48
        );
        (Frame source, byte[] pixels) = Uploaded(producer, seed: 23);
        using (source)
        {
            using Frame drm = new();
            drm.PixelFormat = PixelFormat.DrmPrime;
            source.MapTo(drm, HardwareMapAccess.Read);
            using Frame mapped = new();
            drm.MapTo(imports, mapped, HardwareMapAccess.Read);
            using Frame copy = new();
            pool.GetFrame(copy);

            mapped.CopyTo(copy);

            CollectionAssert.AreEqual(pixels, Downloaded(copy));
        }
    }

    // A DMA-BUF imported for a pool's use is the producer's picture, read in place: copying it out of the
    // imported frame gives back what the producer wrote.
    [TestMethod]
    [TestCategory("RequiresVulkan")]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void VulkanDmaBufImporter_ImportedPicture_IsTheProducersPicture()
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vulkan);
        RequireDmaBufSharing(device);
        using HardwareFramePool producer = DmaBufProducer(device, 64, 48);

        // A pool read by copies, so the imported picture can be copied out and compared.
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Nv12,
            64,
            48,
            configure: static pool =>
            {
                unsafe
                {
                    ((AVVulkanFramesContext*)pool.Context->hwctx)->usage =
                        VkImageUsageFlagBits.VK_IMAGE_USAGE_TRANSFER_SRC_BIT
                        | VkImageUsageFlagBits.VK_IMAGE_USAGE_TRANSFER_DST_BIT
                        | VkImageUsageFlagBits.VK_IMAGE_USAGE_SAMPLED_BIT;
                }
            }
        );
        using VulkanDmaBufImporter importer = new(pool);
        (Frame source, byte[] pixels) = Uploaded(producer, seed: 29);
        using (source)
        {
            using Frame drm = new();
            drm.PixelFormat = PixelFormat.DrmPrime;
            source.MapTo(drm, HardwareMapAccess.Read);
            DrmPrimeImage image = SingleLayer(drm);
            Assert.Contains(image.Objects[0].Modifier, importer.SupportedModifiers);
            Assert.IsTrue(importer.Supports(image.Objects[0].Modifier));

            using Frame imported = new();
            importer.Import(image, 64, 48, imported);
            Assert.IsTrue(imported.TryGetVulkanFrame(out VulkanFrame view));
            Assert.AreEqual(1, view.ImageCount);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => importer.Release(imported));

            importer.Acquire(imported);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => importer.Acquire(imported));
            using Frame copy = new();
            pool.GetFrame(copy);
            imported.CopyTo(copy);
            importer.Release(imported);

            CollectionAssert.AreEqual(pixels, Downloaded(copy));
        }
    }

    [TestMethod]
    [TestCategory("RequiresVulkan")]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void VulkanDmaBufImporter_PictureOfSeveralObjectsOrAnUnknownModifier_IsRefused()
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vulkan);
        RequireDmaBufSharing(device);
        using HardwareFramePool pool = HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Nv12,
            64,
            48
        );
        using VulkanDmaBufImporter importer = new(pool);
        using Frame frame = new();
        DrmLayer layer = new(Nv12Fourcc, [new DrmPlane(0, 0, 64), new DrmPlane(1, 0, 64)]);
        DrmPrimeImage twoObjects = new(
            [new DrmObject(0, 4096, 0), new DrmObject(1, 4096, 0)],
            [layer]
        );
        DrmPrimeImage unknown = new(
            [new DrmObject(0, 8192, 0x00ffffffffffffff)],
            [new DrmLayer(Nv12Fourcc, [new DrmPlane(0, 0, 64), new DrmPlane(0, 4096, 64)])]
        );

        Assert.IsFalse(importer.Supports(0x00ffffffffffffff));
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            importer.Import(twoObjects, 64, 48, frame)
        );
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            importer.Import(unknown, 64, 48, frame)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() => importer.Acquire(frame));
    }

    [TestMethod]
    [TestCategory("RequiresVulkan")]
    public void VulkanFrames_CopyToAnotherSizeOrSystemMemory_Throws()
    {
        using HardwareDevice device = HardwareDevice.Create(HardwareDeviceType.Vulkan);
        using HardwareFramePool small = HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Nv12,
            64,
            48
        );
        using HardwareFramePool large = HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Nv12,
            128,
            96
        );
        using Frame from = new();
        small.GetFrame(from);
        using Frame to = new();
        large.GetFrame(to);
        using Frame system = new();
        system.AllocateVideo(64, 48, PixelFormat.Nv12);

        _ = Assert.ThrowsExactly<ArgumentException>(() => from.CopyTo(to));
        _ = Assert.ThrowsExactly<NotSupportedException>(() => from.CopyTo(system));
        _ = Assert.ThrowsExactly<NotSupportedException>(() => system.CopyTo(from));
    }

    [TestMethod]
    [TestCategory("RequiresHardwareDecoder")]
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
    [OSCondition(OperatingSystems.Linux)]
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
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
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
    internal static unsafe void UseAsDecoderTarget(HardwareFramePool pool)
    {
        const uint D3D11BindDecoder = 0x200;
        ((Native.AVD3D11VAFramesContext*)pool.Context->hwctx)->BindFlags = D3D11BindDecoder;
    }

    // DRM_FORMAT_NV12.
    internal const uint Nv12Fourcc = 0x3231564E;

    // The list a DMA-BUF producer's pool allocates from: linear, which every importer reads. Lives as long
    // as the process, as the pools that point at it allocate lazily.
    private static readonly unsafe VkImageDrmFormatModifierListCreateInfoEXT* LinearOnly = Linear();

    private static unsafe VkImageDrmFormatModifierListCreateInfoEXT* Linear()
    {
        ulong* modifiers = (ulong*)
            System.Runtime.InteropServices.NativeMemory.AllocZeroed(sizeof(ulong));
        var list = (VkImageDrmFormatModifierListCreateInfoEXT*)
            System.Runtime.InteropServices.NativeMemory.AllocZeroed(
                (nuint)sizeof(VkImageDrmFormatModifierListCreateInfoEXT)
            );
        list->sType =
            VkStructureType.VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_LIST_CREATE_INFO_EXT;
        list->drmFormatModifierCount = 1;
        list->pDrmFormatModifiers = modifiers;
        return list;
    }

    // A pool that hands its pictures out as DMA-BUFs, the way a producer allocates them: linear images
    // the application uploads into and others read.
    internal static HardwareFramePool DmaBufProducer(
        HardwareDevice device,
        int width,
        int height
    ) =>
        HardwareFramePool.Create(
            device,
            PixelFormat.Vulkan,
            PixelFormat.Nv12,
            width,
            height,
            configure: static pool =>
            {
                unsafe
                {
                    var frames = (AVVulkanFramesContext*)pool.Context->hwctx;
                    frames->tiling = VkImageTiling.VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
                    frames->usage =
                        VkImageUsageFlagBits.VK_IMAGE_USAGE_TRANSFER_DST_BIT
                        | VkImageUsageFlagBits.VK_IMAGE_USAGE_TRANSFER_SRC_BIT
                        | VkImageUsageFlagBits.VK_IMAGE_USAGE_SAMPLED_BIT;
                    frames->create_pnext = LinearOnly;

                    // Without flags FFmpeg adds a mutable format, which DRM-modifier tiling would need
                    // a format list for; a producer that writes the picture in its own format needs
                    // neither.
                    frames->img_flags = (uint)VkImageCreateFlagBits.VK_IMAGE_CREATE_ALIAS_BIT;
                }
            }
        );

    // A mapped picture as one NV12 layer: FFmpeg's mappings describe each plane as a layer of its own.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static DrmPrimeImage SingleLayer(Frame drm)
    {
        Assert.IsTrue(drm.TryGetDrmFrame(out DrmFrameDescriptor descriptor));
        ImmutableArray<DrmObject>.Builder objects = ImmutableArray.CreateBuilder<DrmObject>();
        for (int i = 0; i < descriptor.ObjectCount; i++)
        {
            objects.Add(descriptor.GetObject(i));
        }

        ImmutableArray<DrmPlane>.Builder planes = ImmutableArray.CreateBuilder<DrmPlane>();
        for (int layer = 0; layer < descriptor.LayerCount; layer++)
        {
            for (int plane = 0; plane < descriptor.GetPlaneCount(layer); plane++)
            {
                planes.Add(descriptor.GetPlane(layer, plane));
            }
        }

        return new DrmPrimeImage(
            objects.ToImmutable(),
            [new DrmLayer(Nv12Fourcc, planes.ToImmutable())]
        );
    }

    private static void RequireDmaBufSharing(HardwareDevice device)
    {
        if (
            !device.VulkanDeviceExtensions.Contains("VK_EXT_external_memory_dma_buf")
            || !device.VulkanDeviceExtensions.Contains("VK_EXT_image_drm_format_modifier")
        )
        {
            Assert.Inconclusive("This Vulkan driver cannot share images as DMA-BUFs.");
        }
    }

    // A pool surface holding a random picture, and the picture's packed bytes.
    private static (Frame Surface, byte[] Pixels) Uploaded(HardwareFramePool pool, int seed)
    {
        using Frame picture = new();
        picture.AllocateVideo(pool.Width, pool.Height, pool.SoftwareFormat);
        byte[] pixels = new byte[picture.GetImageSize()];
        new Random(seed).NextBytes(pixels);
        picture.CopyImageFrom(pixels);
        Frame surface = new();
        pool.Upload(picture, surface);
        return (surface, pixels);
    }

    private static byte[] Downloaded(Frame surface)
    {
        using Frame picture = new();
        surface.TransferTo(picture);
        byte[] pixels = new byte[picture.GetImageSize()];
        _ = picture.CopyImageTo(pixels);
        return pixels;
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
        if (
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
            && hardwareFormat == PixelFormat.D3D11
        )
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
