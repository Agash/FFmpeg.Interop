using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVFormat;

namespace FFmpeg.Interop;

/// <summary>Writes encoded packets into a media file or URL (a muxer, <see cref="AVFormatContext"/>).</summary>
/// <remarks>Add the streams, <see cref="WriteHeader"/>, write packets, then <see cref="Complete"/>.</remarks>
public sealed unsafe class MediaWriter : IDisposable
{
    private readonly SafeOutputFormatHandle _handle;
    private readonly string _url;
    private bool _headerWritten;

    private MediaWriter(SafeOutputFormatHandle handle, string url)
    {
        _handle = handle;
        _url = url;
    }

    /// <summary>The native context, owned by this instance and invalid after <see cref="Dispose"/>.</summary>
    public AVFormatContext* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Pointer;
        }
    }

    /// <summary>
    /// Whether the format stores codec setup data in its header, so encoders feeding it should set
    /// <see cref="EncoderOptions.GlobalHeader"/>.
    /// </summary>
    public bool RequiresGlobalHeader => (NativePointer->oformat->flags & AVFMT_GLOBALHEADER) != 0;

    /// <summary>Creates a writer.</summary>
    /// <param name="url">A path or a URL of a protocol FFmpeg supports.</param>
    /// <param name="format">The container, for example <c>mp4</c>; null to infer it from the extension.</param>
    /// <returns>The writer.</returns>
    public static MediaWriter Create(string url, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        AVFormatContext* context = null;
        using (Utf8String nativeUrl = new(url))
        using (Utf8String nativeFormat = new(format))
        {
            FFmpegError.ThrowIfError(
                avformat_alloc_output_context2(&context, null, nativeFormat, nativeUrl)
            );
        }

        return new MediaWriter(SafeOutputFormatHandle.Own(context), url);
    }

    /// <summary>Adds a stream carrying an encoder's output.</summary>
    /// <param name="encoder">The opened encoder.</param>
    /// <returns>The stream index to write the encoder's packets with.</returns>
    public int AddStream(Encoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (_headerWritten)
        {
            throw new InvalidOperationException(
                "Streams must be added before the header is written."
            );
        }

        AVStream* stream = avformat_new_stream(NativePointer, null);
        if (stream is null)
        {
            FFmpegError.ThrowOutOfMemory("avformat_new_stream");
        }

        FFmpegError.ThrowIfError(
            LibAVCodec.avcodec_parameters_from_context(stream->codecpar, encoder.NativePointer)
        );
        stream->time_base = encoder.TimeBase;
        return stream->index;
    }

    /// <summary>Opens the output and writes the container header.</summary>
    /// <param name="options">Muxer options, for example <c>movflags=+faststart</c>. Unknown names are an error.</param>
    public void WriteHeader(IReadOnlyDictionary<string, string>? options = null)
    {
        AVFormatContext* context = NativePointer;
        if ((context->oformat->flags & AVFMT_NOFILE) == 0)
        {
            using Utf8String url = new(_url);
            FFmpegError.ThrowIfError(avio_open(&context->pb, url, AVIO_FLAG_WRITE));
        }

        AVDictionary* dictionary = NativeOptions.Create(options);
        try
        {
            FFmpegError.ThrowIfError(avformat_write_header(context, &dictionary));
            NativeOptions.ThrowIfUnused(dictionary, "The muxer");
            _headerWritten = true;
        }
        finally
        {
            LibAVUtil.av_dict_free(&dictionary);
        }
    }

    /// <summary>
    /// Writes a packet, converting its timestamps from <see cref="Packet.TimeBase"/> to the stream's.
    /// The packet is consumed: it is empty afterwards.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <param name="streamIndex">The stream from <see cref="AddStream"/>.</param>
    public void Write(Packet packet, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(packet);
        AVFormatContext* context = RequireHeader();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)streamIndex,
            context->nb_streams,
            nameof(streamIndex)
        );
        AVPacket* native = packet.NativePointer;
        LibAVCodec.av_packet_rescale_ts(
            native,
            native->time_base,
            context->streams[streamIndex]->time_base
        );
        native->stream_index = streamIndex;
        FFmpegError.ThrowIfError(av_interleaved_write_frame(context, native));
    }

    /// <summary>Flushes the interleaving queue and writes the container trailer.</summary>
    public void Complete() => FFmpegError.ThrowIfError(av_write_trailer(RequireHeader()));

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();

    private AVFormatContext* RequireHeader() =>
        _headerWritten
            ? NativePointer
            : throw new InvalidOperationException("Write the header first.");
}
