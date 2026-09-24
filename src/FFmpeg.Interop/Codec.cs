using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;

namespace FFmpeg.Interop;

/// <summary>
/// An encoder or decoder implementation compiled into FFmpeg (<see cref="AVCodec"/>). Codecs are
/// static descriptions; they are looked up, never created or freed.
/// </summary>
public readonly unsafe struct Codec : IEquatable<Codec>
{
    private readonly AVCodec* _codec;

    internal Codec(AVCodec* codec) => _codec = codec;

    /// <summary>The native codec, which lives as long as the library is loaded.</summary>
    public AVCodec* NativePointer =>
        _codec is not null ? _codec : throw new InvalidOperationException("The codec is default.");

    /// <summary>The short name, for example <c>libdav1d</c> or <c>h264_nvenc</c>.</summary>
    public string Name => NativeString.Read(NativePointer->name) ?? string.Empty;

    /// <summary>The descriptive name.</summary>
    public string? LongName => NativeString.Read(NativePointer->long_name);

    /// <summary>The coding format the codec implements.</summary>
    public CodecId Id => NativePointer->id;

    /// <summary>Whether the codec is for video, audio or other data.</summary>
    public MediaType MediaType => (MediaType)NativePointer->type;

    /// <summary>Whether this is an encoder.</summary>
    public bool IsEncoder => av_codec_is_encoder(NativePointer) != 0;

    /// <summary>Whether this is a decoder.</summary>
    public bool IsDecoder => av_codec_is_decoder(NativePointer) != 0;

    /// <summary>What the codec can do.</summary>
    public CodecCapabilities Capabilities => (CodecCapabilities)NativePointer->capabilities;

    /// <summary>The external library the codec wraps, for example <c>libdav1d</c>, or null for a native codec.</summary>
    public string? WrapperName => NativeString.Read(NativePointer->wrapper_name);

    /// <summary>The pixel formats an encoder accepts or a decoder produces; empty when any or unknown.</summary>
    public ReadOnlySpan<PixelFormat> PixelFormats =>
        SupportedConfig<PixelFormat>(AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT);

    /// <summary>The sample formats the codec supports; empty when any or unknown.</summary>
    public ReadOnlySpan<SampleFormat> SampleFormats =>
        SupportedConfig<SampleFormat>(AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT);

    /// <summary>The sample rates the codec supports; empty when any or unknown.</summary>
    public ReadOnlySpan<int> SampleRates =>
        SupportedConfig<int>(AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE);

    /// <summary>The frame rates the codec supports; empty when any or unknown.</summary>
    public ReadOnlySpan<Rational> FrameRates =>
        SupportedConfig<Rational>(AVCodecConfig.AV_CODEC_CONFIG_FRAME_RATE);

    /// <summary>The ways the codec can use hardware acceleration.</summary>
    public IReadOnlyList<HardwareConfig> HardwareConfigs
    {
        get
        {
            List<HardwareConfig> configs = [];
            AVCodecHWConfig* config;
            for (int i = 0; (config = avcodec_get_hw_config(NativePointer, i)) is not null; i++)
            {
                configs.Add(
                    new(
                        config->pix_fmt,
                        config->device_type,
                        (HardwareConfigMethods)config->methods
                    )
                );
            }

            return configs;
        }
    }

    /// <summary>Every codec compiled into the loaded FFmpeg.</summary>
    public static IEnumerable<Codec> All
    {
        get
        {
            nint state = 0;
            while (Next(ref state) is { } codec)
            {
                yield return codec;
            }
        }
    }

    /// <summary>Finds the preferred decoder for a coding format.</summary>
    /// <param name="id">The format.</param>
    /// <param name="codec">The decoder.</param>
    /// <returns>Whether one exists.</returns>
    public static bool TryFindDecoder(CodecId id, out Codec codec) =>
        Found(avcodec_find_decoder(id), out codec);

    /// <summary>Finds a decoder by name.</summary>
    /// <param name="name">The name, for example <c>libdav1d</c>.</param>
    /// <param name="codec">The decoder.</param>
    /// <returns>Whether one exists.</returns>
    public static bool TryFindDecoder(string name, out Codec codec)
    {
        ArgumentNullException.ThrowIfNull(name);
        using Utf8String native = new(name);
        return Found(avcodec_find_decoder_by_name(native), out codec);
    }

    /// <summary>
    /// Finds a decoder for a coding format that can decode on a kind of hardware device. The preferred
    /// decoder is not always one: FFmpeg prefers libdav1d for AV1, which decodes in software only, while
    /// the hardware paths run through its native <c>av1</c> decoder.
    /// </summary>
    /// <param name="id">The format.</param>
    /// <param name="deviceType">The device type the decoder must support.</param>
    /// <param name="codec">The decoder.</param>
    /// <returns>Whether one exists.</returns>
    public static bool TryFindDecoder(CodecId id, HardwareDeviceType deviceType, out Codec codec)
    {
        if (TryFindDecoder(id, out codec) && codec.SupportsDevice(deviceType))
        {
            return true;
        }

        foreach (Codec candidate in All)
        {
            if (candidate.Id == id && candidate.IsDecoder && candidate.SupportsDevice(deviceType))
            {
                codec = candidate;
                return true;
            }
        }

        codec = default;
        return false;
    }

    /// <summary>Finds a decoder for a coding format that can decode on a kind of hardware device.</summary>
    /// <param name="id">The format.</param>
    /// <param name="deviceType">The device type the decoder must support.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="NotSupportedException">This FFmpeg build cannot decode the format on that device type.</exception>
    public static Codec FindDecoder(CodecId id, HardwareDeviceType deviceType) =>
        TryFindDecoder(id, deviceType, out Codec codec)
            ? codec
            : throw new NotSupportedException(
                $"No {id} decoder in this FFmpeg build decodes on {deviceType}."
            );

    /// <summary>Whether the codec can use a device of the given type through a device context.</summary>
    /// <param name="deviceType">The device type.</param>
    /// <returns>Whether a hardware configuration for it exists.</returns>
    public bool SupportsDevice(HardwareDeviceType deviceType)
    {
        AVCodecHWConfig* config;
        for (int i = 0; (config = avcodec_get_hw_config(NativePointer, i)) is not null; i++)
        {
            if (
                config->device_type == deviceType
                && (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the preferred encoder for a coding format.</summary>
    /// <param name="id">The format.</param>
    /// <param name="codec">The encoder.</param>
    /// <returns>Whether one exists.</returns>
    public static bool TryFindEncoder(CodecId id, out Codec codec) =>
        Found(avcodec_find_encoder(id), out codec);

    /// <summary>Finds an encoder by name.</summary>
    /// <param name="name">The name, for example <c>libsvtav1</c>.</param>
    /// <param name="codec">The encoder.</param>
    /// <returns>Whether one exists.</returns>
    public static bool TryFindEncoder(string name, out Codec codec)
    {
        ArgumentNullException.ThrowIfNull(name);
        using Utf8String native = new(name);
        return Found(avcodec_find_encoder_by_name(native), out codec);
    }

    /// <summary>Finds the preferred decoder for a coding format.</summary>
    /// <param name="id">The format.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="NotSupportedException">This FFmpeg build has no decoder for it.</exception>
    public static Codec FindDecoder(CodecId id) =>
        TryFindDecoder(id, out Codec codec)
            ? codec
            : throw new NotSupportedException($"No {id} decoder in this FFmpeg build.");

    /// <summary>Finds a decoder by name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="NotSupportedException">This FFmpeg build has no decoder of that name.</exception>
    public static Codec FindDecoder(string name) =>
        TryFindDecoder(name, out Codec codec)
            ? codec
            : throw new NotSupportedException($"No decoder '{name}' in this FFmpeg build.");

    /// <summary>Finds the preferred encoder for a coding format.</summary>
    /// <param name="id">The format.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="NotSupportedException">This FFmpeg build has no encoder for it.</exception>
    public static Codec FindEncoder(CodecId id) =>
        TryFindEncoder(id, out Codec codec)
            ? codec
            : throw new NotSupportedException($"No {id} encoder in this FFmpeg build.");

    /// <summary>Finds an encoder by name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="NotSupportedException">This FFmpeg build has no encoder of that name.</exception>
    public static Codec FindEncoder(string name) =>
        TryFindEncoder(name, out Codec codec)
            ? codec
            : throw new NotSupportedException($"No encoder '{name}' in this FFmpeg build.");

    /// <inheritdoc/>
    public bool Equals(Codec other) => _codec == other._codec;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Codec other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ((nint)_codec).GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => _codec is null ? "(none)" : Name;

    /// <summary>Whether two values are the same codec.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(Codec left, Codec right) => left.Equals(right);

    /// <summary>Whether two values are different codecs.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(Codec left, Codec right) => !left.Equals(right);

    private static Codec? Next(ref nint state)
    {
        void* opaque = (void*)state;
        AVCodec* codec = av_codec_iterate(&opaque);
        state = (nint)opaque;
        return codec is null ? null : new Codec(codec);
    }

    private static bool Found(AVCodec* native, out Codec codec)
    {
        codec = new(native);
        return native is not null;
    }

    // The configuration arrays are static tables in the codec, so they are exposed in place. Each element
    // type here has the layout of the native element: PixelFormat and SampleFormat wrap an int-sized
    // enum, Rational is two ints like AVRational.
    private ReadOnlySpan<T> SupportedConfig<T>(AVCodecConfig config)
        where T : unmanaged
    {
        void* values;
        int count;
        FFmpegError.ThrowIfError(
            avcodec_get_supported_config(null, NativePointer, config, 0, &values, &count)
        );
        return values is null ? [] : new ReadOnlySpan<T>(values, count);
    }
}

/// <summary>How a codec can use a hardware device.</summary>
/// <param name="PixelFormat">The hardware pixel format of the frames involved.</param>
/// <param name="DeviceType">The device type, or <see cref="HardwareDeviceType.None"/> when none is needed.</param>
/// <param name="Methods">How the device is supplied to the codec.</param>
public readonly record struct HardwareConfig(
    PixelFormat PixelFormat,
    HardwareDeviceType DeviceType,
    HardwareConfigMethods Methods
);

/// <summary>How a hardware device is supplied to a codec.</summary>
[Flags]
public enum HardwareConfigMethods
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Through a device context (<c>hw_device_ctx</c>).</summary>
    DeviceContext = LibAVCodec.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX,

    /// <summary>Through a frames context (<c>hw_frames_ctx</c>).</summary>
    FramesContext = LibAVCodec.AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX,

    /// <summary>The codec sets the device up itself.</summary>
    Internal = LibAVCodec.AV_CODEC_HW_CONFIG_METHOD_INTERNAL,

    /// <summary>A codec-specific mechanism.</summary>
    AdHoc = LibAVCodec.AV_CODEC_HW_CONFIG_METHOD_AD_HOC,
}

/// <summary>What a codec can do (<c>AV_CODEC_CAP_*</c>).</summary>
[Flags]
public enum CodecCapabilities
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The codec buffers frames and must be drained at end of stream.</summary>
    Delay = LibAVCodec.AV_CODEC_CAP_DELAY,

    /// <summary>The encoder accepts a smaller last audio frame.</summary>
    SmallLastFrame = LibAVCodec.AV_CODEC_CAP_SMALL_LAST_FRAME,

    /// <summary>Experimental: needs <c>strict=-2</c> to use.</summary>
    Experimental = LibAVCodec.AV_CODEC_CAP_EXPERIMENTAL,

    /// <summary>Frame-level threading.</summary>
    FrameThreads = LibAVCodec.AV_CODEC_CAP_FRAME_THREADS,

    /// <summary>Slice-level threading.</summary>
    SliceThreads = LibAVCodec.AV_CODEC_CAP_SLICE_THREADS,

    /// <summary>Threading by other means (for example a wrapped library's own).</summary>
    OtherThreads = LibAVCodec.AV_CODEC_CAP_OTHER_THREADS,

    /// <summary>Audio encoder accepting any frame size.</summary>
    VariableFrameSize = LibAVCodec.AV_CODEC_CAP_VARIABLE_FRAME_SIZE,

    /// <summary>Backed by dedicated hardware.</summary>
    Hardware = LibAVCodec.AV_CODEC_CAP_HARDWARE,

    /// <summary>Possibly backed by hardware, with a software fallback.</summary>
    Hybrid = LibAVCodec.AV_CODEC_CAP_HYBRID,

    /// <summary>The encoder can be flushed and reused.</summary>
    EncoderFlush = LibAVCodec.AV_CODEC_CAP_ENCODER_FLUSH,
}

internal static unsafe class NativeOptions
{
    public static AVDictionary* Create(IReadOnlyDictionary<string, string>? options)
    {
        AVDictionary* dictionary = null;
        if (options is null)
        {
            return null;
        }

        foreach ((string key, string value) in options)
        {
            using Utf8String nativeKey = new(key);
            using Utf8String nativeValue = new(value);
            int result = LibAVUtil.av_dict_set(&dictionary, nativeKey, nativeValue, 0);
            if (result < 0)
            {
                LibAVUtil.av_dict_free(&dictionary);
                FFmpegError.Throw(result, "av_dict_set");
            }
        }

        return dictionary;
    }

    // FFmpeg removes each option it recognises and leaves the rest. Unrecognised options are an error:
    // a misspelled option name silently changes nothing, which is the failure worth preventing.
    public static void ThrowIfUnused(AVDictionary* remaining, string component)
    {
        if (LibAVUtil.av_dict_count(remaining) == 0)
        {
            return;
        }

        List<string> unused = [];
        for (
            AVDictionaryEntry* entry = null;
            (entry = LibAVUtil.av_dict_iterate(remaining, entry)) is not null;

        )
        {
            unused.Add(NativeString.Read(entry->key) ?? string.Empty);
        }

        throw new ArgumentException(
            $"{component} does not recognise the option(s): {string.Join(", ", unused)}."
        );
    }
}
