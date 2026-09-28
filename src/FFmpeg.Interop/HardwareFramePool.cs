using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// A pool of hardware surfaces of one format and size on one device (<see cref="AVHWFramesContext"/>).
/// Hardware encoders take their input frames from one, and uploads go into one.
/// </summary>
public sealed unsafe class HardwareFramePool : IDisposable
{
    private readonly SafeBufferHandle _reference;

    private HardwareFramePool(SafeBufferHandle reference) => _reference = reference;

    /// <summary>The native <c>AVBufferRef</c> holding the frames context.</summary>
    public AVBufferRef* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_reference.IsClosed, this);
            return _reference.Pointer;
        }
    }

    /// <summary>The native frames context.</summary>
    public AVHWFramesContext* Context => (AVHWFramesContext*)NativePointer->data;

    /// <summary>The hardware pixel format of the surfaces.</summary>
    public PixelFormat Format => Context->format;

    /// <summary>The layout of the data inside the surfaces, for example NV12.</summary>
    public PixelFormat SoftwareFormat => Context->sw_format;

    /// <summary>The surface width.</summary>
    public int Width => Context->width;

    /// <summary>The surface height.</summary>
    public int Height => Context->height;

    /// <summary>Creates a pool.</summary>
    /// <param name="device">The device the surfaces live on.</param>
    /// <param name="format">The hardware format, for example <see cref="PixelFormat.D3D11"/>.</param>
    /// <param name="softwareFormat">The data layout, for example <see cref="PixelFormat.Nv12"/>.</param>
    /// <param name="width">The surface width.</param>
    /// <param name="height">The surface height.</param>
    /// <param name="initialSize">
    /// Surfaces to allocate up front; 0 lets the pool grow. Some devices (D3D11 texture arrays, QSV)
    /// need a fixed size.
    /// </param>
    /// <param name="configure">
    /// Called with the frames context before it is initialised, to set device-specific fields such as
    /// D3D11 bind flags or Vulkan usage.
    /// </param>
    /// <returns>The pool.</returns>
    public static HardwareFramePool Create(
        HardwareDevice device,
        PixelFormat format,
        PixelFormat softwareFormat,
        int width,
        int height,
        int initialSize = 0,
        Action<HardwareFramePool>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(initialSize);
        if (!format.IsHardware)
        {
            throw new ArgumentException($"{format} is not a hardware format.", nameof(format));
        }

        HardwareFramePool pool = new(
            SafeBufferHandle.Own(av_hwframe_ctx_alloc(device.NativePointer), "av_hwframe_ctx_alloc")
        );
        try
        {
            AVHWFramesContext* context = pool.Context;
            context->format = format;
            context->sw_format = softwareFormat;
            context->width = width;
            context->height = height;
            context->initial_pool_size = initialSize;
            configure?.Invoke(pool);
            FFmpegError.ThrowIfError(av_hwframe_ctx_init(pool.NativePointer));
            return pool;
        }
        catch
        {
            pool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The pool a hardware frame's surface came from, for example a hardware decoder's output pool.
    /// Passing it to <see cref="VideoEncoderOptions.HardwareFrames"/> lets an encoder take the decoder's
    /// surfaces directly, so a transcode never leaves the GPU.
    /// </summary>
    /// <param name="frame">A hardware frame.</param>
    /// <returns>A new reference to the frame's pool.</returns>
    /// <exception cref="ArgumentException">The frame is not a hardware frame.</exception>
    public static HardwareFramePool Of(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        AVBufferRef* context = frame.NativePointer->hw_frames_ctx;
        if (context is null)
        {
            throw new ArgumentException("The frame is not a hardware frame.", nameof(frame));
        }

        return new(SafeBufferHandle.Own(av_buffer_ref(context), "av_buffer_ref"));
    }

    /// <summary>Fills <paramref name="frame"/> with a surface from the pool, replacing what it held.</summary>
    /// <param name="frame">The frame.</param>
    public void GetFrame(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        AVFrame* native = frame.NativePointer;
        av_frame_unref(native);
        FFmpegError.ThrowIfError(av_hwframe_get_buffer(NativePointer, native, 0));
    }

    /// <summary>Uploads a system-memory frame into a surface from the pool.</summary>
    /// <param name="source">A frame in <see cref="SoftwareFormat"/> at the pool's size.</param>
    /// <param name="destination">The frame to receive the surface.</param>
    public void Upload(Frame source, Frame destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        GetFrame(destination);
        source.TransferTo(destination);
    }

    /// <summary>
    /// Copies a Direct3D 11 texture into a surface from this pool on the GPU, the zero-readback way to
    /// hand an encoder a texture the application rendered or captured (a Windows Graphics Capture frame):
    /// encoders only take surfaces from their own pool.
    /// </summary>
    /// <param name="texture">
    /// The <c>ID3D11Texture2D*</c>. It must be on this pool's device (create the pool's device with
    /// <see cref="HardwareDevice.FromD3D11Device"/> from the application's), in the pool's DXGI format, and
    /// at least the pool's size; the top-left pool-sized region is copied.
    /// </param>
    /// <param name="subresource">The source subresource: the array slice, for a texture array.</param>
    /// <param name="destination">The frame to receive the surface; its previous content is released.</param>
    [SupportedOSPlatform("windows")]
    public void CopyFromD3D11Texture(nint texture, int subresource, Frame destination)
    {
        if (texture == 0)
        {
            throw new ArgumentNullException(nameof(texture));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(subresource);
        ArgumentNullException.ThrowIfNull(destination);
        if (Format != PixelFormat.D3D11)
        {
            throw new InvalidOperationException(
                $"The pool holds {Format} surfaces, not D3D11 textures."
            );
        }

        AVD3D11VADeviceContext* device = (AVD3D11VADeviceContext*)Context->device_ctx->hwctx;
        nint owner = D3D11.GetDevice(texture);
        _ = D3D11.Release(owner);
        if (owner != (nint)device->device)
        {
            throw new ArgumentException(
                "The texture belongs to another D3D11 device; open the pool's device from the application's device.",
                nameof(texture)
            );
        }

        GetFrame(destination);
        if (!destination.TryGetD3D11Texture(out D3D11Texture surface))
        {
            throw new InvalidOperationException("The pool did not produce a D3D11 surface.");
        }

        D3D11.TextureDescription source = D3D11.GetDescription(texture);
        D3D11.TextureDescription target = D3D11.GetDescription(surface.Texture);
        if (
            source.Format != target.Format
            || source.Width < (uint)Width
            || source.Height < (uint)Height
        )
        {
            throw new ArgumentException(
                $"The texture is {source.Width}x{source.Height} in DXGI format {source.Format}; the pool needs at least {Width}x{Height} in format {target.Format}.",
                nameof(texture)
            );
        }

        // The device's immediate context is shared with FFmpeg's encoders and transfers, which take this
        // lock around it.
        D3D11.Box region = new()
        {
            Right = (uint)Width,
            Bottom = (uint)Height,
            Back = 1,
        };
        device->@lock(device->lock_ctx);
        try
        {
            D3D11.CopySubresourceRegion(
                (nint)device->device_context,
                surface.Texture,
                (uint)surface.ArraySlice,
                texture,
                (uint)subresource,
                region
            );
        }
        finally
        {
            device->unlock(device->lock_ctx);
        }
    }

    /// <summary>
    /// Wraps a <c>CVPixelBufferRef</c> (for example an IOSurface from a Syphon server or a capture
    /// session) as a VideoToolbox frame of this pool, without copying, for a VideoToolbox encoder. The
    /// frame retains the pixel buffer and releases it when FFmpeg drops the frame's last reference.
    /// </summary>
    /// <param name="pixelBuffer">The <c>CVPixelBufferRef</c>, in the pool's format and size.</param>
    /// <param name="destination">The frame to receive it; its previous content is released.</param>
    [SupportedOSPlatform("macos")]
    public void WrapCVPixelBuffer(nint pixelBuffer, Frame destination)
    {
        if (pixelBuffer == 0)
        {
            throw new ArgumentNullException(nameof(pixelBuffer));
        }

        ArgumentNullException.ThrowIfNull(destination);
        if (Format != PixelFormat.VideoToolbox)
        {
            throw new InvalidOperationException(
                $"The pool holds {Format} surfaces, not CVPixelBuffers."
            );
        }

        AVFrame* frame = destination.NativePointer;
        av_frame_unref(frame);
        frame->buf.e0 = CoreFoundation.RetainAsBuffer(pixelBuffer);
        frame->data[3] = (byte*)pixelBuffer;
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;
        frame->width = Width;
        frame->height = Height;
        frame->hw_frames_ctx = NewReference();
    }

    /// <inheritdoc/>
    public void Dispose() => _reference.Dispose();

    internal AVBufferRef* NewReference() => _reference.NewReference();
}
