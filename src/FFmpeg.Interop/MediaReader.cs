using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVFormat;

namespace FFmpeg.Interop;

/// <summary>Reads packets from a media file or URL (a demuxer, <see cref="AVFormatContext"/>).</summary>
public sealed unsafe class MediaReader : IDisposable
{
    private readonly SafeInputFormatHandle _handle;

    private MediaReader(SafeInputFormatHandle handle)
    {
        _handle = handle;
        AVFormatContext* context = handle.Pointer;
        MediaStream[] streams = new MediaStream[context->nb_streams];
        for (int i = 0; i < streams.Length; i++)
        {
            streams[i] = new MediaStream(this, context->streams[i]);
        }

        Streams = streams;
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

    /// <summary>The streams in the input.</summary>
    public IReadOnlyList<MediaStream> Streams { get; }

    /// <summary>The duration of the input, when known.</summary>
    public TimeSpan? Duration =>
        NativePointer->duration == LibAVUtil.AV_NOPTS_VALUE
            ? null
            : Rational.Microseconds.ToTimeSpan(NativePointer->duration);

    /// <summary>Opens a file or URL and reads enough of it to describe its streams.</summary>
    /// <param name="url">A path or a URL of a protocol FFmpeg supports.</param>
    /// <param name="options">Demuxer and protocol options. Unknown names are an error.</param>
    /// <returns>The reader.</returns>
    public static MediaReader Open(string url, IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        AVFormatContext* context = null;
        AVDictionary* dictionary = NativeOptions.Create(options);
        try
        {
            using (Utf8String native = new(url))
            {
                FFmpegError.ThrowIfError(avformat_open_input(&context, native, null, &dictionary));
            }

            SafeInputFormatHandle handle = SafeInputFormatHandle.Own(context);
            try
            {
                NativeOptions.ThrowIfUnused(dictionary, "The demuxer");
                FFmpegError.ThrowIfError(avformat_find_stream_info(context, null));
                return new MediaReader(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            LibAVUtil.av_dict_free(&dictionary);
        }
    }

    /// <summary>The best stream of a kind, as FFmpeg ranks them.</summary>
    /// <param name="type">The kind of stream.</param>
    /// <returns>The stream, or null when there is none.</returns>
    public MediaStream? FindBestStream(MediaType type)
    {
        int index = av_find_best_stream(NativePointer, (AVMediaType)type, -1, -1, null, 0);
        return index < 0 ? null : Streams[index];
    }

    /// <summary>Reads the next packet of any stream.</summary>
    /// <param name="packet">The packet to fill, with its time base set to its stream's.</param>
    /// <returns>False at end of input.</returns>
    public bool TryReadPacket(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        AVPacket* native = packet.NativePointer;

        // av_read_frame fills a blank packet; it does not release what a reused one still holds.
        LibAVCodec.av_packet_unref(native);
        int result = av_read_frame(NativePointer, native);
        if (result == LibAVUtil.AVERROR_EOF)
        {
            return false;
        }

        FFmpegError.ThrowIfError(result, "av_read_frame");
        native->time_base = Streams[native->stream_index].TimeBase;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();
}

/// <summary>One stream of a <see cref="MediaReader"/>; valid while the reader is open.</summary>
public sealed unsafe class MediaStream
{
    private readonly MediaReader _reader;
    private readonly AVStream* _stream;

    internal MediaStream(MediaReader reader, AVStream* stream)
    {
        _reader = reader;
        _stream = stream;
    }

    /// <summary>The native stream.</summary>
    public AVStream* NativePointer
    {
        get
        {
            _ = _reader.NativePointer;
            return _stream;
        }
    }

    /// <summary>The stream's index in the input.</summary>
    public int Index => NativePointer->index;

    /// <summary>The kind of data.</summary>
    public MediaType MediaType => (MediaType)NativePointer->codecpar->codec_type;

    /// <summary>The coding format.</summary>
    public CodecId CodecId => NativePointer->codecpar->codec_id;

    /// <summary>The time base of the stream's packet timestamps.</summary>
    public Rational TimeBase => NativePointer->time_base;

    /// <summary>The average frame rate, for video.</summary>
    public Rational FrameRate => NativePointer->avg_frame_rate;

    /// <summary>The picture width, for video.</summary>
    public int Width => NativePointer->codecpar->width;

    /// <summary>The picture height, for video.</summary>
    public int Height => NativePointer->codecpar->height;

    /// <summary>The sample rate, for audio.</summary>
    public int SampleRate => NativePointer->codecpar->sample_rate;

    /// <summary>The codec setup data the container holds for the stream.</summary>
    public ReadOnlySpan<byte> ExtraData =>
        new(NativePointer->codecpar->extradata, NativePointer->codecpar->extradata_size);

    /// <summary>Opens a decoder configured from the stream's parameters.</summary>
    /// <param name="codec">
    /// The decoder, or null for FFmpeg's preferred one for the stream; with a hardware device in the
    /// options, the preferred one that can decode on it.
    /// </param>
    /// <param name="options">Options, or null for the defaults. The packet time base is the stream's.</param>
    /// <returns>The decoder.</returns>
    public Decoder CreateDecoder(Codec? codec = null, DecoderOptions? options = null) =>
        Decoder.Create(
            codec
                ?? (
                    options?.HardwareDevice is { } device
                        ? Codec.FindDecoder(CodecId, device.Type)
                        : Codec.FindDecoder(CodecId)
                ),
            NativePointer->codecpar,
            (options ?? new()) with
            {
                PacketTimeBase = options?.PacketTimeBase ?? TimeBase,
            }
        );
}
