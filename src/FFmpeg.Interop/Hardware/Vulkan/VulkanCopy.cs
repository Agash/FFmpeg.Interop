using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

// Copies one Vulkan frame's images into another's on the GPU, for two frames of one device. FFmpeg has
// no such transfer: av_hwframe_transfer_data moves pictures between a surface and system memory only.
// Both frames are locked while their state is read and advanced, and the copy is ordered by their
// timeline semaphores. The call returns once the copy has finished, so the source may be released (a
// mapped DMA-BUF unmapped) straight after.
internal static unsafe class VulkanCopy
{
    internal const uint QueueFamilyIgnored = ~0u;

    public static void Copy(Frame source, Frame destination)
    {
        AVFrame* from = source.NativePointer;
        AVFrame* to = destination.NativePointer;
        AVHWFramesContext* sourcePool = Pool(from);
        AVHWFramesContext* destinationPool = Pool(to);
        if (sourcePool->device_ctx != destinationPool->device_ctx)
        {
            throw new ArgumentException(
                "Both frames must be on one Vulkan device to copy between them on the GPU.",
                nameof(destination)
            );
        }

        PixelFormat format = sourcePool->sw_format;
        if (format != destinationPool->sw_format)
        {
            throw new ArgumentException(
                $"The frames hold different formats ({format} and {(PixelFormat)destinationPool->sw_format}).",
                nameof(destination)
            );
        }

        if (from->width != to->width || from->height != to->height)
        {
            throw new ArgumentException(
                $"The frames differ in size ({from->width}x{from->height} and {to->width}x{to->height}).",
                nameof(destination)
            );
        }

        AVVkFrame* src = (AVVkFrame*)from->data[0];
        AVVkFrame* dst = (AVVkFrame*)to->data[0];
        AVVulkanFramesContext* srcFrames = (AVVulkanFramesContext*)sourcePool->hwctx;
        AVVulkanFramesContext* dstFrames = (AVVulkanFramesContext*)destinationPool->hwctx;
        using VulkanQueueWork work = new(sourcePool->device_ctx);

        srcFrames->lock_frame(sourcePool, src);
        dstFrames->lock_frame(destinationPool, dst);
        ulong done;
        try
        {
            void* commands = work.Begin();
            int srcImages = VulkanQueueWork.Images(src);
            int dstImages = VulkanQueueWork.Images(dst);
            Span<VkImageMemoryBarrier> barriers =
                stackalloc VkImageMemoryBarrier[srcImages + dstImages];
            for (int i = 0; i < srcImages; i++)
            {
                barriers[i] = Transition(
                    src,
                    i,
                    work.Family,
                    VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    VkAccessFlagBits.VK_ACCESS_TRANSFER_READ_BIT,
                    discard: false
                );
            }

            for (int i = 0; i < dstImages; i++)
            {
                barriers[srcImages + i] = Transition(
                    dst,
                    i,
                    work.Family,
                    VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    VkAccessFlagBits.VK_ACCESS_TRANSFER_WRITE_BIT,
                    discard: true
                );
            }

            work.Barriers(
                commands,
                barriers,
                VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                VkPipelineStageFlagBits.VK_PIPELINE_STAGE_TRANSFER_BIT
            );

            int planes = format.PlaneCount;
            for (int plane = 0; plane < planes; plane++)
            {
                (nint srcImage, VkImageAspectFlagBits srcAspect) = Plane(
                    src,
                    srcImages,
                    planes,
                    plane
                );
                (nint dstImage, VkImageAspectFlagBits dstAspect) = Plane(
                    dst,
                    dstImages,
                    planes,
                    plane
                );
                bool chroma = plane is 1 or 2;
                VkImageCopy region = new()
                {
                    srcSubresource = new() { aspectMask = (uint)srcAspect, layerCount = 1 },
                    dstSubresource = new() { aspectMask = (uint)dstAspect, layerCount = 1 },
                    extent = new()
                    {
                        width = (uint)(
                            chroma ? -(-from->width >> format.ChromaShiftX) : from->width
                        ),
                        height = (uint)(
                            chroma ? -(-from->height >> format.ChromaShiftY) : from->height
                        ),
                        depth = 1,
                    },
                };
                work.Functions.CmdCopyImage(
                    commands,
                    (void*)srcImage,
                    VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    (void*)dstImage,
                    VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    1,
                    &region
                );
            }

            done = work.Submit(commands, [(nint)src, (nint)dst]);
        }
        finally
        {
            dstFrames->unlock_frame(destinationPool, dst);
            srcFrames->unlock_frame(sourcePool, src);
        }

        work.Wait(done);
    }

    // Moves one image into a layout, taking it from the queue family that owns it: an imported image
    // comes from outside the device, a concurrently shared one needs no transfer. A discarded image's
    // old contents are not kept.
    internal static VkImageMemoryBarrier Transition(
        AVVkFrame* frame,
        int index,
        uint family,
        VkImageLayout layout,
        VkAccessFlagBits access,
        bool discard
    )
    {
        uint owner = frame->queue_family[index];
        bool transfer = owner != QueueFamilyIgnored && owner != family;
        VkImageMemoryBarrier barrier = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            srcAccessMask = 0,
            dstAccessMask = (uint)access,
            oldLayout = discard ? VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED : frame->layout[index],
            newLayout = layout,
            srcQueueFamilyIndex = transfer ? owner : QueueFamilyIgnored,
            dstQueueFamilyIndex = transfer ? family : QueueFamilyIgnored,
            image = frame->img[index],
            subresourceRange = new()
            {
                aspectMask = (uint)VkImageAspectFlagBits.VK_IMAGE_ASPECT_COLOR_BIT,
                levelCount = 1,
                layerCount = 1,
            },
        };
        frame->layout[index] = layout;

        // The submission's semaphore signal makes the copy's writes available to whatever waits on it, so
        // no access is carried into the next user's barrier, as FFmpeg's own submissions leave it.
        frame->access[index] = 0;
        if (transfer)
        {
            frame->queue_family[index] = family;
        }

        return barrier;
    }

    private static AVHWFramesContext* Pool(AVFrame* frame)
    {
        if (
            (AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_VULKAN
            || frame->hw_frames_ctx is null
            || frame->data[0] is null
        )
        {
            throw new ArgumentException("Both frames must be Vulkan frames with a surface.");
        }

        return (AVHWFramesContext*)frame->hw_frames_ctx->data;
    }

    // A plane is an aspect of a multi-planar image, or an image of its own.
    private static (nint Image, VkImageAspectFlagBits Aspect) Plane(
        AVVkFrame* frame,
        int images,
        int planes,
        int plane
    ) =>
        images > 1 ? ((nint)frame->img[plane], VkImageAspectFlagBits.VK_IMAGE_ASPECT_COLOR_BIT)
        : planes == 1 ? ((nint)frame->img[0], VkImageAspectFlagBits.VK_IMAGE_ASPECT_COLOR_BIT)
        : (
            (nint)frame->img[0],
            plane switch
            {
                0 => VkImageAspectFlagBits.VK_IMAGE_ASPECT_PLANE_0_BIT,
                1 => VkImageAspectFlagBits.VK_IMAGE_ASPECT_PLANE_1_BIT,
                _ => VkImageAspectFlagBits.VK_IMAGE_ASPECT_PLANE_2_BIT,
            }
        );
}
