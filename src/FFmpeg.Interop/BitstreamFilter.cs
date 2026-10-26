using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;

namespace FFmpeg.Interop;

/// <summary>
/// A bitstream filter (<see cref="AVBSFContext"/>): rewrites encoded packets without decoding them, such as
/// <c>h264_mp4toannexb</c> turning MP4's length-prefixed H.264 into Annex B start codes for a raw or MPEG-TS
/// output. Send packets, then receive what comes out, as with a codec.
/// </summary>
public sealed unsafe class BitstreamFilter : IDisposable
{
    private AVBSFContext* _context;

    private BitstreamFilter(AVBSFContext* context) => _context = context;

    /// <summary>The native context, owned by this instance and invalid after <see cref="Dispose"/>.</summary>
    public AVBSFContext* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_context is null, this);
            return _context;
        }
    }

    /// <summary>The codec parameters of what comes out, for a writer's stream.</summary>
    public AVCodecParameters* OutputParameters => NativePointer->par_out;

    /// <summary>The time base of what comes out.</summary>
    public Rational OutputTimeBase => NativePointer->time_base_out;

    /// <summary>A filter for a reader's stream.</summary>
    /// <param name="name">The filter's name, with options as FFmpeg writes them after an <c>=</c>.</param>
    /// <param name="input">The stream whose packets it takes.</param>
    /// <returns>The filter.</returns>
    public static BitstreamFilter Create(string name, MediaStream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Create(name, input.CodecParameters, input.TimeBase);
    }

    /// <summary>A filter for packets of the given parameters.</summary>
    /// <param name="name">The filter's name.</param>
    /// <param name="parameters">The codec parameters of the packets it takes.</param>
    /// <param name="timeBase">Their time base.</param>
    /// <returns>The filter.</returns>
    public static BitstreamFilter Create(
        string name,
        AVCodecParameters* parameters,
        Rational timeBase
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        AVBSFContext* context = null;
        using (Utf8String native = new(name))
        {
            FFmpegError.ThrowIfError(
                av_bsf_list_parse_str(native, &context),
                "av_bsf_list_parse_str"
            );
        }

        BitstreamFilter filter = new(context);
        try
        {
            FFmpegError.ThrowIfError(avcodec_parameters_copy(context->par_in, parameters));
            context->time_base_in = timeBase;
            FFmpegError.ThrowIfError(av_bsf_init(context));
            return filter;
        }
        catch
        {
            filter.Dispose();
            throw;
        }
    }

    /// <summary>Sends a packet; the filter takes its data, leaving it empty.</summary>
    /// <param name="packet">The packet, in the input's time base.</param>
    /// <returns>False when the filter takes nothing until its output is received.</returns>
    public bool TrySend(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return Send(packet.NativePointer);
    }

    /// <summary>Signals end of stream, so the filter puts out what it still holds.</summary>
    public void SendEndOfStream() => _ = Send(null);

    /// <summary>Receives the next filtered packet, if there is one.</summary>
    /// <param name="packet">The packet to fill, in <see cref="OutputTimeBase"/>.</param>
    /// <returns>Whether a packet was produced, more input is needed, or the stream is finished.</returns>
    public CodecStatus Receive(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        AVPacket* native = packet.NativePointer;
        av_packet_unref(native);
        int result = av_bsf_receive_packet(NativePointer, native);
        if (result >= 0)
        {
            native->time_base = OutputTimeBase;
        }

        return result switch
        {
            >= 0 => CodecStatus.Available,
            _ when result == LibAVUtil.AVERROR_EAGAIN => CodecStatus.NeedsInput,
            _ when result == LibAVUtil.AVERROR_EOF => CodecStatus.EndOfStream,
            _ => throw FFmpegError.Create(result, "av_bsf_receive_packet"),
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        AVBSFContext* context = _context;
        _context = null;
        if (context is not null)
        {
            av_bsf_free(&context);
        }
    }

    private bool Send(AVPacket* packet)
    {
        int result = av_bsf_send_packet(NativePointer, packet);
        if (result == LibAVUtil.AVERROR_EAGAIN)
        {
            return false;
        }

        FFmpegError.ThrowIfError(result, "av_bsf_send_packet");
        return true;
    }
}
