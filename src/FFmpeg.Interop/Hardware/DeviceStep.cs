namespace FFmpeg.Interop;

internal sealed record OpenDevice(HardwareDeviceType Type, string? Selector);

internal sealed record OpenVulkanByLuid(long Luid);

internal sealed record DeriveDevice(HardwareDeviceType Type);

// One step in opening a device on a GPU: open one by its type's own selector, open the Vulkan device
// whose LUID is the GPU's, or derive a device of another type from the previous step's.
internal union DeviceStep(OpenDevice, OpenVulkanByLuid, DeriveDevice);
