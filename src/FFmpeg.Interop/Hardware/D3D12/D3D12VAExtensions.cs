using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using Windows.Win32.Graphics.Direct3D12;

namespace FFmpeg.Interop;

/// <summary>
/// Direct3D 12 interop for <see cref="HardwareDeviceType.D3D12VA"/> devices, their pools and frames
/// (FFmpeg's <c>hwcontext_d3d12va.h</c>).
/// </summary>
/// <remarks>
/// A D3D12 frame carries its resource together with a fence and the value that fence reaches when the
/// resource is ready (<see cref="D3D12Texture"/>). Whoever uses the resource waits for that value first,
/// and whoever hands it on signals a later value: FFmpeg's encoders wait for it before reading the
/// resource and advance it once they are done. Resources change hands in
/// <c>D3D12_RESOURCE_STATE_COMMON</c>.
/// </remarks>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class D3D12VAExtensions
{
    extension(HardwareDevice)
    {
        /// <summary>
        /// Wraps a Direct3D 12 device the application already uses, so resources it renders or captures
        /// can be encoded without leaving the GPU (<see cref="WrapD3D12Texture(HardwareFramePool, nint, nint, Frame)"/>).
        /// </summary>
        /// <param name="device">The <c>ID3D12Device*</c>. It is AddRef'd, and released when the device is freed.</param>
        /// <returns>The device.</returns>
        public static HardwareDevice FromD3D12Device(nint device)
        {
            if (device == 0)
            {
                throw new ArgumentNullException(nameof(device));
            }

            HardwareDevice created = HardwareDevice.Allocate(HardwareDeviceType.D3D12VA);
            try
            {
                // The free callback runs whether or not init succeeds, and releases this reference.
                _ = Dxgi.AddRef(device);
                ((AVD3D12VADeviceContext*)created.Context->hwctx)->device = (void*)device;
                created.Context->free = D3D12.ReleaseDevice;
                created.Initialize();
                return created;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Wraps the Direct3D 12 device a resource belongs to, for a caller that holds resources but not
        /// their device: a frame from a capture API or a shared-texture protocol.
        /// </summary>
        /// <param name="resource">The <c>ID3D12Resource*</c> or any other <c>ID3D12DeviceChild*</c>.</param>
        /// <returns>The device.</returns>
        public static HardwareDevice FromD3D12Resource(nint resource) =>
            resource == 0
                ? throw new ArgumentNullException(nameof(resource))
                : HardwareDevice.FromD3D12Device(D3D12.GetDevice(resource));
    }

    extension(HardwareDevice device)
    {
        /// <summary>The Direct3D 12 objects behind a <see cref="HardwareDeviceType.D3D12VA"/> device.</summary>
        /// <param name="objects">The COM pointers, not AddRef'd: valid while the device is open.</param>
        /// <returns>Whether the device is a D3D12VA device.</returns>
        public bool TryGetD3D12(out D3D12Device objects)
        {
            if (device.Type != HardwareDeviceType.D3D12VA)
            {
                objects = default;
                return false;
            }

            AVD3D12VADeviceContext* context = (AVD3D12VADeviceContext*)device.Context->hwctx;
            objects = new((nint)context->device, (nint)context->video_device);
            return true;
        }
    }

    extension(HardwareFramePool pool)
    {
        /// <summary>
        /// Wraps a Direct3D 12 texture as a frame of this pool without copying, for a D3D12 encoder. The
        /// frame holds a reference on the resource until FFmpeg drops the frame's last reference.
        /// </summary>
        /// <param name="resource">
        /// The <c>ID3D12Resource*</c>: a single 2D texture (one mip level, one array slice) on this pool's
        /// device (create the pool's device with <see cref="FromD3D12Device"/> from the application's), in
        /// the pool's DXGI format and exactly the pool's size, in <c>D3D12_RESOURCE_STATE_COMMON</c>.
        /// </param>
        /// <param name="producerQueue">
        /// The <c>ID3D12CommandQueue*</c> the texture was produced on: it is ready once the work submitted
        /// to the queue so far completes, and FFmpeg waits for that on the GPU. Zero when it is ready now.
        /// </param>
        /// <param name="destination">
        /// The frame to receive the texture; its previous content is released. Before writing the
        /// resource again, wait for its <see cref="D3D12Texture.Fence"/> to reach
        /// <see cref="D3D12Texture.FenceValue"/> as <see cref="TryGetD3D12Texture"/> reports it after the
        /// frame is encoded: the encoder advances the value.
        /// </param>
        public void WrapD3D12Texture(nint resource, nint producerQueue, Frame destination)
        {
            nint device = CheckWrap(pool, resource, destination);
            if (producerQueue != 0 && D3D12.GetDevice(producerQueue) != device)
            {
                throw new ArgumentException(
                    "The queue belongs to another D3D12 device than the pool's.",
                    nameof(producerQueue)
                );
            }

            AVBufferRef* buffer = D3D12.WrapResource(device, resource, producerQueue);
            pool.Adopt(destination, buffer, 0, buffer->data);
        }

        /// <summary>
        /// Makes an application's Direct3D 12 texture a frame of this pool, as
        /// the producer-queue overload does, for a texture that is ready when a
        /// fence reaches a value: another component's output, such as a decoded frame. The encoder waits
        /// for the fence on the GPU; the producer's fence is only waited on, never signalled.
        /// </summary>
        /// <param name="resource">The <c>ID3D12Resource*</c>, as for the producer-queue overload.</param>
        /// <param name="readyFence">The <c>ID3D12Fence*</c> on the pool's device that marks the texture ready.</param>
        /// <param name="readyValue">The value the fence reaches when it is.</param>
        /// <param name="destination">The frame to receive the texture; its previous content is released.</param>
        public void WrapD3D12Texture(
            nint resource,
            nint readyFence,
            ulong readyValue,
            Frame destination
        )
        {
            if (readyFence == 0)
            {
                throw new ArgumentNullException(nameof(readyFence));
            }

            nint device = CheckWrap(pool, resource, destination);
            if (D3D12.GetDevice(readyFence) != device)
            {
                throw new ArgumentException(
                    "The fence belongs to another D3D12 device than the pool's.",
                    nameof(readyFence)
                );
            }

            D3D12OrderingQueue ordering = pool.Attachment(static p => new D3D12OrderingQueue(
                DeviceOf(p)
            ));
            AVBufferRef* buffer = D3D12.WrapResource(
                device,
                resource,
                ordering.Queue,
                readyFence,
                readyValue
            );
            pool.Adopt(destination, buffer, 0, buffer->data);
        }
    }

    private static nint DeviceOf(HardwareFramePool pool) =>
        (nint)((AVD3D12VADeviceContext*)pool.Context->device_ctx->hwctx)->device;

    // The checks every wrap makes; returns the pool's device.
    private static nint CheckWrap(HardwareFramePool pool, nint resource, Frame destination)
    {
        if (resource == 0)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        ArgumentNullException.ThrowIfNull(destination);
        if (pool.Format != PixelFormat.D3D12)
        {
            throw new InvalidOperationException(
                $"The pool holds {pool.Format} surfaces, not D3D12 resources."
            );
        }

        nint device = DeviceOf(pool);
        if (D3D12.GetDevice(resource) != device)
        {
            throw new ArgumentException(
                "The resource belongs to another D3D12 device; open the pool's device from the application's device.",
                nameof(resource)
            );
        }

        D3D12_RESOURCE_DESC description = D3D12.GetDescription(resource);
        int format = ((AVD3D12VAFramesContext*)pool.Context->hwctx)->format;
        if (
            description.Dimension != D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D
            || description.DepthOrArraySize != 1
            || description.MipLevels != 1
            || (int)description.Format != format
            || description.Width != (ulong)pool.Width
            || description.Height != (uint)pool.Height
        )
        {
            throw new ArgumentException(
                $"The resource is a {description.Dimension} of {description.Width}x{description.Height}x{description.DepthOrArraySize} with {description.MipLevels} mip levels in DXGI format {(int)description.Format}; the pool needs a single 2D texture of {pool.Width}x{pool.Height} in format {format}.",
                nameof(resource)
            );
        }

        return device;
    }

    extension(Frame frame)
    {
        /// <summary>
        /// The Direct3D 12 resource behind a <see cref="PixelFormat.D3D12"/> frame, with the fence that
        /// guards it: wait until the fence reaches <see cref="D3D12Texture.FenceValue"/> before using the
        /// resource, and signal a later value when handing it back to FFmpeg.
        /// </summary>
        /// <param name="texture">The surface.</param>
        /// <returns>Whether the frame is a D3D12 frame.</returns>
        public bool TryGetD3D12Texture(out D3D12Texture texture)
        {
            AVFrame* native = frame.NativePointer;
            if (frame.PixelFormat != PixelFormat.D3D12 || native->data[0] is null)
            {
                texture = default;
                return false;
            }

            AVD3D12VAFrame* surface = (AVD3D12VAFrame*)native->data[0];
            texture = new(
                (nint)surface->texture,
                surface->subresource_index,
                (nint)surface->sync_ctx.fence,
                surface->sync_ctx.fence_value
            );
            return true;
        }
    }
}

/// <summary>The Direct3D 12 objects of a D3D12VA device.</summary>
/// <param name="Device">The <c>ID3D12Device*</c>.</param>
/// <param name="VideoDevice">The <c>ID3D12VideoDevice*</c>.</param>
public readonly record struct D3D12Device(nint Device, nint VideoDevice);

/// <summary>A Direct3D 12 resource holding a picture, and the fence that orders access to it.</summary>
/// <param name="Resource">The <c>ID3D12Resource*</c>, not AddRef'd: valid while the frame holds it.</param>
/// <param name="Subresource">The subresource (array slice) the picture is in; 0 unless the pool uses a texture array.</param>
/// <param name="Fence">The <c>ID3D12Fence*</c> that reaches <paramref name="FenceValue"/> when the resource is ready.</param>
/// <param name="FenceValue">The fence value to wait for before using the resource.</param>
public readonly record struct D3D12Texture(
    nint Resource,
    int Subresource,
    nint Fence,
    ulong FenceValue
);

// The queue a pool orders its imports on, released with the pool.
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class D3D12OrderingQueue(nint device) : IDisposable
{
    public nint Queue { get; } = D3D12.CreateOrderingQueue(device);

    public void Dispose() => D3D12.Release(Queue);
}
