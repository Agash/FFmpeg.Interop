using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVFormat;

namespace FFmpeg.Interop;

/// <summary>
/// An I/O context over a <see cref="System.IO.Stream"/> (<c>avio_alloc_context</c>): FFmpeg reads,
/// writes and seeks through it, so a demuxer or muxer works on any stream, not only files and URLs. The
/// format context is marked <c>AVFMT_FLAG_CUSTOM_IO</c>, so freeing the context is this owner's job.
/// </summary>
internal sealed unsafe class StreamIO : IDisposable
{
    // libavformat's own default buffer size for protocols.
    private const int BufferSize = 64 * 1024;

    private readonly Stream _stream;
    private GCHandle _self;
    private AVIOContext* _context;

    public StreamIO(Stream stream, bool write)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (write ? !stream.CanWrite : !stream.CanRead)
        {
            throw new ArgumentException(
                write ? "The stream cannot be written." : "The stream cannot be read.",
                nameof(stream)
            );
        }

        _stream = stream;
        _self = GCHandle.Alloc(this);
        byte* buffer = (byte*)LibAVUtil.av_malloc(BufferSize);
        if (buffer is null)
        {
            _self.Free();
            FFmpegError.ThrowOutOfMemory("av_malloc");
        }

        _context = avio_alloc_context(
            buffer,
            BufferSize,
            write ? 1 : 0,
            (void*)GCHandle.ToIntPtr(_self),
            write ? null : &Read,
            write ? &Write : null,
            stream.CanSeek ? &Seek : null
        );
        if (_context is null)
        {
            LibAVUtil.av_free(buffer);
            _self.Free();
            FFmpegError.ThrowOutOfMemory("avio_alloc_context");
        }
    }

    /// <summary>The I/O context, to set as a format context's <c>pb</c>.</summary>
    public AVIOContext* Context => _context;

    public void Dispose()
    {
        if (_context is not null)
        {
            // The buffer may have been replaced by FFmpeg since; the context holds the current one.
            AVIOContext* context = _context;
            LibAVUtil.av_freep(&context->buffer);
            avio_context_free(&context);
            _context = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private static StreamIO Of(void* opaque) => (StreamIO)GCHandle.FromIntPtr((nint)opaque).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(void* opaque, byte* buffer, int size)
    {
        try
        {
            int read = Of(opaque)._stream.Read(new Span<byte>(buffer, size));
            return read == 0 ? LibAVUtil.AVERROR_EOF : read;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: an exception cannot cross back into FFmpeg; the failure ends
            // the call that is reading, which reports it.
            return LibAVUtil.AVERROR_EXTERNAL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Write(void* opaque, byte* buffer, int size)
    {
        try
        {
            Of(opaque)._stream.Write(new ReadOnlySpan<byte>(buffer, size));
            return size;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Deliberately not rethrown: an exception cannot cross back into FFmpeg; the failure ends
            // the call that is writing, which reports it.
            return LibAVUtil.AVERROR_EXTERNAL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            Stream stream = Of(opaque)._stream;

            // AVSEEK_SIZE asks for the stream's size without moving; AVSEEK_FORCE is only a hint.
            if ((whence & AVSEEK_SIZE) != 0)
            {
                return stream.Length;
            }

            return stream.Seek(
                offset,
                (whence & ~AVSEEK_FORCE) switch
                {
                    1 => SeekOrigin.Current,
                    2 => SeekOrigin.End,
                    _ => SeekOrigin.Begin,
                }
            );
        }
        catch (Exception exception)
            when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            // Deliberately not rethrown: an exception cannot cross back into FFmpeg; a failed seek is
            // reported to the caller as an error, which FFmpeg handles.
            return LibAVUtil.AVERROR_EXTERNAL;
        }
    }
}
