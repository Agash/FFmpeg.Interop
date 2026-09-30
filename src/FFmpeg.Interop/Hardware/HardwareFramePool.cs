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
    private readonly Lock _gate = new();
    private IDisposable? _attachment;

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

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _attachment?.Dispose();
            _attachment = null;
        }

        _reference.Dispose();
    }

    // Per-pool state an import needs, created on first use and released with the pool.
    internal T Attachment<T>(Func<HardwareFramePool, T> create)
        where T : class, IDisposable
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_reference.IsClosed, this);
            return _attachment as T ?? (T)(_attachment = create(this));
        }
    }

    internal AVBufferRef* NewReference() => _reference.NewReference();

    // Makes destination a frame of this pool over a surface an import built, in the shape FFmpeg's own
    // frames of the pool's type have: buffer owns the surface and data[dataIndex] points at it.
    internal void Adopt(Frame destination, AVBufferRef* buffer, int dataIndex, void* data)
    {
        AVFrame* frame = destination.NativePointer;
        av_frame_unref(frame);
        frame->buf.e0 = buffer;
        frame->data[dataIndex] = (byte*)data;
        frame->format = (int)Format.Value;
        frame->width = Width;
        frame->height = Height;
        frame->hw_frames_ctx = NewReference();
    }
}
