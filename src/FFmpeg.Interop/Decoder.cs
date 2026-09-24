using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;

namespace FFmpeg.Interop;

/// <summary>Options for opening a <see cref="Decoder"/>.</summary>
public sealed record DecoderOptions
{
    /// <summary>
    /// Decode on this device. The decoder outputs hardware frames in the device's format; use
    /// <see cref="Frame.TransferTo"/> or <see cref="Frame.MapTo"/> to get at the pixels.
    /// </summary>
    public HardwareDevice? HardwareDevice { get; init; }

    /// <summary>
    /// When hardware decoding is requested but the stream cannot be decoded on the device, decode in
    /// software instead of failing. Off by default so a missing hardware path is noticed.
    /// </summary>
    public bool AllowSoftwareFallback { get; init; }

    /// <summary>Decoding threads; 0 lets FFmpeg choose.</summary>
    public int ThreadCount { get; init; }

    /// <summary>
    /// Codec setup data (<see cref="CodecContext.ExtraData"/>), for streams that carry it out of band,
    /// such as H.264 from an MP4 or Opus with a header.
    /// </summary>
    public ReadOnlyMemory<byte> ExtraData { get; init; }

    /// <summary>The time base of the packets' timestamps, which the decoder carries into the frames.</summary>
    public Rational? PacketTimeBase { get; init; }

    /// <summary>The sample rate, for audio decoders fed a raw stream with no container to state it.</summary>
    public int? SampleRate { get; init; }

    /// <summary>The channel layout, for audio decoders fed a raw stream with no container to state it.</summary>
    public ChannelLayout? ChannelLayout { get; init; }

    /// <summary>Codec-private options, as <c>ffmpeg -c:v name -option value</c> takes them. Unknown names are an error.</summary>
    public IReadOnlyDictionary<string, string>? CodecOptions { get; init; }
}

/// <summary>Turns packets into frames.</summary>
/// <example>
/// <code>
/// using Decoder decoder = Decoder.Create(Codec.FindDecoder(CodecId.Av1));
/// using Frame frame = new();
/// foreach (Frame decoded in decoder.Decode(packet, frame)) { /* use decoded */ }
/// foreach (Frame decoded in decoder.Decode(null, frame)) { /* drain at end of stream */ }
/// </code>
/// </example>
public sealed unsafe class Decoder : CodecContext
{
    // The hardware pixel format the get_format callback picks, and whether it may fall back to
    // software, are packed into the context's opaque pointer: the callback is static and must not
    // reach managed state through a handle it would have to look up on every stream change.
    internal const long FallbackFlag = 1L << 32;

    private Decoder(Codec codec)
        : base(codec) { }

    /// <summary>Opens a decoder.</summary>
    /// <param name="codec">A decoder implementation.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <returns>The decoder.</returns>
    public static Decoder Create(Codec codec, DecoderOptions? options = null) =>
        Create(codec, null, options);

