using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

// Typed views of the GPU surface behind a hardware frame, for handing to a graphics API without a copy.
// Each is valid while the frame holds its current data.
public sealed unsafe partial class Frame
{
    /// <summary>The Direct3D 11 texture behind a <see cref="PixelFormat.D3D11"/> frame.</summary>
    /// <param name="texture">The surface.</param>
    /// <returns>Whether the frame is a D3D11 frame.</returns>
    [SupportedOSPlatform("windows")]
    public bool TryGetD3D11Texture(out D3D11Texture texture)
    {
        AVFrame* frame = NativePointer;
        if (PixelFormat != PixelFormat.D3D11 || frame->data[0] is null)
        {
            texture = default;
            return false;
        }

        // data[0] is the ID3D11Texture2D (usually a texture array), data[1] the slice index.
        texture = new((nint)frame->data[0], (int)(nint)frame->data[1]);
        return true;
    }

    /// <summary>
    /// The Direct3D 12 resource behind a <see cref="PixelFormat.D3D12"/> frame, with the fence that
    /// guards it: wait until the fence reaches <see cref="D3D12Texture.FenceValue"/> before using the
    /// resource, and signal a later value when handing it back to FFmpeg.
    /// </summary>
    /// <param name="texture">The surface.</param>
    /// <returns>Whether the frame is a D3D12 frame.</returns>
    [SupportedOSPlatform("windows")]
    public bool TryGetD3D12Texture(out D3D12Texture texture)
    {
        AVFrame* frame = NativePointer;
        if (PixelFormat != PixelFormat.D3D12 || frame->data[0] is null)
        {
            texture = default;
            return false;
        }

        AVD3D12VAFrame* native = (AVD3D12VAFrame*)frame->data[0];
        texture = new(
            (nint)native->texture,
            native->subresource_index,
            (nint)native->sync_ctx.fence,
            native->sync_ctx.fence_value
        );
        return true;
    }

    /// <summary>The <c>CVPixelBufferRef</c> behind a <see cref="PixelFormat.VideoToolbox"/> frame.</summary>
    /// <param name="pixelBuffer">The pixel buffer; the frame holds the retain on it.</param>
    /// <returns>Whether the frame is a VideoToolbox frame.</returns>
    [SupportedOSPlatform("macos")]
    public bool TryGetCVPixelBuffer(out nint pixelBuffer)
    {
        AVFrame* frame = NativePointer;
        pixelBuffer = PixelFormat == PixelFormat.VideoToolbox ? (nint)frame->data[3] : 0;
        return pixelBuffer != 0;
    }

    /// <summary>The VA-API surface behind a <see cref="PixelFormat.Vaapi"/> frame.</summary>
    /// <param name="surfaceId">The <c>VASurfaceID</c>.</param>
    /// <returns>Whether the frame is a VA-API frame.</returns>
    [SupportedOSPlatform("linux")]
    public bool TryGetVaapiSurface(out uint surfaceId)
    {
        AVFrame* frame = NativePointer;
        bool isVaapi = PixelFormat == PixelFormat.Vaapi;
        surfaceId = isVaapi ? (uint)(nuint)frame->data[3] : 0;
        return isVaapi;
    }

    /// <summary>The DMA-BUF descriptors of a <see cref="PixelFormat.DrmPrime"/> frame.</summary>
    /// <param name="descriptor">A view of the descriptors.</param>
    /// <returns>Whether the frame is a DRM PRIME frame.</returns>
    [SupportedOSPlatform("linux")]
    public bool TryGetDrmFrame(out DrmFrameDescriptor descriptor)
    {
        AVFrame* frame = NativePointer;
        if (PixelFormat != PixelFormat.DrmPrime || frame->data[0] is null)
        {
            descriptor = default;
            return false;
        }

        descriptor = new((AVDRMFrameDescriptor*)frame->data[0]);
        return true;
    }

    /// <summary>The Vulkan images of a <see cref="PixelFormat.Vulkan"/> frame.</summary>
    /// <param name="vulkanFrame">A view of the images and their synchronization state.</param>
    /// <returns>Whether the frame is a Vulkan frame.</returns>
    public bool TryGetVulkanFrame(out VulkanFrame vulkanFrame)
    {
        AVFrame* frame = NativePointer;
        if (PixelFormat != PixelFormat.Vulkan || frame->data[0] is null)
        {
            vulkanFrame = default;
            return false;
        }

        vulkanFrame = new((AVVkFrame*)frame->data[0]);
        return true;
    }

