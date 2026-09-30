using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using Windows.Win32.Graphics.Direct3D11;

namespace FFmpeg.Interop;

/// <summary>
/// Direct3D 11 interop for <see cref="HardwareDeviceType.D3D11VA"/> devices, their pools and frames
/// (FFmpeg's <c>hwcontext_d3d11va.h</c>).
/// </summary>
[SupportedOSPlatform("windows6.1")]
public static unsafe class D3D11VAExtensions
{
    extension(HardwareDevice)
    {
        /// <summary>
        /// Wraps a Direct3D 11 device the application already uses, so frames it renders or captures can
        /// be encoded without leaving the GPU. The device should have multithread protection enabled
        /// (<c>ID3D10Multithread::SetMultithreadProtected</c>) if the application uses it concurrently.
        /// </summary>
        /// <param name="device">The <c>ID3D11Device*</c>. It is AddRef'd; FFmpeg releases it when the device is freed.</param>
        /// <returns>The device.</returns>
        public static HardwareDevice FromD3D11Device(nint device)
        {
            if (device == 0)
            {
                throw new ArgumentNullException(nameof(device));
            }

            HardwareDevice created = HardwareDevice.Allocate(HardwareDeviceType.D3D11VA);
            try
            {
                // FFmpeg's D3D11 uninit releases the device, also after a failed init.
                _ = Dxgi.AddRef(device);
                ((AVD3D11VADeviceContext*)created.Context->hwctx)->device = (void*)device;
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
        /// Wraps the Direct3D 11 device a texture belongs to, for a caller that holds textures but not
        /// their device: a frame from a capture API or a shared-texture protocol.
        /// </summary>
        /// <param name="texture">The <c>ID3D11Texture2D*</c> or any other <c>ID3D11DeviceChild*</c>.</param>
        /// <returns>The device.</returns>
        public static HardwareDevice FromD3D11Texture(nint texture) =>
            texture == 0
                ? throw new ArgumentNullException(nameof(texture))
                : HardwareDevice.FromD3D11Device(D3D11.GetDevice(texture));
    }

    extension(HardwareDevice device)
    {
        /// <summary>The Direct3D 11 objects behind a <see cref="HardwareDeviceType.D3D11VA"/> device.</summary>
        /// <param name="objects">The COM pointers, not AddRef'd: valid while the device is open.</param>
        /// <returns>Whether the device is a D3D11VA device.</returns>
        public bool TryGetD3D11(out D3D11Device objects)
        {
            if (device.Type != HardwareDeviceType.D3D11VA)
            {
                objects = default;
                return false;
            }

            AVD3D11VADeviceContext* context = (AVD3D11VADeviceContext*)device.Context->hwctx;
            objects = new(
                (nint)context->device,
                (nint)context->device_context,
                (nint)context->video_device,
                (nint)context->video_context
            );
            return true;
        }
    }

    extension(HardwareFramePool pool)
    {
        /// <summary>
        /// Copies a Direct3D 11 texture into a surface from this pool on the GPU, the zero-readback way to
        /// hand an encoder a texture the application rendered or captured (a Windows Graphics Capture
        /// frame): encoders only take surfaces from their own pool.
        /// </summary>
        /// <param name="texture">
        /// The <c>ID3D11Texture2D*</c>. It must be on this pool's device (create the pool's device with
        /// <see cref="FromD3D11Device"/> from the application's), in the pool's DXGI format, and at least
        /// the pool's size; the top-left pool-sized region is copied.
        /// </param>
        /// <param name="subresource">The source subresource: the array slice, for a texture array.</param>
        /// <param name="destination">The frame to receive the surface; its previous content is released.</param>
        public void CopyFromD3D11Texture(nint texture, int subresource, Frame destination)
        {
            if (texture == 0)
            {
                throw new ArgumentNullException(nameof(texture));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(subresource);
            ArgumentNullException.ThrowIfNull(destination);
            if (pool.Format != PixelFormat.D3D11)
            {
                throw new InvalidOperationException(
                    $"The pool holds {pool.Format} surfaces, not D3D11 textures."
                );
            }

            AVD3D11VADeviceContext* device = (AVD3D11VADeviceContext*)
                pool.Context->device_ctx->hwctx;
            if (D3D11.GetDevice(texture) != (nint)device->device)
            {
                throw new ArgumentException(
                    "The texture belongs to another D3D11 device; open the pool's device from the application's device.",
                    nameof(texture)
                );
            }

            pool.GetFrame(destination);
            if (!destination.TryGetD3D11Texture(out D3D11Texture surface))
            {
                throw new InvalidOperationException("The pool did not produce a D3D11 surface.");
            }

            D3D11_TEXTURE2D_DESC source = D3D11.GetDescription(texture);
            D3D11_TEXTURE2D_DESC target = D3D11.GetDescription(surface.Texture);
            if (
                source.Format != target.Format
                || source.Width < (uint)pool.Width
                || source.Height < (uint)pool.Height
            )
            {
                throw new ArgumentException(
                    $"The texture is {source.Width}x{source.Height} in DXGI format {source.Format}; the pool needs at least {pool.Width}x{pool.Height} in format {target.Format}.",
                    nameof(texture)
                );
            }

            // The device's immediate context is shared with FFmpeg's encoders and transfers, which take
            // this lock around it.
            device->@lock(device->lock_ctx);
            try
            {
                D3D11.CopySubresourceRegion(
                    (nint)device->device_context,
                    surface.Texture,
                    (uint)surface.ArraySlice,
                    texture,
                    (uint)subresource,
                    (uint)pool.Width,
                    (uint)pool.Height
                );
            }
            finally
            {
                device->unlock(device->lock_ctx);
            }
        }
    }

    extension(Frame frame)
    {
        /// <summary>The Direct3D 11 texture behind a <see cref="PixelFormat.D3D11"/> frame.</summary>
        /// <param name="texture">The surface.</param>
        /// <returns>Whether the frame is a D3D11 frame.</returns>
        public bool TryGetD3D11Texture(out D3D11Texture texture)
        {
            AVFrame* native = frame.NativePointer;
            if (frame.PixelFormat != PixelFormat.D3D11 || native->data[0] is null)
            {
                texture = default;
                return false;
            }

            // data[0] is the ID3D11Texture2D (usually a texture array), data[1] the slice index.
            texture = new((nint)native->data[0], (int)(nint)native->data[1]);
            return true;
        }
    }
}

/// <summary>The Direct3D 11 objects of a D3D11VA device.</summary>
/// <param name="Device">The <c>ID3D11Device*</c>.</param>
/// <param name="DeviceContext">The <c>ID3D11DeviceContext*</c>.</param>
/// <param name="VideoDevice">The <c>ID3D11VideoDevice*</c>.</param>
/// <param name="VideoContext">The <c>ID3D11VideoContext*</c>.</param>
public readonly record struct D3D11Device(
    nint Device,
    nint DeviceContext,
    nint VideoDevice,
    nint VideoContext
);

/// <summary>A Direct3D 11 texture holding a decoded or to-be-encoded picture.</summary>
/// <param name="Texture">The <c>ID3D11Texture2D*</c>, not AddRef'd: valid while the frame holds it.</param>
/// <param name="ArraySlice">The slice of the texture array the picture is in.</param>
public readonly record struct D3D11Texture(nint Texture, int ArraySlice);
