using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVFormat;

namespace FFmpeg.Interop;

/// <summary>Reads packets from a media file or URL (a demuxer, <see cref="AVFormatContext"/>).</summary>
public sealed unsafe class MediaReader : IDisposable
{
    private readonly SafeInputFormatHandle _handle;
    private readonly Interruption _interruption;
    private readonly StreamIO? _io;

    private MediaReader(SafeInputFormatHandle handle, Interruption interruption, StreamIO? io)
    {
        _handle = handle;
        _interruption = interruption;
        _io = io;
        AVFormatContext* context = handle.Pointer;
        MediaStream[] streams = new MediaStream[context->nb_streams];
        for (int i = 0; i < streams.Length; i++)
        {
            streams[i] = new MediaStream(this, context->streams[i]);
        }

        Streams = ImmutableCollectionsMarshal.AsImmutableArray(streams);
        Metadata = NativeMetadata.Read(context->metadata);
        ImmutableArray<Chapter>.Builder chapters = ImmutableArray.CreateBuilder<Chapter>(
            (int)context->nb_chapters
        );
        for (int i = 0; i < context->nb_chapters; i++)
        {
            AVChapter* chapter = context->chapters[i];
            Rational timeBase = chapter->time_base;
            chapters.Add(
                new Chapter(
                    timeBase.ToTimeSpan(chapter->start),
                    timeBase.ToTimeSpan(chapter->end),
                    NativeMetadata.Read(chapter->metadata)
                )
            );
        }

        Chapters = chapters.MoveToImmutable();
    }

    /// <summary>The container's tags, such as <c>title</c> or <c>encoder</c>.</summary>
    public ImmutableDictionary<string, string> Metadata { get; }

    /// <summary>The input's chapters, in order.</summary>
    public ImmutableArray<Chapter> Chapters { get; }

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
    public ImmutableArray<MediaStream> Streams { get; }

    /// <summary>The duration of the input, when known.</summary>
    public TimeSpan? Duration =>
        NativePointer->duration == LibAVUtil.AV_NOPTS_VALUE
            ? null
            : Rational.Microseconds.ToTimeSpan(NativePointer->duration);

    /// <summary>Opens a file or URL and reads enough of it to describe its streams.</summary>
    /// <param name="url">A path or a URL of a protocol FFmpeg supports.</param>
    /// <param name="options">Demuxer and protocol options. Unknown names are an error.</param>
    /// <param name="format">
    /// The input format's short name (<c>matroska</c>, <c>h264</c>, <c>lavfi</c>), or null to probe for
    /// it. Needed for inputs that carry no signature of their own, such as raw elementary streams.
    /// </param>
    /// <param name="cancellationToken">Ends the open, which can block on a network input.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="OperationCanceledException">The open was cancelled.</exception>
    public static MediaReader Open(
        string url,
        IReadOnlyDictionary<string, string>? options = null,
        string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(url);
        return OpenCore(url, null, options, format, cancellationToken);
    }

    /// <summary>
    /// Opens a stream and reads enough of it to describe its streams: a demuxer over any readable
    /// <see cref="Stream"/>, seeking where the stream can. The stream stays the caller's.
    /// </summary>
    /// <param name="stream">The input, readable; read from its current position.</param>
    /// <param name="options">Demuxer options. Unknown names are an error.</param>
    /// <param name="format">The input format's short name, or null to probe for it.</param>
    /// <param name="cancellationToken">Ends the open.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="OperationCanceledException">The open was cancelled.</exception>
    public static MediaReader OpenStream(
        Stream stream,
        IReadOnlyDictionary<string, string>? options = null,
        string? format = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OpenCore(null, stream, options, format, cancellationToken);
    }