    /// <summary>A plane of a <see cref="PixelFormat.Cuda"/> frame in device memory.</summary>
    /// <param name="plane">The plane index.</param>
    /// <param name="devicePointer">The <c>CUdeviceptr</c> of the plane.</param>
    /// <param name="pitch">The row pitch in bytes.</param>
    /// <returns>Whether the frame is a CUDA frame with that plane.</returns>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    public bool TryGetCudaPlane(int plane, out nint devicePointer, out int pitch)
    {
        AVFrame* frame = NativePointer;
        if (PixelFormat != PixelFormat.Cuda || (uint)plane >= 8 || frame->data[plane] is null)
        {
            devicePointer = 0;
            pitch = 0;
            return false;
        }

        devicePointer = (nint)frame->data[plane];
        pitch = frame->linesize[plane];
        return true;
    }
}

/// <summary>A Direct3D 11 texture holding a decoded or to-be-encoded picture.</summary>
/// <param name="Texture">The <c>ID3D11Texture2D*</c>, not AddRef'd: valid while the frame holds it.</param>
/// <param name="ArraySlice">The slice of the texture array the picture is in.</param>
public readonly record struct D3D11Texture(nint Texture, int ArraySlice);

/// <summary>A Direct3D 12 resource holding a picture, and the fence that orders access to it.</summary>
/// <param name="Resource">The <c>ID3D12Resource*</c>, not AddRef'd: valid while the frame holds it.</param>
/// <param name="Subresource">The subresource (array slice) the picture is in; 0 unless the pool uses a texture array.</param>
/// <param name="Fence">The <c>ID3D12Fence*</c> FFmpeg signals when it has finished with the resource.</param>
/// <param name="FenceValue">The fence value to wait for before using the resource.</param>
public readonly record struct D3D12Texture(
    nint Resource,
    int Subresource,
    nint Fence,
    ulong FenceValue
);

/// <summary>A DMA-BUF object: a file descriptor and its size and layout modifier.</summary>
/// <param name="FileDescriptor">The DMA-BUF file descriptor, owned by the frame.</param>
/// <param name="Size">The size of the object in bytes.</param>
/// <param name="Modifier">The DRM format modifier (tiling/compression layout).</param>
public readonly record struct DrmObject(int FileDescriptor, long Size, ulong Modifier);

/// <summary>A plane within a DMA-BUF object.</summary>
/// <param name="ObjectIndex">The index of the object holding the plane.</param>
/// <param name="Offset">The offset of the plane in the object, in bytes.</param>
/// <param name="Pitch">The row pitch in bytes.</param>
public readonly record struct DrmPlane(int ObjectIndex, long Offset, long Pitch);

/// <summary>A view of the DMA-BUF descriptors of a DRM PRIME frame (<see cref="AVDRMFrameDescriptor"/>).</summary>
public readonly unsafe ref struct DrmFrameDescriptor
{
    private readonly AVDRMFrameDescriptor* _descriptor;

    internal DrmFrameDescriptor(AVDRMFrameDescriptor* descriptor) => _descriptor = descriptor;

    /// <summary>The number of DMA-BUF objects.</summary>
    public int ObjectCount => _descriptor->nb_objects;

    /// <summary>The number of layers; each layer is one DRM format (for example NV12, or R8 plus GR88).</summary>
    public int LayerCount => _descriptor->nb_layers;

    /// <summary>The native descriptor.</summary>
    public AVDRMFrameDescriptor* NativePointer => _descriptor;

    /// <summary>One DMA-BUF object.</summary>
    /// <param name="index">The object index.</param>
    /// <returns>The object.</returns>
    public DrmObject GetObject(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)index,
            (uint)ObjectCount,
            nameof(index)
        );
        AVDRMObjectDescriptor native = _descriptor->objects[index];
        return new(native.fd, (long)native.size, native.format_modifier);
    }

    /// <summary>The DRM fourcc of one layer.</summary>
    /// <param name="layer">The layer index.</param>
    /// <returns>The <c>DRM_FORMAT_*</c> code.</returns>
    public uint GetLayerFormat(int layer) => Layer(layer)->format;

    /// <summary>The number of planes in one layer.</summary>
    /// <param name="layer">The layer index.</param>
    /// <returns>The plane count.</returns>
    public int GetPlaneCount(int layer) => Layer(layer)->nb_planes;

    /// <summary>One plane of one layer.</summary>
    /// <param name="layer">The layer index.</param>
    /// <param name="plane">The plane index within the layer.</param>
    /// <returns>The plane.</returns>
    public DrmPlane GetPlane(int layer, int plane)
    {
        AVDRMLayerDescriptor* native = Layer(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)plane,
            (uint)native->nb_planes,
            nameof(plane)
        );
        AVDRMPlaneDescriptor descriptor = native->planes[plane];
        return new(descriptor.object_index, descriptor.offset, descriptor.pitch);
    }

    private AVDRMLayerDescriptor* Layer(int layer)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)layer,
            (uint)LayerCount,
            nameof(layer)
        );
        return (AVDRMLayerDescriptor*)Unsafe.AsPointer(ref _descriptor->layers[layer]);
    }
}

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
