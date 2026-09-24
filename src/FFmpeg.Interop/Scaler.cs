using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibSwScale;

namespace FFmpeg.Interop;

/// <summary>The interpolation a <see cref="Scaler"/> uses when resizing.</summary>
public enum ScaleAlgorithm
{
    /// <summary>Bicubic: the usual trade-off between sharpness and speed.</summary>
    Bicubic = SwsFlags.SWS_BICUBIC,

    /// <summary>Bilinear.</summary>
    Bilinear = SwsFlags.SWS_BILINEAR,

    /// <summary>A faster, lower quality bilinear.</summary>
    FastBilinear = SwsFlags.SWS_FAST_BILINEAR,

    /// <summary>Nearest neighbour.</summary>
    Point = SwsFlags.SWS_POINT,

    /// <summary>Area averaging, good for downscaling.</summary>
    Area = SwsFlags.SWS_AREA,

    /// <summary>Lanczos: sharpest, slowest.</summary>
    Lanczos = SwsFlags.SWS_LANCZOS,

    /// <summary>Natural bicubic spline.</summary>
    Spline = SwsFlags.SWS_SPLINE,
}

/// <summary>Options for a <see cref="Scaler"/>.</summary>
public sealed record ScalerOptions
{
    /// <summary>The interpolation when the size changes.</summary>
    public ScaleAlgorithm Algorithm { get; init; } = ScaleAlgorithm.Bicubic;

    /// <summary>Round accurately rather than fast; needed for bit-exact comparisons.</summary>
    public bool AccurateRounding { get; init; }

    /// <summary>Interpolate chroma at full resolution when converting to RGB.</summary>
    public bool FullChromaInterpolation { get; init; }

    /// <summary>Worker threads; 0 lets FFmpeg choose.</summary>
    public int ThreadCount { get; init; } = 1;
}

/// <summary>
/// Converts pictures between sizes and pixel formats (<see cref="SwsContext"/>). The conversion is set up
/// from the frames themselves, on first use and again whenever their sizes or formats change.
/// </summary>
public sealed unsafe class Scaler : IDisposable
{
    private readonly SafeScalerHandle _handle;

    /// <summary>Creates a scaler.</summary>
    /// <param name="options">Options, or null for the defaults.</param>
    public Scaler(ScalerOptions? options = null)
    {
        options ??= new();
        _handle = SafeScalerHandle.Allocate();
        SwsContext* context = _handle.Pointer;
        SwsFlags flags = (SwsFlags)options.Algorithm;
        if (options.AccurateRounding)
        {
            flags |= SwsFlags.SWS_ACCURATE_RND;
        }

        if (options.FullChromaInterpolation)
        {
            flags |= SwsFlags.SWS_FULL_CHR_H_INT;
        }

        context->flags = (uint)flags;
        context->threads = options.ThreadCount;
    }

    /// <summary>The native context, owned by this instance and invalid after <see cref="Dispose"/>.</summary>
    public SwsContext* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Pointer;
        }
    }

    /// <summary>Whether the scaler can read a pixel format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether it is supported as input.</returns>
    public static bool IsSupportedInput(PixelFormat format) => sws_test_format(format, 0) != 0;

    /// <summary>Whether the scaler can write a pixel format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether it is supported as output.</returns>
    public static bool IsSupportedOutput(PixelFormat format) => sws_test_format(format, 1) != 0;

    /// <summary>
    /// Converts <paramref name="source"/> into <paramref name="destination"/>, whose width, height and
    /// pixel format say what to produce. The destination's previous data is released and replaced with a
    /// buffer from the scaler's pool, so a loop that reuses one destination frame allocates nothing once
    /// the pool is warm. Timestamps and other properties are copied from the source.
    /// </summary>
    /// <param name="source">The picture to convert.</param>
    /// <param name="destination">The frame to write, with its size and format set.</param>
    public void Scale(Frame source, Frame destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        int width = destination.Width;
        int height = destination.Height;
        PixelFormat format = destination.PixelFormat;
        if (width <= 0 || height <= 0 || format == PixelFormat.None)
        {
            throw new ArgumentException(
                "Set the destination's width, height and pixel format first.",
                nameof(destination)
            );
        }

        // Existing buffers are never reused in place: their strides were computed for whatever geometry
        // they were allocated with, and writing a larger picture through them would overrun them.
        AVFrame* frame = destination.NativePointer;
        LibAVUtil.av_frame_unref(frame);
        frame->width = width;
        frame->height = height;
        frame->format = (int)format.Value;

        int size = FFmpegError.ThrowIfError(
            LibAVUtil.av_image_get_buffer_size(format, width, height, BufferAlignment)
        );
        frame->buf.e0 = LibAVUtil.av_buffer_pool_get(Pool(size + Packet.PaddingSize));
        if (frame->buf.e0 is null)
        {
            FFmpegError.ThrowOutOfMemory("av_buffer_pool_get");
        }

        FFmpegError.ThrowIfError(
            LibAVUtil.av_image_fill_arrays(
                (byte**)&frame->data,
                (int*)&frame->linesize,
                frame->buf.e0->data,
                format,
                width,
                height,
                BufferAlignment
            )
        );
        FFmpegError.ThrowIfError(sws_scale_frame(NativePointer, frame, source.NativePointer));
        destination.CopyPropertiesFrom(source);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _handle.Dispose();
        _pool?.Dispose();
    }

    // Row alignment of pooled pictures: enough for every SIMD path swscale and the encoders take.
    private const int BufferAlignment = 64;

    private SafeBufferPoolHandle? _pool;
    private int _poolSize;

    private AVBufferPool* Pool(int size)
    {
        if (_pool is null || _poolSize != size)
        {
            _pool?.Dispose();
            _pool = SafeBufferPoolHandle.Create(size);
            _poolSize = size;
        }

        return _pool.Pointer;
    }
}
