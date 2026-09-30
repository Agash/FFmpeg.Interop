using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;

namespace FFmpeg.Interop;

/// <summary>An opened encoder or decoder (<see cref="AVCodecContext"/>).</summary>
/// <remarks>Not thread-safe: send and receive from one thread at a time.</remarks>
public abstract unsafe class CodecContext : IDisposable
{
    private readonly SafeCodecContextHandle _handle;

    private protected CodecContext(Codec codec)
    {
        Codec = codec;
        _handle = SafeCodecContextHandle.Allocate(codec.NativePointer);
    }

    /// <summary>The codec implementation.</summary>
    public Codec Codec { get; }

    /// <summary>The native context, owned by this instance and invalid after <see cref="Dispose"/>.</summary>
    public AVCodecContext* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Pointer;
        }
    }

    /// <summary>The time base of the timestamps the context works in.</summary>
    public Rational TimeBase => NativePointer->time_base;

    /// <summary>The picture width.</summary>
    public int Width => NativePointer->width;

    /// <summary>The picture height.</summary>
    public int Height => NativePointer->height;

    /// <summary>The pixel format of the pictures (a hardware format when hardware accelerated).</summary>
    public PixelFormat PixelFormat => NativePointer->pix_fmt;

    /// <summary>The range of sample values the stream signals.</summary>
    public ColorRange ColorRange => (ColorRange)NativePointer->color_range;

    /// <summary>The colour primaries the stream signals.</summary>
    public ColorPrimaries ColorPrimaries => (ColorPrimaries)NativePointer->color_primaries;

    /// <summary>The transfer characteristics the stream signals.</summary>
    public ColorTransfer ColorTransfer => (ColorTransfer)NativePointer->color_trc;

    /// <summary>The matrix from RGB to luma and chroma the stream signals.</summary>
    public ColorSpace ColorSpace => (ColorSpace)NativePointer->colorspace;

    /// <summary>The audio sample rate.</summary>
    public int SampleRate => NativePointer->sample_rate;

    /// <summary>The audio sample format.</summary>
    public SampleFormat SampleFormat => NativePointer->sample_fmt;

    /// <summary>The audio channel layout.</summary>
    public ChannelLayout ChannelLayout => ChannelLayout.FromNative(&NativePointer->ch_layout);

    /// <summary>
    /// Codec-specific setup data: for H.264 in global-header mode the SPS and PPS, for AV1 the sequence
    /// header OBU. Empty when the codec carries it in band.
    /// </summary>
    public ReadOnlySpan<byte> ExtraData =>
        new(NativePointer->extradata, NativePointer->extradata_size);

    /// <summary>
    /// Discards buffered input and output so the context can continue from a new position; after end of
    /// stream it makes a decoder, or an encoder with <see cref="CodecCapabilities.EncoderFlush"/>, reusable.
    /// </summary>
    public void Flush() => avcodec_flush_buffers(NativePointer);

    /// <inheritdoc/>
    public void Dispose()
    {
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private protected void Open(IReadOnlyDictionary<string, string>? options)
    {
        AVDictionary* dictionary = NativeOptions.Create(options);
        try
        {
            FFmpegError.ThrowIfError(
                avcodec_open2(NativePointer, Codec.NativePointer, &dictionary)
            );
            NativeOptions.ThrowIfUnused(dictionary, Codec.Name);
        }
        finally
        {
            LibAVUtil.av_dict_free(&dictionary);
        }
    }

    // Copies setup data into an FFmpeg allocation with the zero padding decoders read into.
    private protected void SetExtraData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        AVCodecContext* context = NativePointer;
        byte* copy = (byte*)LibAVUtil.av_mallocz((nuint)(data.Length + Packet.PaddingSize));
        if (copy is null)
        {
            FFmpegError.ThrowOutOfMemory("av_mallocz");
        }

        data.CopyTo(new Span<byte>(copy, data.Length));
        context->extradata = copy;
        context->extradata_size = data.Length;
    }

    // Maps send/receive return codes to the three outcomes callers act on.
    private protected static CodecStatus Status(int result, string operation) =>
        result switch
        {
            >= 0 => CodecStatus.Available,
            _ when result == LibAVUtil.AVERROR_EAGAIN => CodecStatus.NeedsInput,
            _ when result == LibAVUtil.AVERROR_EOF => CodecStatus.EndOfStream,
            _ => throw FFmpegError.Create(result, operation),
        };
}

/// <summary>The outcome of asking a codec for output.</summary>
public enum CodecStatus
{
    /// <summary>Output was produced.</summary>
    Available,

    /// <summary>No output until more input is sent.</summary>
    NeedsInput,

    /// <summary>The codec has been drained after end of stream; no more output will come.</summary>
    EndOfStream,
}
