using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVFormat;

namespace FFmpeg.Interop;

/// <summary>Writes encoded packets into a media file or URL (a muxer, <see cref="AVFormatContext"/>).</summary>
/// <remarks>Add the streams, <see cref="WriteHeader"/>, write packets, then <see cref="Complete"/>.</remarks>
public sealed unsafe class MediaWriter : IDisposable
{
    private readonly SafeOutputFormatHandle _handle;
    private readonly string? _url;
    private readonly Interruption _interruption = new();
    private readonly StreamIO? _io;
    private bool _headerWritten;

    private MediaWriter(SafeOutputFormatHandle handle, string? url, StreamIO? io)
    {
        _handle = handle;
        _url = url;
        _io = io;
        handle.Pointer->interrupt_callback = _interruption.Callback;
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

        return new MediaWriter(SafeOutputFormatHandle.Own(context), url, null);
    }

    /// <summary>
    /// Creates a writer into a stream: a muxer over any writable <see cref="Stream"/>. Formats that go
    /// back to finish their header (MP4 without fragmentation) need a seekable stream. The stream stays
    /// the caller's.
    /// </summary>
    /// <param name="stream">The output, writable.</param>
    /// <param name="format">The container, for example <c>matroska</c>; a stream has no extension to infer it from.</param>
    /// <returns>The writer.</returns>
    public static MediaWriter CreateStream(Stream stream, string format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(format);
        AVFormatContext* context = null;
        using (Utf8String nativeFormat = new(format))
        {
            FFmpegError.ThrowIfError(
                avformat_alloc_output_context2(&context, null, nativeFormat, null)
            );
        }

        SafeOutputFormatHandle handle = SafeOutputFormatHandle.Own(context);
        try
        {
            StreamIO io = new(stream, write: true);
            context->pb = io.Context;
            context->flags |= AVFMT_FLAG_CUSTOM_IO;
            return new MediaWriter(handle, null, io);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
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

    /// <summary>
    /// Adds a stream copied as it is from a reader's (stream copy, a remux): its codec parameters, with
    /// the source's time base; write the reader's packets to it unchanged.
    /// </summary>
    /// <param name="source">The stream to copy.</param>
    /// <returns>The stream index to write the source's packets with.</returns>
    public int AddStream(MediaStream source)
    {
        ArgumentNullException.ThrowIfNull(source);
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
            LibAVCodec.avcodec_parameters_copy(stream->codecpar, source.CodecParameters)
        );

        // The tag names the codec in the source's container; the muxer picks its own.
        stream->codecpar->codec_tag = 0;
        stream->time_base = source.TimeBase;
        return stream->index;
    }

    /// <summary>Opens the output and writes the container header.</summary>
    /// <param name="options">Muxer options, for example <c>movflags=+faststart</c>. Unknown names are an error.</param>
    /// <param name="cancellationToken">Ends an open or write that blocks on a network output.</param>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public void WriteHeader(
        IReadOnlyDictionary<string, string>? options = null,
        CancellationToken cancellationToken = default
    )
    {
        AVFormatContext* context = NativePointer;
        using Interruption.Scope during = _interruption.During(cancellationToken);
        if (_io is null && (context->oformat->flags & AVFMT_NOFILE) == 0)
        {
            using Utf8String url = new(_url);
            AVIOInterruptCB interrupt = context->interrupt_callback;
            FFmpegError.ThrowIfError(
                _interruption.Checked(
                    avio_open2(&context->pb, url, AVIO_FLAG_WRITE, &interrupt, null)
                ),
                "avio_open2"
            );
        }

        AVDictionary* dictionary = NativeOptions.Create(options);
        try
        {
            FFmpegError.ThrowIfError(
                _interruption.Checked(avformat_write_header(context, &dictionary)),
                "avformat_write_header"
            );
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
    /// <param name="streamIndex">The stream from <see cref="AddStream(Encoder)"/> or <see cref="AddStream(MediaStream)"/>.</param>
    /// <param name="cancellationToken">Ends a write that blocks on a network output.</param>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public void Write(Packet packet, int streamIndex, CancellationToken cancellationToken = default)
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
        using (_interruption.During(cancellationToken))
        {
            FFmpegError.ThrowIfError(
                _interruption.Checked(av_interleaved_write_frame(context, native)),
                "av_interleaved_write_frame"
            );
        }
    }

    /// <summary>Flushes the interleaving queue and writes the container trailer.</summary>
    /// <param name="cancellationToken">Ends a write that blocks on a network output.</param>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public void Complete(CancellationToken cancellationToken = default)
    {
        AVFormatContext* context = RequireHeader();
        using (_interruption.During(cancellationToken))
        {
            FFmpegError.ThrowIfError(
                _interruption.Checked(av_write_trailer(context)),
                "av_write_trailer"
            );
        }

        // What the muxer buffered reaches the stream before the caller goes on with it.
        if (_io is not null)
        {
            avio_flush(_io.Context);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _handle.Dispose();
        _io?.Dispose();
        _interruption.Dispose();
    }

    private AVFormatContext* RequireHeader() =>
        _headerWritten
            ? NativePointer
            : throw new InvalidOperationException("Write the header first.");
}
