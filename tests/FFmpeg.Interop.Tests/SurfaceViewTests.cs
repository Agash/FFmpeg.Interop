using FFmpeg.Interop.Native;

namespace FFmpeg.Interop.Tests;

/// <summary>
/// The typed GPU surface views and the hardware format callback, driven with hand-built native
/// structures so they are checked on every machine, not only where the hardware exists.
/// </summary>
[TestClass]
public sealed unsafe class SurfaceViewTests
{
    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    public void TryGet_FrameWithTheMatchingFormat_ReadsTheNativeSlots()
    {
        using Frame frame = new();
        AVFrame* native = frame.NativePointer;
        try
        {
            native->data[0] = (byte*)0x1000;
            native->data[1] = (byte*)3;
            native->data[3] = (byte*)0x2000;
            native->linesize[0] = 256;

            // Each platform checks the surfaces it has.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            {
                frame.PixelFormat = PixelFormat.D3D11;
                Assert.IsTrue(frame.TryGetD3D11Texture(out D3D11Texture texture));
                Assert.AreEqual(new D3D11Texture(0x1000, 3), texture);
            }

            if (OperatingSystem.IsMacOS())
            {
                frame.PixelFormat = PixelFormat.VideoToolbox;
                Assert.IsTrue(frame.TryGetCVPixelBuffer(out nint pixelBuffer));
                Assert.AreEqual(0x2000, pixelBuffer);
            }

            if (OperatingSystem.IsLinux())
            {
                frame.PixelFormat = PixelFormat.Vaapi;
                Assert.IsTrue(frame.TryGetVaapiSurface(out uint surface));
                Assert.AreEqual(0x2000u, surface);
            }

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                frame.PixelFormat = PixelFormat.Cuda;
                Assert.IsTrue(frame.TryGetCudaPlane(0, out nint devicePointer, out int pitch));
                Assert.AreEqual(0x1000, devicePointer);
                Assert.AreEqual(256, pitch);
                Assert.IsFalse(frame.TryGetCudaPlane(2, out _, out _), "No plane 2.");
                Assert.IsFalse(frame.TryGetCudaPlane(8, out _, out _));
            }
        }
        finally
        {
            // The pointers are not FFmpeg's; clear them so freeing the frame does not touch them.
            *native = default;
        }
    }

    [TestMethod]
    public void TryGet_FormatWithoutData_ReturnsFalse()
    {
        using Frame frame = new();

        frame.PixelFormat = PixelFormat.Vulkan;
        Assert.IsFalse(frame.TryGetVulkanFrame(out _));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            frame.PixelFormat = PixelFormat.D3D11;
            Assert.IsFalse(frame.TryGetD3D11Texture(out _));
            frame.PixelFormat = PixelFormat.D3D12;
            Assert.IsFalse(frame.TryGetD3D12Texture(out _));
        }

        if (OperatingSystem.IsLinux())
        {
            frame.PixelFormat = PixelFormat.DrmPrime;
            Assert.IsFalse(frame.TryGetDrmFrame(out _));
        }

        if (OperatingSystem.IsMacOS())
        {
            frame.PixelFormat = PixelFormat.VideoToolbox;
            Assert.IsFalse(frame.TryGetCVPixelBuffer(out _));
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    [OSCondition(OperatingSystems.Windows)]
    public void TryGetD3D12Texture_ReadsTheResourceAndItsFence()
    {
        AVD3D12VAFrame d3d12 = default;
        d3d12.texture = (void*)0x40;
        d3d12.subresource_index = 2;
        d3d12.sync_ctx.fence = (void*)0x50;
        d3d12.sync_ctx.fence_value = 9;
        using Frame frame = new();
        AVFrame* native = frame.NativePointer;
        try
        {
            native->data[0] = (byte*)&d3d12;
            frame.PixelFormat = PixelFormat.D3D12;

            Assert.IsTrue(frame.TryGetD3D12Texture(out D3D12Texture texture));
            Assert.AreEqual(new D3D12Texture(0x40, 2, 0x50, 9), texture);
        }
        finally
        {
            *native = default;
        }
    }

    [TestMethod]
    public void DrmFrameDescriptor_ReadsObjectsLayersAndPlanes()
    {
        AVDRMFrameDescriptor descriptor = default;
        descriptor.nb_objects = 1;
        descriptor.objects[0] = new AVDRMObjectDescriptor
        {
            fd = 42,
            size = 4096,
            format_modifier = 0x0100000000000001,
        };
        descriptor.nb_layers = 1;
        descriptor.layers[0].format = 0x3231564E; // NV12
        descriptor.layers[0].nb_planes = 2;
        descriptor.layers[0].planes[0] = new AVDRMPlaneDescriptor
        {
            object_index = 0,
            offset = 0,
            pitch = 128,
        };
        descriptor.layers[0].planes[1] = new AVDRMPlaneDescriptor
        {
            object_index = 0,
            offset = 8192,
            pitch = 128,
        };

        using Frame frame = new();
        AVFrame* native = frame.NativePointer;
        try
        {
            native->data[0] = (byte*)&descriptor;
            frame.PixelFormat = PixelFormat.DrmPrime;

            // The descriptor view is plain data and is checked everywhere; reaching it from a frame is Linux only.
            DrmFrameDescriptor view = new(&descriptor);
            if (OperatingSystem.IsLinux())
            {
                Assert.IsTrue(frame.TryGetDrmFrame(out view));
            }

            Assert.IsTrue(view.NativePointer == &descriptor);
            Assert.AreEqual(1, view.ObjectCount);
            Assert.AreEqual(new DrmObject(42, 4096, 0x0100000000000001), view.GetObject(0));
            Assert.AreEqual(1, view.LayerCount);
            Assert.AreEqual(0x3231564Eu, view.GetLayerFormat(0));
            Assert.AreEqual(2, view.GetPlaneCount(0));
            Assert.AreEqual(new DrmPlane(0, 8192, 128), view.GetPlane(0, 1));
            nint address = (nint)(&descriptor);
            Assert.IsTrue(
                Throws(() => new DrmFrameDescriptor((AVDRMFrameDescriptor*)address).GetObject(1))
            );
            Assert.IsTrue(
                Throws(() => new DrmFrameDescriptor((AVDRMFrameDescriptor*)address).GetPlane(1, 0))
            );
            Assert.IsTrue(
                Throws(() => new DrmFrameDescriptor((AVDRMFrameDescriptor*)address).GetPlane(0, 2))
            );
        }
        finally
        {
            *native = default;
        }
    }

    [TestMethod]
    public void VulkanFrame_ReadsImagesAndExposesWritableSyncState()
    {
        AVVkFrame vk = default;
        vk.img[0] = (void*)0x10;
        vk.img[1] = (void*)0x20;
        vk.sem[0] = (void*)0x30;
        vk.sem_value[1] = 5;
        vk.tiling = VkImageTiling.VK_IMAGE_TILING_OPTIMAL;

        using Frame frame = new();
        AVFrame* native = frame.NativePointer;
        try
        {
            native->data[0] = (byte*)&vk;
            frame.PixelFormat = PixelFormat.Vulkan;

            Assert.IsTrue(frame.TryGetVulkanFrame(out VulkanFrame view));
            Assert.AreEqual(2, view.ImageCount);
            Assert.AreEqual(0x20, view.GetImage(1));
            Assert.AreEqual(0x30, view.GetSemaphore(0));
            Assert.AreEqual(VkImageTiling.VK_IMAGE_TILING_OPTIMAL, view.Tiling);
            Assert.AreEqual(5UL, view.SemaphoreValue(1));
            view.SemaphoreValue(1) = 6;
            view.Layout(0) = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL;
            Assert.AreEqual(6UL, vk.sem_value[1]);
            Assert.AreEqual(VkImageLayout.VK_IMAGE_LAYOUT_GENERAL, vk.layout[0]);
            Assert.IsTrue(view.NativePointer == &vk);
            nint address = (nint)(&vk);
            Assert.IsTrue(Throws(() => new VulkanFrame((AVVkFrame*)address).GetImage(2)));
        }
        finally
        {
            *native = default;
        }
    }

    [TestMethod]
    public void SelectFormat_OfferedHardwareFormat_IsChosen()
    {
        AVPixelFormat[] offered =
        [
            AVPixelFormat.AV_PIX_FMT_D3D11,
            AVPixelFormat.AV_PIX_FMT_YUV420P,
            AVPixelFormat.AV_PIX_FMT_NONE,
        ];

        Assert.AreEqual(
            AVPixelFormat.AV_PIX_FMT_D3D11,
            Select(AVPixelFormat.AV_PIX_FMT_D3D11, fallback: false, offered)
        );
    }

    [TestMethod]
    public void SelectFormat_HardwareFormatNotOffered_FailsOrFallsBackToSoftware()
    {
        AVPixelFormat[] offered =
        [
            AVPixelFormat.AV_PIX_FMT_VAAPI,
            AVPixelFormat.AV_PIX_FMT_NV12,
            AVPixelFormat.AV_PIX_FMT_YUV420P,
            AVPixelFormat.AV_PIX_FMT_NONE,
        ];

        Assert.AreEqual(
            AVPixelFormat.AV_PIX_FMT_NONE,
            Select(AVPixelFormat.AV_PIX_FMT_D3D11, fallback: false, offered)
        );
        Assert.AreEqual(
            AVPixelFormat.AV_PIX_FMT_NV12,
            Select(AVPixelFormat.AV_PIX_FMT_D3D11, fallback: true, offered)
        );
    }

    private static AVPixelFormat Select(
        AVPixelFormat wanted,
        bool fallback,
        AVPixelFormat[] offered
    )
    {
        AVCodecContext context = default;
        Decoder.SelectionState* state = Decoder.SelectionState.Allocate(wanted, fallback, []);
        context.opaque = state;
        try
        {
            fixed (AVPixelFormat* formats = offered)
            {
                delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat> select =
                    &Decoder.SelectFormat;
                return select(&context, formats);
            }
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(state);
        }
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