    private static MediaReader OpenCore(
        string? url,
        Stream? stream,
        IReadOnlyDictionary<string, string>? options,
        string? format,
        CancellationToken cancellationToken
    )
    {
        AVInputFormat* inputFormat = format is null ? null : FindInputFormat(format);
        Interruption interruption = new();
        StreamIO? io = null;
        AVDictionary* dictionary = NativeOptions.Create(options);
        try
        {
            // Allocated here rather than by avformat_open_input, so the interrupt callback and a stream's
            // I/O context are in place before anything blocks.
            AVFormatContext* context = avformat_alloc_context();
            if (context is null)
            {
                FFmpegError.ThrowOutOfMemory("avformat_alloc_context");
            }

            context->interrupt_callback = interruption.Callback;
            if (stream is not null)
            {
                io = new StreamIO(stream, write: false);
                context->pb = io.Context;
                context->flags |= AVFMT_FLAG_CUSTOM_IO;
            }

            // avformat_open_input frees the context when it fails.
            using (interruption.During(cancellationToken))
            using (Utf8String native = new(url ?? string.Empty))
            {
                FFmpegError.ThrowIfError(
                    interruption.Checked(
                        avformat_open_input(&context, native, inputFormat, &dictionary)
                    ),
                    "avformat_open_input"
                );
            }

            SafeInputFormatHandle handle = SafeInputFormatHandle.Own(context);
            try
            {
                NativeOptions.ThrowIfUnused(dictionary, "The demuxer");
                using (interruption.During(cancellationToken))
                {
                    FFmpegError.ThrowIfError(
                        interruption.Checked(avformat_find_stream_info(context, null)),
                        "avformat_find_stream_info"
                    );
                }

                return new MediaReader(handle, interruption, io);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            io?.Dispose();
            interruption.Dispose();
            throw;
        }
        finally
        {
            LibAVUtil.av_dict_free(&dictionary);
        }
    }

    /// <summary>
    /// Opens a capture device through libavdevice: a camera or screen (<c>v4l2</c>, <c>dshow</c>,
    /// <c>avfoundation</c>, <c>gdigrab</c>), a sound card (<c>alsa</c>, <c>pulse</c>), or a filter graph
    /// source (<c>lavfi</c>).
    /// </summary>
    /// <param name="format">The device format's short name.</param>
    /// <param name="device">The device, in the format's own terms: <c>/dev/video0</c>, <c>video=Camera</c>, <c>0:0</c>.</param>
    /// <param name="options">Device options such as <c>video_size</c>, <c>framerate</c> or <c>input_format</c>.</param>
    /// <returns>The reader.</returns>
    public static MediaReader OpenDevice(
        string format,
        string device,
        IReadOnlyDictionary<string, string>? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(format);
        _ = s_devicesRegistered.Value;
        return Open(device, options, format);
    }

    /// <summary>
    /// The devices a capture format finds (<c>avdevice_list_input_sources</c>): cameras for <c>v4l2</c> or
    /// <c>dshow</c>, sound cards for <c>alsa</c> or <c>pulse</c>. Formats that cannot list their devices,
    /// such as <c>avfoundation</c>, list none.
    /// </summary>
    /// <param name="format">The device format's short name.</param>
    /// <returns>The devices: the name to open each by, and its description.</returns>
    public static ImmutableArray<CaptureDevice> ListDevices(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _ = s_devicesRegistered.Value;
        AVInputFormat* input = FindInputFormat(format);
        AVDeviceInfoList* list = null;
        int result = LibAVDevice.avdevice_list_input_sources(input, null, null, &list);
        try
        {
            if (result < 0)
            {
                return [];
            }

            ImmutableArray<CaptureDevice>.Builder devices =
                ImmutableArray.CreateBuilder<CaptureDevice>(list->nb_devices);
            for (int i = 0; i < list->nb_devices; i++)
            {
                AVDeviceInfo* device = list->devices[i];
                ImmutableArray<MediaType>.Builder types = ImmutableArray.CreateBuilder<MediaType>(
                    device->nb_media_types
                );
                for (int t = 0; t < device->nb_media_types; t++)
                {
                    types.Add((MediaType)device->media_types[t]);
                }

                devices.Add(
                    new CaptureDevice(
                        Marshal.PtrToStringUTF8((nint)device->device_name) ?? string.Empty,
                        Marshal.PtrToStringUTF8((nint)device->device_description) ?? string.Empty,
                        types.ToImmutable()
                    )
                );
            }

            return devices.ToImmutable();
        }
        finally
        {
            LibAVDevice.avdevice_free_list_devices(&list);
        }
    }

    // libavdevice's formats are found by name only once registered, which is process-wide and idempotent.
    private static readonly Lazy<bool> s_devicesRegistered = new(() =>
    {
        LibAVDevice.avdevice_register_all();
        return true;
    });

    private static AVInputFormat* FindInputFormat(string format)
    {
        using Utf8String name = new(format);
        AVInputFormat* found = av_find_input_format(name);
        return found is not null
            ? found
            : throw new ArgumentException(
                $"FFmpeg has no input format '{format}'.",
                nameof(format)
            );
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
    /// <param name="cancellationToken">Ends a read that blocks on a network input.</param>
    /// <returns>False at end of input.</returns>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public bool TryReadPacket(Packet packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        AVPacket* native = packet.NativePointer;

        // av_read_frame fills a blank packet; it does not release what a reused one still holds.
        LibAVCodec.av_packet_unref(native);
        int result;
        using (_interruption.During(cancellationToken))
        {
            result = _interruption.Checked(av_read_frame(NativePointer, native));
        }

        if (result == LibAVUtil.AVERROR_EOF)
        {
            return false;
        }

        FFmpegError.ThrowIfError(result, "av_read_frame");
        native->time_base = Streams[native->stream_index].TimeBase;
        return true;
    }

    /// <summary>
    /// Moves to the keyframe at or before a position, so decoding from there shows the picture at the
    /// position once frames before it are dropped. Decoders reading the input are flushed by the caller.
    /// </summary>
    /// <param name="position">Where to go, from the start of the input.</param>
    /// <param name="stream">The stream whose keyframes count; null for the input's default.</param>
    /// <param name="cancellationToken">Ends a seek that blocks on a network input.</param>
    /// <exception cref="OperationCanceledException">The seek was cancelled.</exception>
    public void Seek(
        TimeSpan position,
        MediaStream? stream = null,
        CancellationToken cancellationToken = default
    )
    {
        // avformat_seek_file takes a range: anything up to the position, the position preferred.
        Rational timeBase = stream?.TimeBase ?? Rational.Microseconds;
        long target = timeBase.FromTimeSpan(position);
        using (_interruption.During(cancellationToken))
        {
            FFmpegError.ThrowIfError(
                _interruption.Checked(
                    avformat_seek_file(
                        NativePointer,
                        stream?.Index ?? -1,
                        long.MinValue,
                        target,
                        target,
                        0
                    )
                ),
                "avformat_seek_file"
            );
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _handle.Dispose();
        _io?.Dispose();
        _interruption.Dispose();
    }
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

    /// <summary>The stream's tags, such as <c>language</c> or <c>title</c>.</summary>
    public ImmutableDictionary<string, string> Metadata =>
        NativeMetadata.Read(NativePointer->metadata);

    /// <summary>How the stream is meant to be used.</summary>
    public StreamDisposition Disposition => (StreamDisposition)NativePointer->disposition;

    /// <summary>The codec parameters, for a writer copying the stream as it is.</summary>
    public AVCodecParameters* CodecParameters => NativePointer->codecpar;

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

/// <summary>A device a capture format found.</summary>
/// <param name="Name">The name to open it by with <see cref="MediaReader.OpenDevice"/>.</param>
/// <param name="Description">What people call it.</param>
/// <param name="MediaTypes">The kinds of media it captures; empty when the format does not say.</param>
public sealed record CaptureDevice(
    string Name,
    string Description,
    ImmutableArray<MediaType> MediaTypes
);
