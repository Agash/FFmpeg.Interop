using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// Shares the pictures of a Vulkan decoder (or any <see cref="PixelFormat.Vulkan"/> frames with exportable
/// DRM-modifier images) as DMA-BUFs, as <see cref="Frame.MapTo(Frame, HardwareMapAccess)"/> to
/// <see cref="PixelFormat.DrmPrime"/> does, with one GPU submission a frame and no CPU waits.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg's map makes three submissions a frame on a small pool of command buffers, each waiting on the CPU
/// for the buffer's last use: the image's release to an external user, the semaphore whose sync file goes
/// into the DMA-BUFs, and at unmap the image taken back with its readers' fences. Here each image's memory
/// is exported and its descriptor read once, the release and the semaphore signal share one submission, and
/// an image returned by its readers is taken back in the next frame's submission, waiting on the readers'
/// fences on the GPU. Command buffers are reused once the GPU has passed them.
/// </para>
/// <para>
/// <see cref="Export"/>, <see cref="Flush"/> and <see cref="Dispose"/> are called from one thread at a time.
/// The frames they produce may be released from any thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class VulkanDmaBufExporter : IDisposable
{
    // linux/dma-buf.h.
    private const nuint ImportSyncFile = 0x40086203;
    private const nuint ExportSyncFile = 0xC0086202;
    private const uint SyncRead = 1 << 0;
    private const uint SyncWrite = 2 << 0;
    private const uint QueueFamilyExternal = ~0u - 1;
    private const uint ReadWrite = (uint)(
        VkAccessFlagBits.VK_ACCESS_MEMORY_READ_BIT | VkAccessFlagBits.VK_ACCESS_MEMORY_WRITE_BIT
    );

    // Orders the release callback, on any thread, against detaching from a pool.
    private readonly Lock _gate = new();
    private readonly Dictionary<nint, Image> _images = [];

    // Pictures released by their readers, under the gate; a queue's buffer is reused where a concurrent
    // queue would allocate a segment every few dozen frames.
    private readonly Queue<nint> _returned = new();
    private readonly List<Image> _taking = [];
    private GCHandle _self;
    private AVBufferRef* _pool;
    private VulkanQueueWork? _work;
    private bool _disposed;

    /// <summary>Creates an exporter; it attaches to the device of the first frame it exports.</summary>
    public VulkanDmaBufExporter() => _self = GCHandle.Alloc(this, GCHandleType.Weak);

    /// <summary>
    /// Makes <paramref name="destination"/> a <see cref="PixelFormat.DrmPrime"/> frame sharing
    /// <paramref name="source"/>'s picture: its DMA-BUFs carry the decode's completion as an implicit fence,
    /// and the picture stays the readers' until every reference to the destination is gone.
    /// </summary>
    /// <param name="source">A Vulkan frame with DRM-modifier tiling on exportable memory.</param>
    /// <param name="destination">An empty frame, which receives the DRM PRIME frame.</param>
    /// <returns>
    /// False when this frame cannot be exported here (another tiling, or an image owned by a queue family
    /// the exporter does not submit on); <see cref="Frame.MapTo(Frame, HardwareMapAccess)"/> shares it then.
    /// </returns>
    public bool Export(Frame source, Frame destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        AVFrame* from = source.NativePointer;
        if (
            (AVPixelFormat)from->format != AVPixelFormat.AV_PIX_FMT_VULKAN
            || from->hw_frames_ctx is null
        )
        {
            return false;
        }

        AVVkFrame* vkf = (AVVkFrame*)from->data[0];
        if (vkf->tiling != VkImageTiling.VK_IMAGE_TILING_DRM_FORMAT_MODIFIER_EXT)
        {
            return false;
        }

        Attach(from->hw_frames_ctx);
        VulkanQueueWork work = _work!;
        if (!Owned(vkf, work.Family))
        {
            return false;
        }

        if (!_images.TryGetValue((nint)vkf, out Image? image))
        {
            image = Describe(source, vkf, work);
            _images.Add((nint)vkf, image);
        }

        AVHWFramesContext* pool = (AVHWFramesContext*)_pool->data;
        AVVulkanFramesContext* frames = (AVVulkanFramesContext*)pool->hwctx;
        Collect();
        int images = VulkanQueueWork.Images(vkf);
        int count = images;
        foreach (Image taken in _taking)
        {
            count += VulkanQueueWork.Images(taken.Frame);
        }

        Span<nint> submitted = stackalloc nint[_taking.Count + 1];
        Span<(nint, ulong)> fences = stackalloc (nint, ulong)[_taking.Count * 4];
        Span<VkImageMemoryBarrier> barriers = stackalloc VkImageMemoryBarrier[count];
        int barrier = 0;
        int fenced = 0;
        void* commands = work.Begin();
        frames->lock_frame(pool, vkf);
        foreach (Image taken in _taking)
        {
            frames->lock_frame(pool, taken.Frame);
        }

        try
        {
            // The pictures the readers have returned come back from the external user, unless FFmpeg took
            // one back already as a reference picture.
            for (int t = 0; t < _taking.Count; t++)
            {
                Image taken = _taking[t];
                submitted[t + 1] = (nint)taken.Frame;
                for (int i = 0; i < VulkanQueueWork.Images(taken.Frame); i++)
                {
                    if (taken.Frame->queue_family[i] == QueueFamilyExternal)
                    {
                        barriers[barrier++] = Acquire(taken.Frame, i, taken.Family[i]);
                    }
                }

                for (int f = 0; f < taken.ReaderFenceCount; f++)
                {
                    fences[fenced++] = ((nint)taken.ReaderFences[f], 0);
                }

                taken.ReaderFenceCount = 0;
            }

            // Vulkan spec, "Queue Family Ownership Transfer": an image shared with another API is released to
            // VK_QUEUE_FAMILY_EXTERNAL, from GENERAL as FFmpeg's export leaves it.
            submitted[0] = (nint)vkf;
            for (int i = 0; i < images; i++)
            {
                image.Family[i] = vkf->queue_family[i];
                barriers[barrier++] = Release(vkf, i);
            }

            work.Barriers(
                commands,
                barriers[..barrier],
                VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT
            );
            _ = work.Submit(commands, submitted, fences[..fenced], image.Signal);
        }
        finally
        {
            foreach (Image taken in _taking)
            {
                frames->unlock_frame(pool, taken.Frame);
            }

            frames->unlock_frame(pool, vkf);
        }

        // The returned pictures go back to FFmpeg's pool now that their acquire is queued ahead of any
        // decode into them.
        foreach (Image taken in _taking)
        {
            av_frame_unref(taken.Source);
        }

        _taking.Clear();
        Fence(image, work);
        Share(from, destination.NativePointer, image);
        return true;
    }

    /// <summary>
    /// Gives the pictures the readers have returned since the last <see cref="Export"/> back to FFmpeg's pool;
    /// a decoder about to be drained or closed calls this, since nothing else would.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Drain();
    }

    private void Drain()
    {
        if (_work is not { } work)
        {
            return;
        }

        Collect();
        if (_taking.Count == 0)
        {
            return;
        }

        AVHWFramesContext* pool = (AVHWFramesContext*)_pool->data;
        AVVulkanFramesContext* frames = (AVVulkanFramesContext*)pool->hwctx;
        int count = 0;
        foreach (Image taken in _taking)
        {
            count += VulkanQueueWork.Images(taken.Frame);
        }

        Span<nint> submitted = stackalloc nint[_taking.Count];
        Span<(nint, ulong)> fences = stackalloc (nint, ulong)[_taking.Count * 4];
        Span<VkImageMemoryBarrier> barriers = stackalloc VkImageMemoryBarrier[count];
        int barrier = 0;
        int fenced = 0;
        void* commands = work.Begin();
        foreach (Image taken in _taking)
        {
            frames->lock_frame(pool, taken.Frame);
        }

        try
        {
            for (int t = 0; t < _taking.Count; t++)
            {
                Image taken = _taking[t];
                submitted[t] = (nint)taken.Frame;
                for (int i = 0; i < VulkanQueueWork.Images(taken.Frame); i++)
                {
                    if (taken.Frame->queue_family[i] == QueueFamilyExternal)
                    {
                        barriers[barrier++] = Acquire(taken.Frame, i, taken.Family[i]);
                    }
                }

                for (int f = 0; f < taken.ReaderFenceCount; f++)
                {
                    fences[fenced++] = ((nint)taken.ReaderFences[f], 0);
                }

                taken.ReaderFenceCount = 0;
            }

            work.Barriers(
                commands,
                barriers[..barrier],
                VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT
            );
            _ = work.Submit(commands, submitted, fences[..fenced]);
        }
        finally
        {
            foreach (Image taken in _taking)
            {
                frames->unlock_frame(pool, taken.Frame);
            }
        }

        foreach (Image taken in _taking)
        {
            av_frame_unref(taken.Source);
        }

        _taking.Clear();
    }

    /// <summary>
    /// Returns the outstanding pictures and releases the exported memory. Frames still holding a shared
    /// picture keep it valid; they are returned to FFmpeg without the exporter when released.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
        _self.Free();
    }

    // Starts on a frame pool, or moves to another one (a stream that changed size): the pictures of the
    // last one are returned and forgotten.
    private void Attach(AVBufferRef* pool)
    {
        if (_pool is not null && _pool->data == pool->data)
        {
            return;
        }

        Detach();
        _pool = av_buffer_ref(pool);
        if (_pool is null)
        {
            FFmpegError.ThrowOutOfMemory("av_buffer_ref");
        }

        _work = new VulkanQueueWork(((AVHWFramesContext*)_pool->data)->device_ctx);
    }

    private void Detach()
    {
        if (_work is null)
        {
            return;
        }

        Drain();
        lock (_gate)
        {
            // Shared pictures not yet returned keep their descriptor, memory and source reference, which
            // their release frees.
            foreach (Image image in _images.Values)
            {
                if (image.Outstanding)
                {
                    image.Context->Exporter = 0;
                    image.Orphan();
                }
            }
        }

        _work.Dispose();
        foreach (Image image in _images.Values)
        {
            image.Dispose(_work);
        }

        _images.Clear();
        _work = null;
        fixed (AVBufferRef** pool = &_pool)
        {
            av_buffer_unref(pool);
        }
    }

    // Whether this exporter's queue can release the frame's images: every image is either shared between
    // FFmpeg's queue families, or owned by the family the exporter submits on.
    private static bool Owned(AVVkFrame* frame, uint family)
    {
        for (int i = 0; i < VulkanQueueWork.Images(frame); i++)
        {
            uint owner = frame->queue_family[i];
            if (owner != VulkanCopy.QueueFamilyIgnored && owner != family)
            {
                return false;
            }
        }

        return true;
    }

    // An image seen for the first time: FFmpeg maps it once, for its descriptor (objects, layers, planes,
    // modifier) and the exported memory, whose descriptors are kept; and its semaphores are made.
    private static Image Describe(Frame source, AVVkFrame* frame, VulkanQueueWork work)
    {
        using Frame mapped = new();
        mapped.PixelFormat = PixelFormat.DrmPrime;
        source.MapTo(mapped, HardwareMapAccess.Read);
        AVDRMFrameDescriptor* found = (AVDRMFrameDescriptor*)mapped.NativePointer->data[0];
        Image image = new(frame, work);
        try
        {
            *image.Descriptor = *found;
            for (int i = 0; i < found->nb_objects; i++)
            {
                int duplicate = Dup(found->objects[i].fd);
                if (duplicate < 0)
                {
                    throw new InvalidOperationException(
                        $"dup of DMA-BUF {found->objects[i].fd} failed: {Marshal.GetLastPInvokeErrorMessage()}"
                    );
                }

                image.Descriptor->objects[i].fd = duplicate;
                image.Objects++;
            }
        }
        catch
        {
            image.Dispose(work);
            throw;
        }

        return image;
    }

    // The DRM PRIME frame: the image's descriptor, and a reference to the source the release returns.
    private void Share(AVFrame* source, AVFrame* destination, Image image)
    {
        if (av_frame_ref(image.Source, source) < 0)
        {
            FFmpegError.ThrowOutOfMemory("av_frame_ref");
        }

        av_frame_unref(destination);
        image.Context->Exporter = GCHandle.ToIntPtr(_self);
        image.Outstanding = true;
        AVBufferRef* buffer = av_buffer_create(
            (byte*)image.Descriptor,
            (nuint)sizeof(AVDRMFrameDescriptor),
            &Released,
            image.Context,
            AV_BUFFER_FLAG_READONLY
        );
        if (buffer is null)
        {
            av_frame_unref(image.Source);
            FFmpegError.ThrowOutOfMemory("av_buffer_create");
        }

        destination->buf[0] = buffer;
        destination->data[0] = (byte*)image.Descriptor;
        destination->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
        destination->width = source->width;
        destination->height = source->height;
        destination->hw_frames_ctx = av_buffer_ref(source->hw_frames_ctx);
        destination->pts = source->pts;
    }

    // The semaphore the release signalled, as a sync file in every DMA-BUF's implicit fences, so a reader
    // that waits on those waits for the decode (dma-buf uAPI, DMA_BUF_IOCTL_IMPORT_SYNC_FILE).
    private static void Fence(Image image, VulkanQueueWork work)
    {
        VkSemaphoreGetFdInfoKHR info = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_GET_FD_INFO_KHR,
            semaphore = image.Signal,
            handleType =
                VkExternalSemaphoreHandleTypeFlagBits.VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT,
        };
        int sync;
        VulkanFunctions.Check(
            work.Functions.GetSemaphoreFd(work.Context->act_dev, &info, &sync),
            "vkGetSemaphoreFdKHR"
        );

        // -1: already signalled, nothing to wait for.
        if (sync < 0)
        {
            return;
        }

        try
        {
            for (int i = 0; i < image.Objects; i++)
            {
                SyncFile import = new() { Flags = SyncWrite, Fd = sync };
                _ = Ioctl(image.Descriptor->objects[i].fd, ImportSyncFile, &import);
            }
        }
        finally
        {
            _ = Close(sync);
        }
    }

    // Called by FFmpeg when the last reference to a shared frame is gone, on whatever thread that was.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Released(void* opaque, byte* data)
    {
        ReleaseContext* context = (ReleaseContext*)opaque;
        if (
            context->Exporter != 0
            && GCHandle.FromIntPtr(context->Exporter).Target is VulkanDmaBufExporter exporter
        )
        {
            lock (exporter._gate)
            {
                if (context->Exporter != 0)
                {
                    exporter._returned.Enqueue((nint)context);
                    return;
                }
            }
        }

        // Deliberately not logged: the exporter left this picture's pool while it was shared, so it goes
        // straight back to FFmpeg's pool (FFmpeg takes it back from the external user before using it) and
        // what the export kept is released here.
        Orphaned.Free(context);
    }

    // Moves the returned pictures to the list the next submission takes back, with their readers' fences.
    private void Collect()
    {
        while (true)
        {
            nint returned;
            lock (_gate)
            {
                if (!_returned.TryDequeue(out returned))
                {
                    break;
                }
            }

            ReleaseContext* context = (ReleaseContext*)returned;
            Image image = _images[(nint)context->Frame];
            image.Outstanding = false;
            image.ReaderFenceCount = 0;
            for (int i = 0; i < image.Objects; i++)
            {
                SyncFile export = new() { Flags = SyncWrite, Fd = -1 };
                if (
                    Ioctl(image.Descriptor->objects[i].fd, ExportSyncFile, &export) != 0
                    || export.Fd < 0
                )
                {
                    continue;
                }

                VkImportSemaphoreFdInfoKHR import = new()
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_IMPORT_SEMAPHORE_FD_INFO_KHR,
                    semaphore = image.ReaderFences[image.ReaderFenceCount],
                    flags = (uint)VkSemaphoreImportFlagBits.VK_SEMAPHORE_IMPORT_TEMPORARY_BIT,
                    handleType =
                        VkExternalSemaphoreHandleTypeFlagBits.VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT,
                    fd = export.Fd,
                };
                if (
                    _work!.Functions.ImportSemaphoreFd(_work.Context->act_dev, &import)
                    == VkResult.VK_SUCCESS
                )
                {
                    image.ReaderFenceCount++;
                }
                else
                {
                    // The import did not take the descriptor, so it is still ours; without it the readers'
                    // fences are waited on the CPU.
                    PollFence(export.Fd);
                    _ = Close(export.Fd);
                }
            }

            _taking.Add(image);
        }
    }

    private static VkImageMemoryBarrier Release(AVVkFrame* frame, int index)
    {
        VkImageMemoryBarrier barrier = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            srcAccessMask = (uint)frame->access[index],
            dstAccessMask = 0,
            oldLayout = frame->layout[index],
            newLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL,
            srcQueueFamilyIndex = frame->queue_family[index],
            dstQueueFamilyIndex = QueueFamilyExternal,
            image = frame->img[index],
            subresourceRange = new()
            {
                aspectMask = (uint)VkImageAspectFlagBits.VK_IMAGE_ASPECT_COLOR_BIT,
                levelCount = 1,
                layerCount = 1,
            },
        };
        frame->layout[index] = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL;
        frame->access[index] = ReadWrite;
        frame->queue_family[index] = QueueFamilyExternal;
        return barrier;
    }

    private static VkImageMemoryBarrier Acquire(AVVkFrame* frame, int index, uint family)
    {
        VkImageMemoryBarrier barrier = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            srcAccessMask = 0,
            dstAccessMask = ReadWrite,
            oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL,
            newLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL,
            srcQueueFamilyIndex = QueueFamilyExternal,
            dstQueueFamilyIndex = family,
            image = frame->img[index],
            subresourceRange = new()
            {
                aspectMask = (uint)VkImageAspectFlagBits.VK_IMAGE_ASPECT_COLOR_BIT,
                levelCount = 1,
                layerCount = 1,
            },
        };

        // The submission's semaphore signal makes the memory available to whatever waits on it next.
        frame->access[index] = 0;
        frame->queue_family[index] = family;
        return barrier;
    }

    // Blocks until a sync file signals: the fallback when Vulkan cannot import it.
    private static void PollFence(int fd)
    {
        PollFd poll = new() { Fd = fd, Events = 1 };
        _ = Poll(&poll, 1, -1);
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, void* argument);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Dup(int fd);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(PollFd* fds, nuint count, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct SyncFile
    {
        public uint Flags;
        public int Fd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    // What the release callback needs, in native memory so FFmpeg can hold it as the buffer's opaque.
    private struct ReleaseContext
    {
        public nint Exporter;
        public AVVkFrame* Frame;
        public AVFrame* Source;
        public AVDRMFrameDescriptor* Descriptor;
        public int Objects;
    }

    // What a picture shared when its exporter left the pool frees on its release.
    private static class Orphaned
    {
        public static void Free(ReleaseContext* context)
        {
            for (int i = 0; i < context->Objects; i++)
            {
                _ = Close(context->Descriptor->objects[i].fd);
            }

            AVFrame* source = context->Source;
            av_frame_free(&source);
            NativeMemory.Free(context->Descriptor);
            NativeMemory.Free(context);
        }
    }

    // One image of FFmpeg's pool, as exported: its descriptor and memory, its semaphores, and the frame
    // reference a shared copy holds.
    private sealed class Image
    {
        public Image(AVVkFrame* frame, VulkanQueueWork work)
        {
            Frame = frame;
            Descriptor = (AVDRMFrameDescriptor*)
                NativeMemory.AllocZeroed((nuint)sizeof(AVDRMFrameDescriptor));
            Context = (ReleaseContext*)NativeMemory.AllocZeroed((nuint)sizeof(ReleaseContext));
            Source = av_frame_alloc();
            if (Source is null)
            {
                FFmpegError.ThrowOutOfMemory("av_frame_alloc");
            }

            Context->Frame = frame;
            Context->Source = Source;
            Signal = Exportable(work);
            for (int i = 0; i < 4; i++)
            {
                ReaderFences[i] = Binary(work, null);
            }
        }

        public AVVkFrame* Frame { get; }

        public AVDRMFrameDescriptor* Descriptor { get; }

        public ReleaseContext* Context { get; }

        public AVFrame* Source { get; }

        public int Objects { get; set; }

        // Shared and not yet returned.
        public bool Outstanding { get; set; }

        // Handed to its release, which frees the descriptor, memory and source.
        private bool _orphaned;

        // The family each image belonged to before its release, which its acquire returns it to.
        public uint[] Family { get; } = new uint[8];

        // Binary, exportable as a sync file: signalled by each release.
        public void* Signal { get; }

        public void*[] ReaderFences { get; } = new void*[4];

        public int ReaderFenceCount { get; set; }

        public void Orphan()
        {
            Context->Descriptor = Descriptor;
            Context->Objects = Objects;
            _orphaned = true;
        }

        public void Dispose(VulkanQueueWork work)
        {
            work.Functions.DestroySemaphore(work.Context->act_dev, Signal, work.Context->alloc);
            foreach (void* fence in ReaderFences)
            {
                work.Functions.DestroySemaphore(work.Context->act_dev, fence, work.Context->alloc);
            }

            if (_orphaned)
            {
                return;
            }

            for (int i = 0; i < Objects; i++)
            {
                _ = Close(Descriptor->objects[i].fd);
            }

            NativeMemory.Free(Descriptor);
            NativeMemory.Free(Context);
            AVFrame* source = Source;
            av_frame_free(&source);
        }

        private static void* Exportable(VulkanQueueWork work)
        {
            VkExportSemaphoreCreateInfo export = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_EXPORT_SEMAPHORE_CREATE_INFO,
                handleTypes = (uint)
                    VkExternalSemaphoreHandleTypeFlagBits.VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_SYNC_FD_BIT,
            };
            return Binary(work, &export);
        }

        private static void* Binary(VulkanQueueWork work, void* next)
        {
            VkSemaphoreCreateInfo info = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
                pNext = next,
            };
            void* semaphore;
            VulkanFunctions.Check(
                work.Functions.CreateSemaphore(
                    work.Context->act_dev,
                    &info,
                    work.Context->alloc,
                    &semaphore
                ),
                "vkCreateSemaphore"
            );
            return semaphore;
        }
    }
}
