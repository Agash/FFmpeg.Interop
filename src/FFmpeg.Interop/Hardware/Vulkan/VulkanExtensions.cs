using System.Buffers.Binary;
using System.Collections.Immutable;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// Vulkan interop for <see cref="HardwareDeviceType.Vulkan"/> devices and <see cref="PixelFormat.Vulkan"/>
/// frames (FFmpeg's <c>hwcontext_vulkan.h</c>).
/// </summary>
public static unsafe class VulkanExtensions
{
    extension(HardwareDevice device)
    {
        /// <summary>The Vulkan handles of a <see cref="HardwareDeviceType.Vulkan"/> device.</summary>
        /// <param name="handles">The instance, physical device and logical device.</param>
        /// <returns>Whether the device is a Vulkan device.</returns>
        public bool TryGetVulkan(out VulkanDevice handles)
        {
            if (device.Type != HardwareDeviceType.Vulkan)
            {
                handles = default;
                return false;
            }

            AVVulkanDeviceContext* context = (AVVulkanDeviceContext*)device.Context->hwctx;
            handles = new((nint)context->inst, (nint)context->phys_dev, (nint)context->act_dev);
            return true;
        }

        /// <summary>
        /// The device extensions FFmpeg enabled on a <see cref="HardwareDeviceType.Vulkan"/> device: every
        /// extension it wanted that the driver offers, so a missing one means the GPU or driver lacks it.
        /// Empty for other device types.
        /// </summary>
        public ImmutableArray<string> VulkanDeviceExtensions
        {
            get
            {
                if (device.Type != HardwareDeviceType.Vulkan)
                {
                    return [];
                }

                AVVulkanDeviceContext* context = (AVVulkanDeviceContext*)device.Context->hwctx;
                ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>(
                    context->nb_enabled_dev_extensions
                );
                for (int i = 0; i < context->nb_enabled_dev_extensions; i++)
                {
                    names.Add(
                        NativeString.Read(context->enabled_dev_extensions[i]) ?? string.Empty
                    );
                }

                return names.MoveToImmutable();
            }
        }

        /// <summary>
        /// Whether a <see cref="HardwareDeviceType.Vulkan"/> device can decode a codec with Vulkan Video,
        /// from the codec's <c>VK_KHR_video_decode_*</c> extension.
        /// </summary>
        /// <param name="codec">The codec.</param>
        /// <returns>Whether the device decodes it.</returns>
        public bool CanVulkanDecode(CodecId codec) =>
            VideoExtension(codec, "decode") is { } name
            && device.VulkanDeviceExtensions.Contains(name);

        /// <summary>
        /// Whether a <see cref="HardwareDeviceType.Vulkan"/> device can encode a codec with Vulkan Video,
        /// from the codec's <c>VK_KHR_video_encode_*</c> extension.
        /// </summary>
        /// <param name="codec">The codec.</param>
        /// <returns>Whether the device encodes it.</returns>
        public bool CanVulkanEncode(CodecId codec) =>
            VideoExtension(codec, "encode") is { } name
            && device.VulkanDeviceExtensions.Contains(name);

        // The locally unique identifier of the GPU behind a Vulkan device, which DXGI reports for the
        // same GPU.
        internal bool TryGetLuid(out long luid)
        {
            luid = 0;
            if (device.Type != HardwareDeviceType.Vulkan)
            {
                return false;
            }

            AVVulkanDeviceContext* context = (AVVulkanDeviceContext*)device.Context->hwctx;
            using Utf8String name = new("vkGetPhysicalDeviceProperties2");
            delegate* unmanaged[Stdcall]<void*, VkPhysicalDeviceProperties2*, void> getProperties =
                (delegate* unmanaged[Stdcall]<void*, VkPhysicalDeviceProperties2*, void>)
                    context->get_proc_addr(context->inst, name);
            if (getProperties is null)
            {
                return false;
            }

            VkPhysicalDeviceIDProperties identity = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES,
            };
            VkPhysicalDeviceProperties2 properties = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,
                pNext = &identity,
            };
            getProperties(context->phys_dev, &properties);
            if (identity.deviceLUIDValid == 0)
            {
                return false;
            }

            luid = BinaryPrimitives.ReadInt64LittleEndian(identity.deviceLUID);
            return true;
        }
    }

    private static string? VideoExtension(CodecId codec, string direction) =>
        codec == CodecId.H264 ? $"VK_KHR_video_{direction}_h264"
        : codec == CodecId.Hevc ? $"VK_KHR_video_{direction}_h265"
        : codec == CodecId.Av1 ? $"VK_KHR_video_{direction}_av1"
        : null;

    extension(Frame frame)
    {
        /// <summary>The Vulkan images of a <see cref="PixelFormat.Vulkan"/> frame.</summary>
        /// <param name="vulkanFrame">A view of the images and their synchronization state.</param>
        /// <returns>Whether the frame is a Vulkan frame.</returns>
        public bool TryGetVulkanFrame(out VulkanFrame vulkanFrame)
        {
            AVFrame* native = frame.NativePointer;
            if (frame.PixelFormat != PixelFormat.Vulkan || native->data[0] is null)
            {
                vulkanFrame = default;
                return false;
            }

            vulkanFrame = new((AVVkFrame*)native->data[0]);
            return true;
        }
    }
}

/// <summary>The Vulkan handles of a Vulkan device.</summary>
/// <param name="Instance">The <c>VkInstance</c>.</param>
/// <param name="PhysicalDevice">The <c>VkPhysicalDevice</c>.</param>
/// <param name="Device">The <c>VkDevice</c>.</param>
public readonly record struct VulkanDevice(nint Instance, nint PhysicalDevice, nint Device);

/// <summary>
/// A view of the Vulkan images of a Vulkan frame (<see cref="AVVkFrame"/>). Layouts and semaphore
/// values are writable because code that uses the images must record the state it leaves them in.
/// </summary>
public readonly unsafe ref struct VulkanFrame
{
    private const int MaxImages = 8;
    private readonly AVVkFrame* _frame;

    internal VulkanFrame(AVVkFrame* frame) => _frame = frame;

    /// <summary>The number of images: one per plane, or one for a multi-planar image.</summary>
    public int ImageCount
    {
        get
        {
            int count = 0;
            while (count < MaxImages && _frame->img[count] is not null)
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>The image tiling.</summary>
    public VkImageTiling Tiling => _frame->tiling;

    /// <summary>The native frame.</summary>
    public AVVkFrame* NativePointer => _frame;

    /// <summary>One <c>VkImage</c> handle.</summary>
    /// <param name="index">The image index.</param>
    /// <returns>The handle.</returns>
    public nint GetImage(int index) => (nint)_frame->img[Checked(index)];

    /// <summary>The <c>VkSemaphore</c> (timeline) guarding one image.</summary>
    /// <param name="index">The image index.</param>
    /// <returns>The handle.</returns>
    public nint GetSemaphore(int index) => (nint)_frame->sem[Checked(index)];

    /// <summary>The layout one image is in; update it after transitioning the image.</summary>
    /// <param name="index">The image index.</param>
    /// <returns>A reference to the layout.</returns>
    public ref VkImageLayout Layout(int index) => ref _frame->layout[Checked(index)];

    /// <summary>The timeline value to wait for before using one image; advance it after signalling.</summary>
    /// <param name="index">The image index.</param>
    /// <returns>A reference to the value.</returns>
    public ref ulong SemaphoreValue(int index) => ref _frame->sem_value[Checked(index)];

    private int Checked(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)index,
            (uint)ImageCount,
            nameof(index)
        );
        return index;
    }
}
