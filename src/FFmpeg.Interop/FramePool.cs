using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// Picture buffers for system-memory frames, taken from a pool and returned to it when the last frame
/// referring to one lets go. A frame filled for every picture, a download or a conversion, then allocates
/// nothing once warm, and a picture still held elsewhere keeps its buffer.
/// </summary>
/// <remarks>
/// The pool holds buffers of one size at a time; asking for another size starts a new pool, and buffers
/// of the old one stay valid until released. Not thread-safe: one pool serves one producer.
/// </remarks>
public sealed unsafe class FramePool : IDisposable
{
    // Row alignment of pooled pictures: enough for every SIMD path swscale and the encoders take.
    private const int Alignment = 64;

    private SafeBufferPoolHandle? _pool;
    private int _size;

    /// <summary>Gives a frame a picture from the pool, releasing any it held.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">A system-memory pixel format.</param>
    public void Allocate(Frame frame, int width, int height, PixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (format.IsHardware || format == PixelFormat.None)
        {
            throw new ArgumentException($"{format} is not a system-memory format.", nameof(format));
        }

        AVFrame* native = frame.NativePointer;
        LibAVUtil.av_frame_unref(native);
        native->width = width;
        native->height = height;
        native->format = (int)format.Value;

        // Padded as packets are, for readers that load past the last row.
        int size =
            FFmpegError.ThrowIfError(
                LibAVUtil.av_image_get_buffer_size(format.Value, width, height, Alignment)
            ) + Packet.PaddingSize;
        if (_pool is null || _size != size)
        {
            _pool?.Dispose();
            _pool = SafeBufferPoolHandle.Create(size);
            _size = size;
        }

        native->buf.e0 = LibAVUtil.av_buffer_pool_get(_pool.Pointer);
        if (native->buf.e0 is null)
        {
            FFmpegError.ThrowOutOfMemory("av_buffer_pool_get");
        }

        FFmpegError.ThrowIfError(
            LibAVUtil.av_image_fill_arrays(
                (byte**)&native->data,
                (int*)&native->linesize,
                native->buf.e0->data,
                format.Value,
                width,
                height,
                Alignment
            )
        );
    }

    /// <inheritdoc/>
    public void Dispose() => _pool?.Dispose();
}
