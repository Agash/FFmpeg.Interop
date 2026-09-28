using System.Globalization;

namespace FFmpeg.Interop;

/// <summary>A GPU vendor, from the PCI vendor ID.</summary>
public enum GpuVendor
{
    /// <summary>Not one of the vendors below.</summary>
    Other,

    /// <summary>NVIDIA (0x10DE).</summary>
    Nvidia,

    /// <summary>AMD (0x1002, or 0x1022 for some integrated parts).</summary>
    Amd,

    /// <summary>Intel (0x8086).</summary>
    Intel,

    /// <summary>Apple.</summary>
    Apple,

    /// <summary>Qualcomm (0x5143).</summary>
    Qualcomm,

    /// <summary>Microsoft (0x1414): the software Basic Render Driver.</summary>
    Microsoft,
}

/// <summary>
/// A physical GPU. FFmpeg has no notion of one: every device type picks its GPU its own way (a DXGI
/// index for D3D11 and D3D12, a Vulkan index, a CUDA index, a render node path, or nothing at all for
/// AMF, which opens the default adapter). An adapter is the one identity all of them are resolved from,
/// by <see cref="HardwareDevice.Create(HardwareDeviceType, GpuAdapter, IReadOnlyDictionary{string, string}?)"/>.
/// </summary>
/// <remarks>
/// On Windows adapters come from DXGI, in the order FFmpeg's D3D devices number them; on Linux from the
/// DRM render nodes; on macOS there is the one Apple GPU, which VideoToolbox uses without being told.
/// </remarks>
public sealed record GpuAdapter
{
    /// <summary>The adapter's name, for example <c>NVIDIA GeForce RTX 3060</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The PCI vendor ID, or 0 for a GPU that is not on PCI.</summary>
    public int VendorId { get; init; }

    /// <summary>The PCI device ID, or 0 for a GPU that is not on PCI.</summary>
    public int DeviceId { get; init; }

    /// <summary>The vendor.</summary>
    public GpuVendor Vendor =>
        VendorId switch
        {
            0x10DE => GpuVendor.Nvidia,
            0x1002 or 0x1022 => GpuVendor.Amd,
            0x8086 => GpuVendor.Intel,
            0x106B => GpuVendor.Apple,
            0x5143 => GpuVendor.Qualcomm,
            0x1414 => GpuVendor.Microsoft,
            _ => Name.StartsWith("Apple", StringComparison.Ordinal)
                ? GpuVendor.Apple
                : GpuVendor.Other,
        };

    /// <summary>The Windows locally unique identifier, which D3D, Vulkan and CUDA all report for the same GPU.</summary>
    public long? Luid { get; init; }

    /// <summary>The index in DXGI's adapter enumeration (Windows).</summary>
    public int? DxgiIndex { get; init; }

    /// <summary>The DRM render node, for example <c>/dev/dri/renderD128</c> (Linux).</summary>
    public string? RenderNode { get; init; }

    /// <summary>The kernel driver, for example <c>amdgpu</c> (Linux).</summary>
    public string? Driver { get; init; }

    /// <summary>Dedicated video memory in bytes, when known; 0 for integrated GPUs.</summary>
    public long DedicatedVideoMemory { get; init; }

    /// <summary>Whether the adapter is a software rasterizer rather than hardware.</summary>
    public bool IsSoftware { get; init; }

    /// <summary>The GPUs in this machine, hardware adapters first in the platform's order.</summary>
    /// <returns>The adapters.</returns>
    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        if (OperatingSystem.IsWindows())
        {
            return Dxgi.EnumerateAdapters();
        }

        if (OperatingSystem.IsMacOS())
        {
            return [new GpuAdapter { Name = "Apple GPU", VendorId = 0x106B }];
        }

        return EnumerateRenderNodes("/sys/class/drm");
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} ({VendorId:x4}:{DeviceId:x4})");

    // Each renderD* entry links to its device; PCI devices carry vendor and device IDs there, while
    // platform devices (Arm SoCs) have only a driver.
    internal static List<GpuAdapter> EnumerateRenderNodes(string drmClass)
    {
        List<GpuAdapter> adapters = [];
        if (!Directory.Exists(drmClass))
        {
            return adapters;
        }

        foreach (
            string node in Directory
                .EnumerateFileSystemEntries(drmClass, "renderD*")
                .Order(StringComparer.Ordinal)
        )
        {
            string device = Path.Combine(node, "device");
            int vendor = ReadHex(Path.Combine(device, "vendor"));
            int id = ReadHex(Path.Combine(device, "device"));
            string driverLink = Path.Combine(device, "driver");
            string? driver = Path.Exists(driverLink)
                ? new DirectoryInfo(driverLink).ResolveLinkTarget(returnFinalTarget: true)?.Name
                : null;
            adapters.Add(
                new GpuAdapter
                {
                    Name = driver is null
                        ? Path.GetFileName(node)
                        : $"{driver} {Path.GetFileName(node)}",
                    VendorId = vendor,
                    DeviceId = id,
                    Driver = driver,
                    RenderNode = $"/dev/dri/{Path.GetFileName(node)}",
                }
            );
        }

        return adapters;
    }

    private static int ReadHex(string path) =>
        File.Exists(path)
        && int.TryParse(
            File.ReadAllText(path).Trim().AsSpan(2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out int value
        )
            ? value
            : 0;
}
