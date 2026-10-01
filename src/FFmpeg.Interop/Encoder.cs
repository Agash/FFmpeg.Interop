using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;

namespace FFmpeg.Interop;

/// <summary>Options shared by video and audio encoders.</summary>
public abstract record EncoderOptions
{
    /// <summary>The target bit rate in bits per second, or null for the codec's default rate control.</summary>
    public long? BitRate { get; init; }

    /// <summary>
    /// The peak bit rate the rate control may reach (<c>maxrate</c>), in bits per second. With
    /// <see cref="BufferSize"/> it caps bursts, which matters when a network path has a fixed capacity.
    /// </summary>
    public long? MaxRate { get; init; }

    /// <summary>The rate control buffer (VBV/HRD) size in bits (<c>bufsize</c>).</summary>
    public int? BufferSize { get; init; }

    /// <summary>
    /// Output each packet as soon as its frame is encoded, without reordering delay
    /// (<c>AV_CODEC_FLAG_LOW_DELAY</c>).
    /// </summary>
    public bool LowDelay { get; init; }

    /// <summary>Encoding threads; 0 lets FFmpeg choose.</summary>
    public int ThreadCount { get; init; }

    /// <summary>
    /// Put codec setup data in <see cref="CodecContext.ExtraData"/> instead of in band, as containers
    /// such as MP4 and Matroska want (<see cref="MediaWriter.RequiresGlobalHeader"/>).
    /// </summary>
    public bool GlobalHeader { get; init; }

    /// <summary>Codec-private options, as <c>ffmpeg -c:v name -option value</c> takes them. Unknown names are an error.</summary>
    public IReadOnlyDictionary<string, string>? CodecOptions { get; init; }
}

/// <summary>Options for a video <see cref="Encoder"/>.</summary>
public sealed record VideoEncoderOptions : EncoderOptions
{
    /// <summary>The picture width.</summary>
    public required int Width { get; init; }

    /// <summary>The picture height.</summary>
    public required int Height { get; init; }

    /// <summary>
    /// The input pixel format; for a hardware encoder the hardware format, with
    /// <see cref="HardwareFrames"/> set.
    /// </summary>
    public required PixelFormat PixelFormat { get; init; }

    /// <summary>The time base of the input frames' timestamps, typically 1/frame rate or 1/90000.</summary>
    public required Rational TimeBase { get; init; }

    /// <summary>The nominal frame rate, which rate control uses; null when variable.</summary>
    public Rational? FrameRate { get; init; }

    /// <summary>Frames between key frames, or null for the codec's default.</summary>
    public int? GopSize { get; init; }

    /// <summary>The maximum number of consecutive B-frames; 0 for low-latency streaming.</summary>
    public int? MaxBFrames { get; init; }

    /// <summary>The pool the input surfaces come from, for encoders that take hardware frames.</summary>
    public HardwareFramePool? HardwareFrames { get; init; }

    /// <summary>
    /// The range the encoded stream signals, and that frames in a YUV format must already be in; for
    /// RGB input to a hardware encoder that converts, the range it converts to.
    /// </summary>
    public ColorRange? ColorRange { get; init; }

    /// <summary>The colour primaries the encoded stream signals.</summary>
    public ColorPrimaries? ColorPrimaries { get; init; }

    /// <summary>The transfer characteristics the encoded stream signals.</summary>
    public ColorTransfer? ColorTransfer { get; init; }

    /// <summary>The matrix the encoded stream signals, and that an RGB-converting encoder applies.</summary>
    public ColorSpace? ColorSpace { get; init; }

    /// <summary>The device, for hardware encoders that take a device rather than frames.</summary>
    public HardwareDevice? HardwareDevice { get; init; }
}

/// <summary>Options for an audio <see cref="Encoder"/>.</summary>
public sealed record AudioEncoderOptions : EncoderOptions
{
    /// <summary>The sample rate.</summary>
    public required int SampleRate { get; init; }

    /// <summary>The input sample format; see <see cref="Codec.SampleFormats"/>.</summary>
    public required SampleFormat SampleFormat { get; init; }

    /// <summary>The channel layout.</summary>
    public required ChannelLayout ChannelLayout { get; init; }

    /// <summary>The time base of the input timestamps; null for 1/<see cref="SampleRate"/>.</summary>
    public Rational? TimeBase { get; init; }
}

/// <summary>Turns frames into packets.</summary>
public sealed unsafe class Encoder : CodecContext
{
    // Whether a frame was ever accepted, and whether end of stream came before one was.
    private bool _fed;
    private bool _endedUnfed;

