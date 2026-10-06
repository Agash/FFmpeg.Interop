using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// Turns DMA-BUF pictures into frames of a <see cref="PixelFormat.Vulkan"/> pool without a copy: each
/// picture's memory is imported as a Vulkan image created for the pool's use, with the pool's image
/// creation chain (a Vulkan encoder's video profile) and its read usages, so the frame goes wherever the
/// pool's own frames go, an encoder's input included.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg's own DRM PRIME mapping (<see cref="Frame.MapTo(HardwareFramePool, Frame, HardwareMapAccess)"/>)
/// creates images that can only be sampled and copied from, which a Vulkan Video encoder cannot read.
/// </para>
/// <para>
/// A DMA-BUF is shared with whoever produced it, so its images change hands explicitly:
/// <see cref="Acquire"/> takes them before this device uses them, and <see cref="Release"/> gives them
/// back once its work on them is done, before the producer writes them again. An imported frame can be
/// kept and reused for as long as the producer keeps handing back the same buffer.
/// </para>
/// <para>Calls are serialised internally; one importer may serve several threads.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class VulkanDmaBufImporter : IDisposable
{
    // VK_QUEUE_FAMILY_EXTERNAL and VK_QUEUE_FAMILY_FOREIGN_EXT: the image's other user, on another
    // device of this driver, or anywhere when the device can name that.
    private const uint QueueFamilyExternal = ~1u;
    private const uint QueueFamilyForeign = ~2u;

    // An encoder's input is read only by the encoder: a video picture's usages must stay within those the
    // driver lists for its profile, which need not include sampling or transfers. Other pools' pictures
    // are read by shaders and copies.
    private const uint EncoderUsages = (uint)
        VkImageUsageFlagBits.VK_IMAGE_USAGE_VIDEO_ENCODE_SRC_BIT_KHR;

    private const uint ReadUsages =
        (uint)VkImageUsageFlagBits.VK_IMAGE_USAGE_SAMPLED_BIT
        | (uint)VkImageUsageFlagBits.VK_IMAGE_USAGE_TRANSFER_SRC_BIT;

    // A pool's images are created profile-independent when the encoder may use any profile, which an
    // imported image must be too to be encoder input. Views in other formats are not needed: the
    // encoder reads the picture in its own format, and with DRM-modifier tiling a mutable format would
    // have to list every view format.
    private const uint KeptCreateFlags = (uint)
        VkImageCreateFlagBits.VK_IMAGE_CREATE_VIDEO_PROFILE_INDEPENDENT_BIT_KHR;

    private readonly Lock _gate = new();
    private readonly SafeBufferHandle _pool;
    private readonly AVHWFramesContext* _frames;
    private readonly AVVulkanDeviceContext* _context;
    private readonly VulkanQueueWork _work;
    private readonly Queue<(ulong Done, nint Semaphore)> _waited = new();
    private readonly VkFormat _format;
    private readonly uint _usage;
    private readonly uint _flags;
    private readonly uint _otherUser;
    private readonly uint[] _families;
    private bool _disposed;

    /// <summary>Creates an importer for a pool's frames.</summary>
    /// <param name="pool">
    /// A <see cref="PixelFormat.Vulkan"/> pool, typically the one a Vulkan encoder takes its input from. The
    /// importer keeps its own reference to the pool.
    /// </param>
    /// <exception cref="ArgumentException">The pool is not a Vulkan pool.</exception>
    /// <exception cref="NotSupportedException">
    /// The pool keeps each plane in an image of its own, or its device cannot import DMA-BUFs.
    /// </exception>
    public VulkanDmaBufImporter(HardwareFramePool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (pool.Format != PixelFormat.Vulkan)
        {
            throw new ArgumentException($"{pool.Format} is not a Vulkan pool.", nameof(pool));
        }

        _frames = pool.Context;
        AVVulkanFramesContext* frames = (AVVulkanFramesContext*)_frames->hwctx;
        if (frames->format[1] != VkFormat.VK_FORMAT_UNDEFINED)
        {
            throw new NotSupportedException(
                "The pool keeps each plane in an image of its own; a DMA-BUF picture is imported as one multi-planar image."
            );
        }

        AVHWDeviceContext* device = _frames->device_ctx;
        _context = (AVVulkanDeviceContext*)device->hwctx;
        string[] required =
        [
            "VK_KHR_external_memory_fd",
            "VK_EXT_external_memory_dma_buf",
            "VK_EXT_image_drm_format_modifier",
        ];
        HashSet<string> enabled = Extensions(_context);
        if (required.FirstOrDefault(e => !enabled.Contains(e)) is { } missing)
        {
            throw new NotSupportedException($"The Vulkan device does not have {missing} enabled.");
        }

        _otherUser = enabled.Contains("VK_EXT_queue_family_foreign")
            ? QueueFamilyForeign
            : QueueFamilyExternal;
        _format = frames->format[0];
        _usage =
            ((uint)frames->usage & EncoderUsages) != 0
                ? EncoderUsages
                : (uint)frames->usage & ReadUsages;
        _flags = frames->img_flags & KeptCreateFlags;
        _families = [.. Families(_context)];
        _pool = SafeBufferHandle.Own(av_buffer_ref(pool.NativePointer), "av_buffer_ref");
        _work = new VulkanQueueWork(device);
    }

    /// <summary>
    /// The DRM format modifiers of the pool's format that a DMA-BUF can have to be imported for the pool's
    /// use. A producer that allocates its buffers with one of these is read without a copy.
    /// </summary>
    public ImmutableArray<ulong> SupportedModifiers
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                VulkanFunctions vk = _work.Functions;
                VkDrmFormatModifierPropertiesListEXT list = new()
                {
                    sType =
                        VkStructureType.VK_STRUCTURE_TYPE_DRM_FORMAT_MODIFIER_PROPERTIES_LIST_EXT,
                };
                VkFormatProperties2 properties = new()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_FORMAT_PROPERTIES_2,
                    pNext = &list,
                };
                vk.GetPhysicalDeviceFormatProperties2(_context->phys_dev, _format, &properties);
                VkDrmFormatModifierPropertiesEXT[] modifiers = new VkDrmFormatModifierPropertiesEXT[
                    list.drmFormatModifierCount
                ];
                fixed (VkDrmFormatModifierPropertiesEXT* entries = modifiers)
                {
                    list.pDrmFormatModifierProperties = entries;
                    vk.GetPhysicalDeviceFormatProperties2(_context->phys_dev, _format, &properties);
                }

                ImmutableArray<ulong>.Builder supported = ImmutableArray.CreateBuilder<ulong>();
                foreach (VkDrmFormatModifierPropertiesEXT modifier in modifiers)
                {
                    if (Importable(modifier.drmFormatModifier))
                    {
                        supported.Add(modifier.drmFormatModifier);
                    }
                }

                return supported.ToImmutable();
            }
        }
    }

    /// <summary>Whether a DMA-BUF with a DRM format modifier can be imported for the pool's use.</summary>
    /// <param name="modifier">The modifier.</param>
    /// <returns>Whether it can.</returns>
    public bool Supports(ulong modifier)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Importable(modifier);
        }
    }

    /// <summary>
    /// Imports a DMA-BUF picture as a frame of the pool. The frame keeps its own duplicate of each file
    /// descriptor and its images until it is released, and starts out held by the picture's producer:
    /// <see cref="Acquire"/> it before use.
    /// </summary>
    /// <param name="image">
    /// The picture: one layer of the pool's format, its planes in one DMA-BUF object.
    /// </param>
    /// <param name="width">The picture's width in pixels.</param>
    /// <param name="height">The picture's height in pixels.</param>
    /// <param name="destination">The frame to make; anything it held is released first.</param>
    /// <exception cref="NotSupportedException">
    /// The picture has several layers or objects, or its modifier is not one of
    /// <see cref="SupportedModifiers"/>.
    /// </exception>
    public void Import(DrmPrimeImage image, int width, int height, Frame destination)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (image.Layers.Length != 1 || image.Objects.Length != 1)
        {
            throw new NotSupportedException(
                $"A picture of {image.Layers.Length} layers in {image.Objects.Length} objects cannot be imported as one image: give one layer in one DMA-BUF."
            );
        }

        DrmObject dmaBuf = image.Objects[0];
        ImmutableArray<DrmPlane> planes = image.Layers[0].Planes;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Importable(dmaBuf.Modifier))
            {
                throw new NotSupportedException(
                    $"DRM modifier 0x{dmaBuf.Modifier:x16} cannot be imported for this pool's use on this GPU."
                );
            }

            ImportLocked(dmaBuf, planes, width, height, destination);
        }
    }

    /// <summary>
    /// Takes an imported frame's images from their producer, contents kept, before this device uses
    /// them. The hand-over is queued on the GPU ahead of the frame's next use; it does not block.
    /// </summary>
    /// <param name="frame">A frame from <see cref="Import"/>, held by its producer.</param>
    /// <param name="ready">
    /// The point the producer signals once the picture is written, for a producer that synchronises
    /// explicitly: the GPU waits for it before the frame's next use. Null when the picture is finished.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// A sync point was given and the device was made without VK_KHR_external_semaphore_fd
    /// (<see cref="WaitsForSyncPoints"/>).
    /// </exception>
    public void Acquire(Frame frame, DrmSyncPoint? ready = null) =>
        HandOver(frame, acquire: true, ready);

    /// <summary>Whether <see cref="Acquire"/> can wait for a producer's sync point on the GPU.</summary>
    public bool WaitsForSyncPoints => _work.Functions.ImportSemaphoreFd is not null;

    /// <summary>
    /// Gives an imported frame's images back to their producer once the work already queued on them is
    /// done, leaving them in the general layout producers write in. Returns when the hand-over has run,
    /// so the producer may reuse the buffer straight after.
    /// </summary>
    /// <param name="frame">A frame from <see cref="Import"/>, acquired.</param>
    public void Release(Frame frame) => HandOver(frame, acquire: false, null);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _work.Dispose();
            DestroyWaited(ulong.MaxValue);
            _pool.Dispose();
        }
    }

    private void HandOver(Frame frame, bool acquire, DrmSyncPoint? ready)
    {
        ArgumentNullException.ThrowIfNull(frame);
        AVFrame* native = frame.NativePointer;
        if (
            (AVPixelFormat)native->format != AVPixelFormat.AV_PIX_FMT_VULKAN
            || native->data[0] is null
            || native->hw_frames_ctx is null
            || (AVHWFramesContext*)native->hw_frames_ctx->data != _frames
        )
        {
            throw new ArgumentException(
                "The frame is not a Vulkan frame of this importer's pool.",
                nameof(frame)
            );
        }

        AVVkFrame* vkf = (AVVkFrame*)native->data[0];
        AVVulkanFramesContext* frames = (AVVulkanFramesContext*)_frames->hwctx;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(_disposed, this);
            DestroyWaited(_work.Completed);
            void* waited = ready is { } point ? ImportSyncPoint(point) : null;
            ulong done;
            frames->lock_frame(_frames, vkf);
            try
            {
                bool held = vkf->queue_family[0] == _otherUser;
                if (acquire != held)
                {
                    throw new InvalidOperationException(
                        acquire
                            ? "The frame is not held by its producer; it was acquired already."
                            : "The frame is held by its producer; acquire it before releasing it."
                    );
                }

                VkImageMemoryBarrier barrier = new()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                    srcAccessMask = 0,
                    dstAccessMask = 0,
                    oldLayout = acquire ? VkImageLayout.VK_IMAGE_LAYOUT_GENERAL : vkf->layout[0],
                    newLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL,
                    srcQueueFamilyIndex = acquire ? _otherUser : _work.Family,
                    dstQueueFamilyIndex = acquire ? _work.Family : _otherUser,
                    image = vkf->img[0],
                    subresourceRange = new()
                    {
                        aspectMask = (uint)VkImageAspectFlagBits.VK_IMAGE_ASPECT_COLOR_BIT,
                        levelCount = 1,
                        layerCount = 1,
                    },
                };
                void* commands = _work.Begin();
                _work.Barriers(
                    commands,
                    [barrier],
                    VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                    VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT
                );
                done = waited is null
                    ? _work.Submit(commands, [(nint)vkf])
                    : _work.Submit(commands, [(nint)vkf], [((nint)waited, ready!.Value.Value)]);

                // Acquired images are shared by every queue family of the device; FFmpeg's own barriers
                // then need no further transfer.
                vkf->layout[0] = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL;
                vkf->access[0] = 0;
                vkf->queue_family[0] = acquire ? VulkanCopy.QueueFamilyIgnored : _otherUser;
            }
            catch
            {
                if (waited is not null)
                {
                    _work.Functions.DestroySemaphore(_context->act_dev, waited, _context->alloc);
                    waited = null;
                }

                throw;
            }
            finally
            {
                frames->unlock_frame(_frames, vkf);
            }

            if (waited is not null)
            {
                _waited.Enqueue((done, (nint)waited));
            }

            if (!acquire)
            {
                _work.Wait(done);
            }
        }
    }

    // A producer's sync point as a timeline semaphore of this device, waited on by one hand-over.
    private void* ImportSyncPoint(DrmSyncPoint point)
    {
        VulkanFunctions vk = _work.Functions;
        if (vk.ImportSemaphoreFd is null)
        {
            throw new NotSupportedException(
                "The Vulkan device was made without VK_KHR_external_semaphore_fd, so a sync point cannot be waited on the GPU."
            );
        }

        void* device = _context->act_dev;
        void* semaphore = VulkanQueueWork.Timeline(vk, _context);

        // The import takes the descriptor; the caller keeps its own.
        int fd = dup(point.Syncobj);
        if (fd < 0)
        {
            vk.DestroySemaphore(device, semaphore, _context->alloc);
            throw new InvalidOperationException(
                $"Duplicating syncobj {point.Syncobj} failed (errno {Marshal.GetLastPInvokeError()})."
            );
        }

        VkImportSemaphoreFdInfoKHR import = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMPORT_SEMAPHORE_FD_INFO_KHR,
            semaphore = semaphore,
            handleType =
                VkExternalSemaphoreHandleTypeFlagBits.VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_OPAQUE_FD_BIT,
            fd = fd,
        };
        VkResult result = vk.ImportSemaphoreFd(device, &import);
        if (result != VkResult.VK_SUCCESS)
        {
            _ = close(fd);
            vk.DestroySemaphore(device, semaphore, _context->alloc);
            VulkanFunctions.Check(result, "vkImportSemaphoreFdKHR");
        }

        return semaphore;
    }

    // Destroys the semaphores of sync points whose hand-overs have run.
    private void DestroyWaited(ulong completed)
    {
        while (_waited.TryPeek(out (ulong Done, nint Semaphore) oldest) && oldest.Done <= completed)
        {
            _ = _waited.Dequeue();
            _work.Functions.DestroySemaphore(
                _context->act_dev,
                (void*)oldest.Semaphore,
                _context->alloc
            );
        }
    }

    private void ImportLocked(
        DrmObject dmaBuf,
        ImmutableArray<DrmPlane> planes,
        int width,
        int height,
        Frame destination
    )
    {
        VulkanFunctions vk = _work.Functions;
        void* device = _context->act_dev;
        Span<VkSubresourceLayout> layouts = stackalloc VkSubresourceLayout[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            layouts[i] = new VkSubresourceLayout
            {
                offset = (ulong)planes[i].Offset,
                rowPitch = (ulong)planes[i].Pitch,
            };
        }

        void* image = null;
        void* memory = null;
        void* semaphore = null;
        int fd = -1;
        try
        {
            fixed (VkSubresourceLayout* planeLayouts = layouts)
            fixed (uint* families = _families)
            {
                VkImageDrmFormatModifierExplicitCreateInfoEXT explicitLayout = new()
                {
                    sType =
                        VkStructureType.VK_STRUCTURE_TYPE_IMAGE_DRM_FORMAT_MODIFIER_EXPLICIT_CREATE_INFO_EXT,
                    pNext = ((AVVulkanFramesContext*)_frames->hwctx)->create_pnext,
                    drmFormatModifier = dmaBuf.Modifier,
                    drmFormatModifierPlaneCount = (uint)planes.Length,
                    pPlaneLayouts = planeLayouts,
                };
                VkExternalMemoryImageCreateInfo external = new()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO,
                    pNext = &explicitLayout,
                    handleTypes = (uint)
                        VkExternalMemoryHandleTypeFlagBits.VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
                };
                VkImageCreateInfo create = new()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
                    pNext = &external,
                    flags = _flags,
                    imageType = VkImageType.VK_IMAGE_TYPE_2D,
                    format = _format,
                    extent = new VkExtent3D
                    {
                        width = (uint)width,
                        height = (uint)height,
                        depth = 1,
                    },
                    mipLevels = 1,
                    arrayLayers = 1,
                    samples = VkSampleCountFlagBits.VK_SAMPLE_COUNT_1_BIT,
                    tiling = VkImageTiling.VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT,
                    usage = _usage,
                    sharingMode =
                        _families.Length > 1
                            ? VkSharingMode.VK_SHARING_MODE_CONCURRENT
                            : VkSharingMode.VK_SHARING_MODE_EXCLUSIVE,
                    queueFamilyIndexCount = _families.Length > 1 ? (uint)_families.Length : 0,
                    pQueueFamilyIndices = _families.Length > 1 ? families : null,
                    initialLayout = VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
                };
                VulkanFunctions.Check(
                    vk.CreateImage(device, &create, _context->alloc, &image),
                    "vkCreateImage"
                );
            }

            // The import takes ownership of the descriptor it is given, so it gets a duplicate.
            fd = dup(dmaBuf.FileDescriptor);
            if (fd < 0)
            {
                throw new InvalidOperationException(
                    $"Duplicating DMA-BUF {dmaBuf.FileDescriptor} failed (errno {Marshal.GetLastPInvokeError()})."
                );
            }

            VkMemoryFdPropertiesKHR fdProperties = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_FD_PROPERTIES_KHR,
            };
            VulkanFunctions.Check(
                vk.GetMemoryFdProperties(
                    device,
                    VkExternalMemoryHandleTypeFlagBits.VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
                    fd,
                    &fdProperties
                ),
                "vkGetMemoryFdPropertiesKHR"
            );
            VkImageMemoryRequirementsInfo2 requirementsInfo = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_REQUIREMENTS_INFO_2,
                image = image,
            };
            VkMemoryRequirements2 requirements = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_REQUIREMENTS_2,
            };
            vk.GetImageMemoryRequirements2(device, &requirementsInfo, &requirements);
            uint types =
                requirements.memoryRequirements.memoryTypeBits & fdProperties.memoryTypeBits;
            if (types == 0)
            {
                throw new NotSupportedException(
                    "No memory type of this GPU can hold both the image and the DMA-BUF."
                );
            }

            VkImportMemoryFdInfoKHR import = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_IMPORT_MEMORY_FD_INFO_KHR,
                handleType =
                    VkExternalMemoryHandleTypeFlagBits.VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
                fd = fd,
            };
            VkMemoryDedicatedAllocateInfo dedicated = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO,
                pNext = &import,
                image = image,
            };
            VkMemoryAllocateInfo allocate = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                pNext = &dedicated,
                allocationSize = requirements.memoryRequirements.size,
                memoryTypeIndex = (uint)System.Numerics.BitOperations.TrailingZeroCount(types),
            };
            VulkanFunctions.Check(
                vk.AllocateMemory(device, &allocate, _context->alloc, &memory),
                "vkAllocateMemory"
            );
            fd = -1; // The memory owns it now.
            VulkanFunctions.Check(
                vk.BindImageMemory(device, image, memory, 0),
                "vkBindImageMemory"
            );
            semaphore = VulkanQueueWork.Timeline(vk, _context);

            Wrap(
                image,
                memory,
                semaphore,
                requirements.memoryRequirements.size,
                width,
                height,
                destination
            );
            image = memory = semaphore = null;
        }
        finally
        {
            if (fd >= 0)
            {
                _ = close(fd);
            }

            if (semaphore is not null)
            {
                vk.DestroySemaphore(device, semaphore, _context->alloc);
            }

            if (image is not null)
            {
                vk.DestroyImage(device, image, _context->alloc);
            }

            if (memory is not null)
            {
                vk.FreeMemory(device, memory, _context->alloc);
            }
        }
    }

    // Makes the frame: an AVVkFrame whose buffer frees the image, memory and semaphore once the last
    // reference to it goes, after the GPU is done with them.
    private void Wrap(
        void* image,
        void* memory,
        void* semaphore,
        ulong size,
        int width,
        int height,
        Frame destination
    )
    {
        AVVkFrame* vkf = av_vk_frame_alloc();
        if (vkf is null)
        {
            FFmpegError.ThrowOutOfMemory("av_vk_frame_alloc");
        }

        vkf->img[0] = image;
        vkf->tiling = VkImageTiling.VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT;
        vkf->mem[0] = memory;
        vkf->size[0] = (nuint)size;
        vkf->layout[0] = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL;
        vkf->sem[0] = semaphore;
        vkf->sem_value[0] = 0;
        vkf->queue_family[0] = _otherUser;

        FrameOwner* owner = (FrameOwner*)NativeMemory.AllocZeroed((nuint)sizeof(FrameOwner));
        owner->Device = av_buffer_ref(_frames->device_ref);
        owner->Functions = _work.Functions;
        AVBufferRef* buffer = av_buffer_create(
            (byte*)vkf,
            (nuint)sizeof(AVVkFrame),
            &Free,
            owner,
            0
        );
        if (buffer is null || owner->Device is null)
        {
            av_buffer_unref(&owner->Device);
            NativeMemory.Free(owner);
            FreeFrame(vkf);
            FFmpegError.ThrowOutOfMemory("av_buffer_create");
        }

        destination.Reset();
        AVFrame* frame = destination.NativePointer;
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_VULKAN;
        frame->width = width;
        frame->height = height;
        frame->data[0] = (byte*)vkf;
        frame->buf[0] = buffer;
        frame->hw_frames_ctx = av_buffer_ref(_pool.Pointer);
        if (frame->hw_frames_ctx is null)
        {
            destination.Reset();
            FFmpegError.ThrowOutOfMemory("av_buffer_ref");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Free(void* opaque, byte* data)
    {
        FrameOwner* owner = (FrameOwner*)opaque;
        AVVkFrame* vkf = (AVVkFrame*)data;
        AVVulkanDeviceContext* context = (AVVulkanDeviceContext*)
            ((AVHWDeviceContext*)owner->Device->data)->hwctx;
        VulkanFunctions vk = owner->Functions;

        // The GPU may still be reading the image: wait for every use signalled on its semaphore.
        void* semaphore = vkf->sem[0];
        ulong value = vkf->sem_value[0];
        VkSemaphoreWaitInfo wait = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_WAIT_INFO,
            semaphoreCount = 1,
            pSemaphores = &semaphore,
            pValues = &value,
        };

        // Deliberately not checked: a callback from FFmpeg's buffer release cannot throw, and a lost
        // device leaves nothing to wait for.
        _ = vk.WaitSemaphores(context->act_dev, &wait, ulong.MaxValue);
        vk.DestroySemaphore(context->act_dev, vkf->sem[0], context->alloc);
        vk.DestroyImage(context->act_dev, vkf->img[0], context->alloc);
        vk.FreeMemory(context->act_dev, vkf->mem[0], context->alloc);
        FreeFrame(vkf);
        av_buffer_unref(&owner->Device);
        NativeMemory.Free(owner);
    }

    // av_vk_frame_alloc's frame: its private part starts with the mutex FFmpeg locks the frame with.
    // Frames made here are never mapped elsewhere, so the private part holds nothing else to free.
    private static void FreeFrame(AVVkFrame* vkf)
    {
        _ = pthread_mutex_destroy(vkf->@internal);
        av_free(vkf->@internal);
        av_free(vkf);
    }

    private bool Importable(ulong modifier)
    {
        fixed (uint* families = _families)
        {
            VkPhysicalDeviceImageDrmFormatModifierInfoEXT drm = new()
            {
                sType =
                    VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_DRM_FORMAT_MODIFIER_INFO_EXT,
                pNext = ((AVVulkanFramesContext*)_frames->hwctx)->create_pnext,
                drmFormatModifier = modifier,
                sharingMode =
                    _families.Length > 1
                        ? VkSharingMode.VK_SHARING_MODE_CONCURRENT
                        : VkSharingMode.VK_SHARING_MODE_EXCLUSIVE,
                queueFamilyIndexCount = _families.Length > 1 ? (uint)_families.Length : 0,
                pQueueFamilyIndices = _families.Length > 1 ? families : null,
            };
            VkPhysicalDeviceExternalImageFormatInfo external = new()
            {
                sType =
                    VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO,
                pNext = &drm,
                handleType =
                    VkExternalMemoryHandleTypeFlagBits.VK_EXTERNAL_MEMORY_HANDLE_TYPE_DMA_BUF_BIT_EXT,
            };
            VkPhysicalDeviceImageFormatInfo2 info = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2,
                pNext = &external,
                format = _format,
                type = VkImageType.VK_IMAGE_TYPE_2D,
                tiling = VkImageTiling.VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT,
                usage = _usage,
                flags = _flags,
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
            VkResult result = _work.Functions.GetPhysicalDeviceImageFormatProperties2(
                _context->phys_dev,
                &info,
                &properties
            );
            return result == VkResult.VK_SUCCESS
                && (
                    externalProperties.externalMemoryProperties.externalMemoryFeatures
                    & (uint)
                        VkExternalMemoryFeatureFlagBits.VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT
                ) != 0;
        }
    }

    private static HashSet<string> Extensions(AVVulkanDeviceContext* context)
    {
        HashSet<string> names = [];
        for (int i = 0; i < context->nb_enabled_dev_extensions; i++)
        {
            if (NativeString.Read(context->enabled_dev_extensions[i]) is { } name)
            {
                _ = names.Add(name);
            }
        }

        return names;
    }

    // Every queue family FFmpeg opened, so the imported images are shared by all the queues it uses.
    private static IEnumerable<uint> Families(AVVulkanDeviceContext* context)
    {
        HashSet<uint> families = [];
        for (int i = 0; i < context->nb_qf; i++)
        {
            if (context->qf[i].num > 0)
            {
                _ = families.Add((uint)context->qf[i].idx);
            }
        }

        return families.Order();
    }

    // What the frame's buffer needs to free it: the device, kept alive by its own reference.
    private struct FrameOwner
    {
        public AVBufferRef* Device;
        public VulkanFunctions Functions;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup(int fileDescriptor);

    [LibraryImport("libc")]
    private static partial int close(int fileDescriptor);

    [LibraryImport("libc")]
    private static partial int pthread_mutex_destroy(void* mutex);
}
