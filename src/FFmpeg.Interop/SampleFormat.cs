using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.AVSampleFormat;

namespace FFmpeg.Interop;

/// <summary>An FFmpeg audio sample format. Wraps <see cref="AVSampleFormat"/>.</summary>
/// <param name="Value">The native format.</param>
public readonly record struct SampleFormat(AVSampleFormat Value)
{
    /// <summary>No format.</summary>
    public static SampleFormat None => new(AV_SAMPLE_FMT_NONE);

    /// <summary>Unsigned 8-bit, interleaved.</summary>
    public static SampleFormat U8 => new(AV_SAMPLE_FMT_U8);

    /// <summary>Signed 16-bit, interleaved.</summary>
    public static SampleFormat S16 => new(AV_SAMPLE_FMT_S16);

    /// <summary>Signed 32-bit, interleaved.</summary>
    public static SampleFormat S32 => new(AV_SAMPLE_FMT_S32);

    /// <summary>32-bit float, interleaved.</summary>
    public static SampleFormat Float => new(AV_SAMPLE_FMT_FLT);

    /// <summary>64-bit float, interleaved.</summary>
    public static SampleFormat Double => new(AV_SAMPLE_FMT_DBL);

    /// <summary>Signed 16-bit, one plane per channel.</summary>
    public static SampleFormat S16Planar => new(AV_SAMPLE_FMT_S16P);

    /// <summary>Signed 32-bit, one plane per channel.</summary>
    public static SampleFormat S32Planar => new(AV_SAMPLE_FMT_S32P);

    /// <summary>32-bit float, one plane per channel.</summary>
    public static SampleFormat FloatPlanar => new(AV_SAMPLE_FMT_FLTP);

    /// <summary>64-bit float, one plane per channel.</summary>
    public static SampleFormat DoublePlanar => new(AV_SAMPLE_FMT_DBLP);

    /// <summary>FFmpeg's name for the format, or null for <see cref="None"/>.</summary>
    public unsafe string? Name => NativeString.Read(LibAVUtil.av_get_sample_fmt_name(Value));

    /// <summary>The size of one sample of one channel, in bytes; 0 for <see cref="None"/>.</summary>
    public int BytesPerSample => Math.Max(0, LibAVUtil.av_get_bytes_per_sample(Value));

    /// <summary>Whether each channel has its own plane.</summary>
    public bool IsPlanar => LibAVUtil.av_sample_fmt_is_planar(Value) != 0;

    /// <summary>The interleaved format with the same sample type.</summary>
    public SampleFormat ToInterleaved() => new(LibAVUtil.av_get_packed_sample_fmt(Value));

    /// <summary>The planar format with the same sample type.</summary>
    public SampleFormat ToPlanar() => new(LibAVUtil.av_get_planar_sample_fmt(Value));

    /// <summary>Finds a format by FFmpeg's name for it, for example <c>s16</c>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="format">The format, or <see cref="None"/>.</param>
    /// <returns>Whether the name is known.</returns>
    public static unsafe bool TryParse(string name, out SampleFormat format)
    {
        ArgumentNullException.ThrowIfNull(name);
        using Utf8String native = new(name);
        format = new(LibAVUtil.av_get_sample_fmt(native));
        return format != None;
    }

    /// <inheritdoc/>
    public override string ToString() => Name ?? Value.ToString();

    /// <summary>Wraps a native format.</summary>
    /// <param name="value">The native format.</param>
    public static implicit operator SampleFormat(AVSampleFormat value) => new(value);

    /// <summary>Unwraps to the native format.</summary>
    /// <param name="value">The format.</param>
    public static implicit operator AVSampleFormat(SampleFormat value) => value.Value;
}
