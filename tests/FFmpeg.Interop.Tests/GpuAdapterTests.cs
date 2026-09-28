namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class GpuAdapterTests
{
    [TestMethod]
    [DataRow(0x10DE, GpuVendor.Nvidia)]
    [DataRow(0x1002, GpuVendor.Amd)]
    [DataRow(0x1022, GpuVendor.Amd)]
    [DataRow(0x8086, GpuVendor.Intel)]
    [DataRow(0x106B, GpuVendor.Apple)]
    [DataRow(0x5143, GpuVendor.Qualcomm)]
    [DataRow(0x1414, GpuVendor.Microsoft)]
    [DataRow(0x1234, GpuVendor.Other)]
    public void Vendor_FollowsThePciVendorId(int vendorId, GpuVendor expected) =>
        Assert.AreEqual(expected, new GpuAdapter { Name = "gpu", VendorId = vendorId }.Vendor);

    [TestMethod]
    public void Vendor_AppleGpuWithoutPci_IsRecognisedByName()
    {
        Assert.AreEqual(GpuVendor.Apple, new GpuAdapter { Name = "Apple GPU" }.Vendor);
        Assert.AreEqual(
            "NVIDIA GeForce RTX 3060 (10de:2503)",
            new GpuAdapter
            {
                Name = "NVIDIA GeForce RTX 3060",
                VendorId = 0x10DE,
                DeviceId = 0x2503,
            }.ToString()
        );
    }

    [TestMethod]
    public void Enumerate_ListsThisMachinesAdaptersWithTheirPlatformIdentity()
    {
        IReadOnlyList<GpuAdapter> adapters = GpuAdapter.Enumerate();

        if (OperatingSystem.IsWindows())
        {
            // Every Windows machine has at least the Basic Render Driver; DXGI numbers adapters 0..n.
            Assert.IsGreaterThanOrEqualTo(1, adapters.Count);
            CollectionAssert.AreEqual(
                Enumerable.Range(0, adapters.Count).ToList(),
                adapters.Select(a => a.DxgiIndex!.Value).ToList()
            );
            Assert.IsTrue(adapters.All(a => a.Luid is not null && !string.IsNullOrEmpty(a.Name)));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.AreEqual(GpuVendor.Apple, adapters.Single().Vendor);
        }
        else
        {
            Assert.IsTrue(
                adapters.All(a =>
                    a.RenderNode!.StartsWith("/dev/dri/renderD", StringComparison.Ordinal)
                )
            );
        }
    }

    [TestMethod]
    public void EnumerateRenderNodes_ReadsPciIdsAndSkipsWhatIsNotARenderNode()
    {
        using Scratch scratch = new();
        string drm = scratch["drm"];
        MakeNode(drm, "renderD129", "0x10de\n", "0x2503\n");
        MakeNode(drm, "renderD128", "0x1002\n", "0x1638\n");
        MakeNode(drm, "renderD130", null, null);
        _ = Directory.CreateDirectory(Path.Combine(drm, "card0"));

        List<GpuAdapter> adapters = GpuAdapter.EnumerateRenderNodes(drm);

        CollectionAssert.AreEqual(
            new[] { "/dev/dri/renderD128", "/dev/dri/renderD129", "/dev/dri/renderD130" },
            adapters.Select(a => a.RenderNode).ToArray()
        );
        Assert.AreEqual(GpuVendor.Amd, adapters[0].Vendor);
        Assert.AreEqual(0x1638, adapters[0].DeviceId);
        Assert.AreEqual(GpuVendor.Nvidia, adapters[1].Vendor);
        Assert.AreEqual(0, adapters[2].VendorId, "A platform GPU has no PCI identity.");
        Assert.IsEmpty(GpuAdapter.EnumerateRenderNodes(scratch["missing"]));
    }

    [TestMethod]
    public void CreateOnAdapter_WithoutTheIdentityTheTypeNeeds_IsNotSupported()
    {
        TestNatives.Require();
        GpuAdapter bare = new() { Name = "bare" };

        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Create(HardwareDeviceType.D3D11VA, bare)
        );
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Create(HardwareDeviceType.Vaapi, bare)
        );
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Create(HardwareDeviceType.OpenCL, bare)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            HardwareDevice.Create(HardwareDeviceType.Vulkan, (GpuAdapter)null!)
        );
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            _ = Assert.ThrowsExactly<NotSupportedException>(() =>
                HardwareDevice.Create(HardwareDeviceType.Vulkan, bare)
            );
            WrapNullDeviceThrows();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    private static void WrapNullDeviceThrows()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => HardwareDevice.FromD3D11Device(0));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => HardwareDevice.FromD3D12Device(0));
    }

    // FFmpeg prefers libdav1d for AV1, which has no hardware path; the hardware-aware lookup must find
    // the native decoder that does. This reads the codecs' hardware tables, so it needs no GPU.
    [TestMethod]
    public void FindDecoderForDevice_Av1_SkipsTheSoftwareOnlyPreferredDecoder()
    {
        TestNatives.Require();
        // The platform's own video API: the pinned builds have its hwaccel for AV1 and H.264.
        HardwareDeviceType device = TestNatives.PlatformVideoApi;
        Assert.IsFalse(
            Codec.FindDecoder(CodecId.Av1).SupportsDevice(device),
            "The preferred AV1 decoder is libdav1d."
        );

        Codec decoder = Codec.FindDecoder(CodecId.Av1, device);

        Assert.AreEqual("av1", decoder.Name);
        Assert.IsTrue(decoder.SupportsDevice(device));
        Assert.AreEqual(
            "h264",
            Codec.FindDecoder(CodecId.H264, device).Name,
            "The preferred decoder is kept when it qualifies."
        );
        Assert.IsFalse(Codec.TryFindDecoder(CodecId.PcmS16LE, device, out _));
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            Codec.FindDecoder(CodecId.PcmS16LE, device)
        );
    }

    // How each device type reaches a chosen GPU, for both platforms' routes, checked on every machine.
    [TestMethod]
    [DataRow("d3d11va", true, "open d3d11va 1")]
    [DataRow("d3d12va", true, "open d3d12va 1")]
    [DataRow("amf", true, "open d3d11va 1 | derive amf")]
    [DataRow("qsv", true, "open d3d11va 1 | derive qsv")]
    [DataRow("qsv", false, "open vaapi /dev/dri/renderD129 | derive qsv")]
    [DataRow("vulkan", true, "vulkan luid 42")]
    [DataRow("vulkan", false, "open drm /dev/dri/renderD129 | derive vulkan")]
    [DataRow("cuda", true, "vulkan luid 42 | derive cuda")]
    [DataRow("cuda", false, "open drm /dev/dri/renderD129 | derive vulkan | derive cuda")]
    [DataRow("vaapi", false, "open vaapi /dev/dri/renderD129")]
    [DataRow("drm", false, "open drm /dev/dri/renderD129")]
    [DataRow("videotoolbox", false, "open videotoolbox ")]
    public void Route_ResolvesEachDeviceTypeToTheSameGpu(string type, bool windows, string expected)
    {
        TestNatives.Require();
        Assert.IsTrue(HardwareDeviceType.TryParse(type, out HardwareDeviceType deviceType));
        GpuAdapter adapter = new()
        {
            Name = "gpu",
            DxgiIndex = 1,
            Luid = 42,
            RenderNode = "/dev/dri/renderD129",
        };

        string route = string.Join(
            " | ",
            HardwareDevice.Route(deviceType, adapter, windows).Select(Describe)
        );

        Assert.AreEqual(expected, route);
    }

    [TestMethod]
    public void Route_TypeWithoutTheIdentityItNeeds_IsNotSupported()
    {
        TestNatives.Require();
        GpuAdapter linuxOnly = new() { Name = "gpu", RenderNode = "/dev/dri/renderD128" };
        GpuAdapter windowsOnly = new()
        {
            Name = "gpu",
            DxgiIndex = 0,
            Luid = 1,
        };

        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Route(HardwareDeviceType.D3D11VA, linuxOnly, windows: true)
        );
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Route(HardwareDeviceType.Vulkan, linuxOnly, windows: true)
        );
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Route(HardwareDeviceType.Vaapi, windowsOnly, windows: false)
        );
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Route(HardwareDeviceType.Cuda, windowsOnly, windows: false)
        );
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            HardwareDevice.Route(HardwareDeviceType.OpenCL, windowsOnly, windows: true)
        );
    }

    private static string Describe(DeviceStep step) =>
        step switch
        {
            OpenDevice open => $"open {open.Type} {open.Selector}",
            OpenVulkanByLuid vulkan => $"vulkan luid {vulkan.Luid}",
            DeriveDevice derive => $"derive {derive.Type}",
        };

    private static void MakeNode(string drm, string name, string? vendor, string? device)
    {
        string path = Path.Combine(drm, name, "device");
        _ = Directory.CreateDirectory(path);
        if (vendor is not null)
        {
            File.WriteAllText(Path.Combine(path, "vendor"), vendor);
            File.WriteAllText(Path.Combine(path, "device"), device);
        }
    }
}