    private Encoder(Codec codec)
        : base(codec) { }

    /// <summary>
    /// The number of samples per channel every audio frame but the last must have, or 0 when the
    /// encoder accepts any.
    /// </summary>
    public int FrameSize => NativePointer->frame_size;

    /// <summary>The current target bit rate in bits per second.</summary>
    public long BitRate => NativePointer->bit_rate;

    /// <summary>
    /// Whether <see cref="SetRateControl"/> takes effect on this encoder; see
    /// <see cref="Codec.SupportsRateControlChanges"/>.
    /// </summary>
    public bool SupportsRateControlChanges => Codec.SupportsRateControlChanges;

    /// <summary>
    /// Changes the rate control for the frames sent from now on, as congestion control needs: FFmpeg's
    /// encoders that support it compare these settings before each frame and reconfigure.
    /// </summary>
    /// <param name="bitRate">The target bit rate in bits per second.</param>
    /// <param name="maxRate">The peak bit rate, or null to leave it.</param>
    /// <param name="bufferSize">The rate control buffer size in bits, or null to leave it.</param>
    /// <exception cref="NotSupportedException">
    /// The encoder reads its rate control only when opened (<see cref="SupportsRateControlChanges"/>);
    /// changing the fields would silently do nothing.
    /// </exception>
    public void SetRateControl(long bitRate, long? maxRate = null, int? bufferSize = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitRate);
        if (!SupportsRateControlChanges)
        {
            throw new NotSupportedException(
                $"{Codec.Name} applies rate control only when opened; open a new encoder at a key frame instead."
            );
        }