    internal static Decoder Create(
        Codec codec,
        AVCodecParameters* parameters,
        DecoderOptions? options
    )
    {
        if (!codec.IsDecoder)
        {
            throw new ArgumentException($"{codec} is not a decoder.", nameof(codec));
        }

        options ??= new();
        Decoder decoder = new(codec);
        try
        {
            AVCodecContext* context = decoder.NativePointer;
            if (parameters is not null)
            {
                FFmpegError.ThrowIfError(avcodec_parameters_to_context(context, parameters));
            }

            decoder.SetExtraData(options.ExtraData.Span);
            context->thread_count = options.ThreadCount;
            if (options.PacketTimeBase is { } timeBase)
            {
                context->pkt_timebase = timeBase;
            }

            if (options.SampleRate is { } sampleRate)
            {
                context->sample_rate = sampleRate;
            }

            if (options.ChannelLayout is { } layout)
            {
                LibAVUtil.av_channel_layout_uninit(&context->ch_layout);
                context->ch_layout = layout.ToNative();
            }

            if (options.HardwareDevice is { } device)
            {
                PixelFormat format = HardwareFormat(codec, device.Type);
                context->hw_device_ctx = device.NewReference();
                context->opaque = (void*)
                    (nint)(
                        (long)(int)format.Value | (options.AllowSoftwareFallback ? FallbackFlag : 0)
                    );
                context->get_format = &SelectFormat;
            }

            decoder.Open(options.CodecOptions);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>Sends a packet to decode.</summary>
    /// <param name="packet">The packet.</param>
    /// <returns>
    /// False when the decoder will not take input until its output is received: call
    /// <see cref="Receive"/> until it returns <see cref="CodecStatus.NeedsInput"/>, then send again.
    /// </returns>
    public bool TrySend(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return Send(packet.NativePointer);
    }

    /// <summary>Signals end of stream, so the decoder outputs the frames it still holds.</summary>
    public void SendEndOfStream() => _ = Send(null);

    /// <summary>Receives the next decoded frame, if there is one.</summary>
    /// <param name="frame">The frame to fill; its previous content is released.</param>
    /// <returns>Whether a frame was produced, more input is needed, or the stream is finished.</returns>
    public CodecStatus Receive(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Status(
            avcodec_receive_frame(NativePointer, frame.NativePointer),
            "avcodec_receive_frame"
        );
    }

    /// <summary>
    /// Sends a packet and enumerates the frames that become available, reusing <paramref name="frame"/>
    /// for each. Pass null at end of stream to drain the remaining frames.
    /// </summary>
    /// <param name="packet">The packet, or null for end of stream.</param>
    /// <param name="frame">The frame each result is written into; valid until the next iteration.</param>
    /// <returns>An allocation-free enumerable of the decoded frames.</returns>
    public DecodeEnumerable Decode(Packet? packet, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new(this, packet, frame);
    }

    private bool Send(AVPacket* packet)
    {
        int result = avcodec_send_packet(NativePointer, packet);
        if (result == LibAVUtil.AVERROR_EAGAIN)
        {
            return false;
        }

        // Signalling end of stream twice is harmless.
        if (packet is null && result == LibAVUtil.AVERROR_EOF)
        {
            return true;
        }

        FFmpegError.ThrowIfError(result, "avcodec_send_packet");
        return true;
    }

    private static PixelFormat HardwareFormat(Codec codec, HardwareDeviceType type)
    {
        foreach (HardwareConfig config in codec.HardwareConfigs)
        {
            if (
                config.DeviceType == type
                && config.Methods.HasFlag(HardwareConfigMethods.DeviceContext)
            )
            {
                return config.PixelFormat;
            }
        }

        throw new NotSupportedException($"{codec} cannot decode on a {type} device.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static AVPixelFormat SelectFormat(AVCodecContext* context, AVPixelFormat* offered)
    {
        long state = (nint)context->opaque;
        AVPixelFormat wanted = (AVPixelFormat)(int)state;
        AVPixelFormat software = AVPixelFormat.AV_PIX_FMT_NONE;
        for (AVPixelFormat* format = offered; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == wanted)
            {
                return wanted != AVPixelFormat.AV_PIX_FMT_D3D12 || AlignD3D12Surfaces(context)
                    ? wanted
                    : AVPixelFormat.AV_PIX_FMT_NONE;
            }

            if (software == AVPixelFormat.AV_PIX_FMT_NONE && !((PixelFormat)(*format)).IsHardware)
            {
                software = *format;
            }
        }

        // Returning NONE makes the decoder fail the stream, which is the point when no fallback is allowed.
        return (state & FallbackFlag) != 0 ? software : AVPixelFormat.AV_PIX_FMT_NONE;
    }

    // FFmpeg 9 sizes D3D12 decode surfaces to the display size, where its D3D11 and DXVA2 paths use the
    // coded size aligned as the DXVA specification asks (128 for HEVC and AV1). A stream whose coded
    // size exceeds its display size, such as 720p HEVC from NVENC coded as 1280x736, then fails every
    // picture. The frames context is built here instead, at the aligned coded size, which is the
    // documented way for an application to supply its own.
    private static bool AlignD3D12Surfaces(AVCodecContext* context)
    {
        if (context->hw_frames_ctx is not null)
        {
            LibAVUtil.av_buffer_unref(&context->hw_frames_ctx);
        }

        AVBufferRef* frames = null;
        if (
            avcodec_get_hw_frames_parameters(
                context,
                context->hw_device_ctx,
                AVPixelFormat.AV_PIX_FMT_D3D12,
                &frames
            ) < 0
        )
        {
            return false;
        }

        int alignment = context->codec_id is AVCodecID.AV_CODEC_ID_HEVC or AVCodecID.AV_CODEC_ID_AV1
            ? 128
            : 16;
        AVHWFramesContext* parameters = (AVHWFramesContext*)frames->data;
        parameters->width = Align(Math.Max(context->coded_width, context->width), alignment);
        parameters->height = Align(Math.Max(context->coded_height, context->height), alignment);
        if (LibAVUtil.av_hwframe_ctx_init(frames) < 0)
        {
            LibAVUtil.av_buffer_unref(&frames);
            return false;
        }

        context->hw_frames_ctx = frames;
        return true;

        static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>The frames produced by one <see cref="Decode"/> call.</summary>
    public ref struct DecodeEnumerable
    {
        private readonly Decoder _decoder;
        private readonly Packet? _packet;
        private readonly Frame _frame;
        private bool _sent;

        internal DecodeEnumerable(Decoder decoder, Packet? packet, Frame frame)
        {
            _decoder = decoder;
            _packet = packet;
            _frame = frame;
        }

        /// <summary>The current frame.</summary>
        public readonly Frame Current => _frame;

        /// <summary>Returns this instance; the enumerable is its own enumerator.</summary>
        /// <returns>The enumerator.</returns>
        public readonly DecodeEnumerable GetEnumerator() => this;

        /// <summary>Advances to the next frame.</summary>
        /// <returns>Whether a frame is available.</returns>
        public bool MoveNext()
        {
            while (true)
            {
                if (!_sent)
                {
                    _sent = _packet is null
                        ? _decoder.Send(null)
                        : _decoder.Send(_packet.NativePointer);
                }

                switch (_decoder.Receive(_frame))
                {
                    case CodecStatus.Available:
                        return true;
                    case CodecStatus.EndOfStream:
                        return false;
                    case CodecStatus.NeedsInput when _sent:
                        return false;
                    default:
                        // The decoder refused the packet because output was pending, yet has none to
                        // give: FFmpeg's send/receive contract rules this out.
                        throw new InvalidOperationException(
                            "The decoder neither accepted input nor produced output."
                        );
                }
            }
        }
    }
}
