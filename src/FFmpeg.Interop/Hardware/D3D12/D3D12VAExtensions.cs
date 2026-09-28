using System.Runtime.Versioning;
using FFmpeg.Interop.Native;

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
        /// can be encoded without leaving the GPU.
        /// </summary>
        /// <param name="device">The <c>ID3D12Device*</c>. It is AddRef'd; FFmpeg releases it when the device is freed.</param>
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
                _ = Dxgi.AddRef(device);
                ((AVD3D12VADeviceContext*)created.Context->hwctx)->device = (void*)device;
                created.Initialize();
                return created;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }
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
