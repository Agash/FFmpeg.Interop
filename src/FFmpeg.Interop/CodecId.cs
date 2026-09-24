using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.AVCodecID;

namespace FFmpeg.Interop;

/// <summary>Identifies a coding format independently of any implementation of it. Wraps <see cref="AVCodecID"/>.</summary>
/// <param name="Value">The native identifier.</param>
public readonly record struct CodecId(AVCodecID Value)
{
    /// <summary>No codec.</summary>
    public static CodecId None => new(AV_CODEC_ID_NONE);

    /// <summary>H.264 / AVC.</summary>
    public static CodecId H264 => new(AV_CODEC_ID_H264);

    /// <summary>H.265 / HEVC.</summary>
    public static CodecId Hevc => new(AV_CODEC_ID_HEVC);

    /// <summary>AV1.</summary>
    public static CodecId Av1 => new(AV_CODEC_ID_AV1);

    /// <summary>VP8.</summary>
    public static CodecId Vp8 => new(AV_CODEC_ID_VP8);

    /// <summary>VP9.</summary>
    public static CodecId Vp9 => new(AV_CODEC_ID_VP9);

    /// <summary>Opus.</summary>
    public static CodecId Opus => new(AV_CODEC_ID_OPUS);

    /// <summary>AAC.</summary>
    public static CodecId Aac => new(AV_CODEC_ID_AAC);

    /// <summary>Signed 16-bit little-endian PCM.</summary>
    public static CodecId PcmS16LE => new(AV_CODEC_ID_PCM_S16LE);

    /// <summary>FFmpeg's short name for the format, for example <c>h264</c>.</summary>
    public unsafe string Name =>
        NativeString.Read(LibAVCodec.avcodec_get_name(Value)) ?? Value.ToString();

    /// <summary>Whether this is a video, audio or other kind of format.</summary>
    public MediaType MediaType => (MediaType)LibAVCodec.avcodec_get_type(Value);

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Wraps a native identifier.</summary>
    /// <param name="value">The native identifier.</param>
    public static implicit operator CodecId(AVCodecID value) => new(value);

    /// <summary>Unwraps to the native identifier.</summary>
    /// <param name="value">The identifier.</param>
    public static implicit operator AVCodecID(CodecId value) => value.Value;
}

/// <summary>The kind of data a stream or codec carries. Same values as <see cref="AVMediaType"/>.</summary>
public enum MediaType
{
    /// <summary>Not known.</summary>
    Unknown = -1,

    /// <summary>Video.</summary>
    Video = 0,

    /// <summary>Audio.</summary>
    Audio = 1,

    /// <summary>Opaque data.</summary>
    Data = 2,

    /// <summary>Subtitles.</summary>
    Subtitle = 3,

    /// <summary>Attachments.</summary>
    Attachment = 4,
}
