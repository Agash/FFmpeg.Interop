using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

// Builds a Vulkan decoder's frames context with DRM-modifier tiling on exportable memory, the shape
// FFmpeg 9's Vulkan decoder takes for output it can share as DMA-BUFs: the modifier list goes ahead of the
// decoder's video profile in the image creation chain. Only modifiers the driver can decode into and
// export with the decoder's usage are listed, so it picks among those.
internal static unsafe class VulkanDecodeExport
{
    // The usages a shared decode picture needs: decoded into (and kept as a reference when the driver
    // decodes in place), and read by whoever it is shared with.
    private const VkImageUsageFlagBits KeptUsages =
        VkImageUsageFlagBits.VK_IMAGE_USAGE_VIDEO_DECODE_DST_BIT_KHR
        | VkImageUsageFlagBits.VK_IMAGE_USAGE_VIDEO_DECODE_DPB_BIT_KHR
        | VkImageUsageFlagBits.VK_IMAGE_USAGE_SAMPLED_BIT
        | VkImageUsageFlagBits.VK_IMAGE_USAGE_TRANSFER_SRC_BIT;

    // Builds and sets the decoder's frames context; false when no modifier suits the stream, which
    // fails it.
    public static bool Configure(AVCodecContext* context, ReadOnlySpan<ulong> requested)
    {
        if (context->hw_frames_ctx is not null)
        {
            av_buffer_unref(&context->hw_frames_ctx);
        }

        AVBufferRef* frames = null;
        if (
            avcodec_get_hw_frames_parameters(
                context,
                context->hw_device_ctx,
                AVPixelFormat.AV_PIX_FMT_VULKAN,
                &frames
            ) < 0
        )
        {
            return false;
        }

        AVHWFramesContext* parameters = (AVHWFramesContext*)frames->data;
        AVVulkanFramesContext* vulkan = (AVVulkanFramesContext*)parameters->hwctx;
        AVVulkanDeviceContext* device = (AVVulkanDeviceContext*)
            ((AVHWDeviceContext*)context->hw_device_ctx->data)->hwctx;
        VkImageUsageFlagBits usage = vulkan->usage & KeptUsages;

        ExportChain* chain = ExportChain.Allocate(requested.Length);
        int count = 0;
        try
        {
            VulkanFunctions vk = new(device);
            foreach (ulong modifier in requested)
            {
                if (Exportable(vk, device, vulkan, usage, modifier))
                {
                    chain->Modifiers[count++] = modifier;
                }
            }
        }
        catch (NotSupportedException)
        {
            // Deliberately not logged: a device without the entry points has no modifier to offer, which
            // the empty list below reports by failing the stream.
            count = 0;
        }

        if (count == 0)
        {
            NativeMemory.Free(chain);
            av_buffer_unref(&frames);
            return false;
        }

        chain->List = new VkImageDrmFormatModifierListCreateInfoEXT
        {
            sType =
                VkStructureType.VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_LIST_CREATE_INFO_EXT,
            pNext = vulkan->create_pnext,
            drmFormatModifierCount = (uint)count,
            pDrmFormatModifiers = chain->Modifiers,
        };
        chain->PreviousFree = parameters->free;
        chain->PreviousOpaque = parameters->user_opaque;
        parameters->user_opaque = chain;
        parameters->free = &Free;
        vulkan->tiling = VkImageTiling.VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
        vulkan->usage = usage;
        vulkan->create_pnext = &chain->List;

        if (av_hwframe_ctx_init(frames) < 0)
        {
            av_buffer_unref(&frames);
            return false;
        }

        context->hw_frames_ctx = frames;
        return true;
    }

    // Whether the driver can decode into an image with this modifier and export its memory.
    private static bool Exportable(
        VulkanFunctions vk,
        AVVulkanDeviceContext* device,
        AVVulkanFramesContext* vulkan,
        VkImageUsageFlagBits usage,
        ulong modifier
    )
    {
        VkPhysicalDeviceImageDrmFormatModifierInfoEXT drm = new()
        {
            sType =
                VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_DRM_FORMAT_MODIFIER_INFO_EXT,
            pNext = vulkan->create_pnext,
            drmFormatModifier = modifier,
            sharingMode = VkSharingMode.VK_SHARING_MODE_EXCLUSIVE,
        };
        VkPhysicalDeviceExternalImageFormatInfo external = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO,
            pNext = &drm,
            handleType =
                VkExternalMemoryHandleTypeFlagBits.VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
        };
        VkPhysicalDeviceImageFormatInfo2 info = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2,
            pNext = &external,
            format = vulkan->format[0],
            type = VkImageType.VK_IMAGE_TYPE_2D,
            tiling = VkImageTiling.VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT,
            usage = (uint)usage,
            flags = vulkan->img_flags,
        };
        VkExternalImageFormatProperties externalProperties = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES,
        };
        VkImageFormatProperties2 properties = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2,
            pNext = &externalProperties,
        };
        return vk.GetPhysicalDeviceImageFormatProperties2(device->phys_dev, &info, &properties)
                == VkResult.VK_SUCCESS
            && (
                externalProperties.externalMemoryProperties.externalMemoryFeatures
                & (uint)VkExternalMemoryFeatureFlagBits.VK_EXTERNAL_MEMORY_FEATURE_EXPORTABLE_BIT
            ) != 0;
    }

    // The frames context's own free, then the chain the decoder's profile came after.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Free(AVHWFramesContext* frames)
    {
        ExportChain* chain = (ExportChain*)frames->user_opaque;
        frames->user_opaque = chain->PreviousOpaque;
        if (chain->PreviousFree is not null)
        {
            chain->PreviousFree(frames);
        }

        NativeMemory.Free(chain);
    }

    // The modifier list in the image creation chain, as long as the frames context lives, with the
    // modifiers following the struct.
    private struct ExportChain
    {
        public VkImageDrmFormatModifierListCreateInfoEXT List;
        public delegate* unmanaged[Cdecl]<AVHWFramesContext*, void> PreviousFree;
        public void* PreviousOpaque;

        public ulong* Modifiers
        {
            get
            {
                fixed (ExportChain* self = &this)
                {
                    return (ulong*)(self + 1);
                }
            }
        }

        public static ExportChain* Allocate(int modifiers) =>
            (ExportChain*)
                NativeMemory.AllocZeroed(
                    (nuint)(sizeof(ExportChain) + (Math.Max(modifiers, 1) * sizeof(ulong)))
                );
    }
}