        AVCodecContext* context = NativePointer;
        context->bit_rate = bitRate;
        if (maxRate is { } peak)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(peak, bitRate, nameof(maxRate));
            context->rc_max_rate = peak;
        }

        if (bufferSize is { } size)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size, nameof(bufferSize));
            context->rc_buffer_size = size;
        }
    }

    /// <summary>Opens a video encoder.</summary>
    /// <param name="codec">A video encoder implementation.</param>
    /// <param name="options">The options.</param>
    /// <returns>The encoder.</returns>
    public static Encoder Create(Codec codec, VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(
            codec,
            MediaType.Video,
            options,
            context =>
            {
                context->width = options.Width;
                context->height = options.Height;
                context->pix_fmt = options.PixelFormat;
                context->time_base = options.TimeBase;
                if (options.FrameRate is { } rate)
                {
                    context->framerate = rate;
                }

                if (options.GopSize is { } gop)
                {
                    context->gop_size = gop;
                }

                if (options.MaxBFrames is { } bFrames)
                {
                    context->max_b_frames = bFrames;
                }

                if (options.ColorRange is { } range)
                {
                    context->color_range = (AVColorRange)range;
                }

                if (options.ColorPrimaries is { } primaries)
                {
                    context->color_primaries = (AVColorPrimaries)primaries;
                }

                if (options.ColorTransfer is { } transfer)
                {
                    context->color_trc = (AVColorTransferCharacteristic)transfer;
                }

                if (options.ColorSpace is { } space)
                {
                    context->colorspace = (AVColorSpace)space;
                }

                if (options.HardwareFrames is { } pool)
                {
                    context->hw_frames_ctx = pool.NewReference();
                }

                if (options.HardwareDevice is { } device)
                {
                    context->hw_device_ctx = device.NewReference();
                }
            }
        );
    }

    /// <summary>Opens an audio encoder.</summary>
    /// <param name="codec">An audio encoder implementation.</param>
    /// <param name="options">The options.</param>
    /// <returns>The encoder.</returns>
    public static Encoder Create(Codec codec, AudioEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(
            codec,
            MediaType.Audio,
            options,
            context =>
            {
                context->sample_rate = options.SampleRate;
                context->sample_fmt = options.SampleFormat;
                LibAVUtil.av_channel_layout_uninit(&context->ch_layout);
                context->ch_layout = options.ChannelLayout.ToNative();
                context->time_base = options.TimeBase ?? new Rational(1, options.SampleRate);
            }
        );
    }

    /// <summary>Sends a frame to encode.</summary>
    /// <param name="frame">The frame, with a timestamp in the encoder's <see cref="CodecContext.TimeBase"/>.</param>
    /// <returns>
    /// False when the encoder will not take input until its output is received: call
    /// <see cref="Receive"/> until it returns <see cref="CodecStatus.NeedsInput"/>, then send again.
    /// </returns>
    public bool TrySend(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Send(frame.NativePointer);
    }

    /// <summary>Signals end of stream, so the encoder outputs the packets it still holds.</summary>
    public void SendEndOfStream() => _ = Send(null);

    /// <summary>Receives the next encoded packet, if there is one.</summary>
    /// <param name="packet">The packet to fill; its previous content is released.</param>
    /// <returns>Whether a packet was produced, more input is needed, or the stream is finished.</returns>
    public CodecStatus Receive(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_endedUnfed)
        {
            return CodecStatus.EndOfStream;
        }

        AVCodecContext* context = NativePointer;
        CodecStatus status = Status(
            avcodec_receive_packet(context, packet.NativePointer),
            "avcodec_receive_packet"
        );
        if (status == CodecStatus.Available)
        {
            packet.NativePointer->time_base = context->time_base;
        }

        return status;
    }

    /// <summary>
    /// Sends a frame and enumerates the packets that become available, reusing <paramref name="packet"/>
    /// for each. Pass null at end of stream to drain the remaining packets.
    /// </summary>
    /// <param name="frame">The frame, or null for end of stream.</param>
    /// <param name="packet">The packet each result is written into; valid until the next iteration.</param>
    /// <returns>An allocation-free enumerable of the encoded packets.</returns>
    public EncodeEnumerable Encode(Frame? frame, Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return new(this, frame, packet);
    }

    private static Encoder Create(
        Codec codec,
        MediaType type,
        EncoderOptions options,
        ConfigureContext configure
    )
    {
        if (!codec.IsEncoder || codec.MediaType != type)
        {
            throw new ArgumentException(
                $"{codec} is not a {type.ToString().ToLowerInvariant()} encoder.",
                nameof(codec)
            );
        }

        Encoder encoder = new(codec);
        try
        {
            AVCodecContext* context = encoder.NativePointer;
            configure(context);
            context->thread_count = options.ThreadCount;
            if (options.BitRate is { } bitRate)
            {
                context->bit_rate = bitRate;
            }

            if (options.MaxRate is { } maxRate)
            {
                context->rc_max_rate = maxRate;
            }

            if (options.BufferSize is { } bufferSize)
            {
                context->rc_buffer_size = bufferSize;
            }

            if (options.GlobalHeader)
            {
                context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            if (options.LowDelay)
            {
                context->flags |= AV_CODEC_FLAG_LOW_DELAY;
            }

            encoder.Open(options.CodecOptions);
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    private bool Send(AVFrame* frame)
    {
        // End of stream for an encoder that never took a frame ends here: there is nothing to drain,
        // and hardware encoders (VA-API among them) dereference state their first frame sets up.
        if (frame is null && !_fed)
        {
            _endedUnfed = true;
            return true;
        }

        int result = avcodec_send_frame(NativePointer, frame);
        if (result == LibAVUtil.AVERROR_EAGAIN)
        {
            return false;
        }

        if (frame is null && result == LibAVUtil.AVERROR_EOF)
        {
            return true;
        }

        FFmpegError.ThrowIfError(result, "avcodec_send_frame");
        _fed |= frame is not null;
        return true;
    }

    private delegate void ConfigureContext(AVCodecContext* context);

    /// <summary>The packets produced by one <see cref="Encode"/> call.</summary>
    public ref struct EncodeEnumerable
    {
        private readonly Encoder _encoder;
        private readonly Frame? _frame;
        private readonly Packet _packet;
        private bool _sent;

        internal EncodeEnumerable(Encoder encoder, Frame? frame, Packet packet)
        {
            _encoder = encoder;
            _frame = frame;
            _packet = packet;
        }

        /// <summary>The current packet.</summary>
        public readonly Packet Current => _packet;

        /// <summary>Returns this instance; the enumerable is its own enumerator.</summary>
        /// <returns>The enumerator.</returns>
        public readonly EncodeEnumerable GetEnumerator() => this;

        /// <summary>Advances to the next packet.</summary>
        /// <returns>Whether a packet is available.</returns>
        public bool MoveNext()
        {
            while (true)
            {
                if (!_sent)
                {
                    _sent = _frame is null
                        ? _encoder.Send(null)
                        : _encoder.Send(_frame.NativePointer);
                }

                switch (_encoder.Receive(_packet))
                {
                    case CodecStatus.Available:
                        return true;
                    case CodecStatus.EndOfStream:
                        return false;
                    case CodecStatus.NeedsInput when _sent:
                        return false;
                    default:
                        throw new InvalidOperationException(
                            "The encoder neither accepted input nor produced output."
                        );
                }
            }
        }
    }
}
