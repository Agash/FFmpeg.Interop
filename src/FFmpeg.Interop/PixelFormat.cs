using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.AVPixelFormat;

namespace FFmpeg.Interop;

/// <summary>
/// An FFmpeg pixel format. Wraps <see cref="AVPixelFormat"/> with the facts FFmpeg's pixel format
/// descriptor holds about it; the well-known formats are named, any other converts from the native value.
/// </summary>
/// <param name="Value">The native format.</param>
public readonly unsafe record struct PixelFormat(AVPixelFormat Value)
{
    /// <summary>No format (<c>AV_PIX_FMT_NONE</c>).</summary>
    public static PixelFormat None => new(AV_PIX_FMT_NONE);

    /// <summary>Planar 8-bit 4:2:0 YUV.</summary>
    public static PixelFormat Yuv420P => new(AV_PIX_FMT_YUV420P);

    /// <summary>Planar 10-bit 4:2:0 YUV, little-endian.</summary>
    public static PixelFormat Yuv420P10 => new(AV_PIX_FMT_YUV420P10LE);

    /// <summary>Semi-planar 8-bit 4:2:0 YUV: a Y plane and an interleaved UV plane.</summary>
    public static PixelFormat Nv12 => new(AV_PIX_FMT_NV12);

    /// <summary>Semi-planar 10-bit 4:2:0 YUV in 16-bit words, little-endian.</summary>
    public static PixelFormat P010 => new(AV_PIX_FMT_P010LE);

    /// <summary>Packed 8-bit RGB.</summary>
    public static PixelFormat Rgb24 => new(AV_PIX_FMT_RGB24);

    /// <summary>Packed 8-bit BGRA.</summary>
    public static PixelFormat Bgra => new(AV_PIX_FMT_BGRA);

    /// <summary>Packed 8-bit RGBA.</summary>
    public static PixelFormat Rgba => new(AV_PIX_FMT_RGBA);

    /// <summary>Direct3D 11 textures.</summary>
    public static PixelFormat D3D11 => new(AV_PIX_FMT_D3D11);

    /// <summary>Direct3D 12 resources.</summary>
    public static PixelFormat D3D12 => new(AV_PIX_FMT_D3D12);

    /// <summary>AMD AMF surfaces.</summary>
    public static PixelFormat AmfSurface => new(AV_PIX_FMT_AMF_SURFACE);

    /// <summary>VA-API surfaces.</summary>
    public static PixelFormat Vaapi => new(AV_PIX_FMT_VAAPI);

    /// <summary>VideoToolbox <c>CVPixelBuffer</c>s.</summary>
    public static PixelFormat VideoToolbox => new(AV_PIX_FMT_VIDEOTOOLBOX);

    /// <summary>Vulkan images.</summary>
    public static PixelFormat Vulkan => new(AV_PIX_FMT_VULKAN);

    /// <summary>CUDA device memory.</summary>
    public static PixelFormat Cuda => new(AV_PIX_FMT_CUDA);

    /// <summary>DRM PRIME buffers (DMA-BUF descriptors).</summary>
    public static PixelFormat DrmPrime => new(AV_PIX_FMT_DRM_PRIME);

    /// <summary>FFmpeg's name for the format, or null for <see cref="None"/> and unknown values.</summary>
    public string? Name => NativeString.Read(LibAVUtil.av_get_pix_fmt_name(Value));

    /// <summary>Whether frames of this format live in hardware memory rather than system memory.</summary>
    public bool IsHardware => HasFlag(LibAVUtil.AV_PIX_FMT_FLAG_HWACCEL);

    /// <summary>Whether the format stores its components in separate planes.</summary>
    public bool IsPlanar => HasFlag(LibAVUtil.AV_PIX_FMT_FLAG_PLANAR);

    /// <summary>Whether the format is an RGB-like format rather than YUV.</summary>
    public bool IsRgb => HasFlag(LibAVUtil.AV_PIX_FMT_FLAG_RGB);

    /// <summary>Whether the format has an alpha channel.</summary>
    public bool HasAlpha => HasFlag(LibAVUtil.AV_PIX_FMT_FLAG_ALPHA);

    /// <summary>The number of planes a frame of this format has, or 0 for <see cref="None"/>.</summary>
    public int PlaneCount => Math.Max(0, LibAVUtil.av_pix_fmt_count_planes(Value));

    /// <summary>The horizontal chroma subsampling as a shift: 1 for 4:2:x, 0 for 4:4:4.</summary>
    public int ChromaShiftX => Descriptor is null ? 0 : Descriptor->log2_chroma_w;

    /// <summary>The vertical chroma subsampling as a shift: 1 for 4:2:0, 0 for 4:2:2 and 4:4:4.</summary>
    public int ChromaShiftY => Descriptor is null ? 0 : Descriptor->log2_chroma_h;

    /// <summary>The native descriptor, or null for <see cref="None"/> and unknown values.</summary>
    public AVPixFmtDescriptor* Descriptor => LibAVUtil.av_pix_fmt_desc_get(Value);

    /// <summary>Finds a format by FFmpeg's name for it, for example <c>yuv420p</c>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="format">The format, or <see cref="None"/>.</param>
    /// <returns>Whether the name is known.</returns>
    public static bool TryParse(string name, out PixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(name);
        using Utf8String native = new(name);
        format = new(LibAVUtil.av_get_pix_fmt(native));
        return format != None;
    }

    /// <summary>Finds a format by FFmpeg's name for it, for example <c>yuv420p</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The format.</returns>
    /// <exception cref="ArgumentException">FFmpeg has no format of that name.</exception>
    public static PixelFormat Parse(string name) =>
        TryParse(name, out PixelFormat format)
            ? format
            : throw new ArgumentException($"Unknown pixel format '{name}'.", nameof(name));

    // The height of one plane: the chroma planes (1 and 2) are subsampled, luma and alpha are not.
    internal int PlaneHeight(int plane, int height) =>
        plane is 1 or 2 ? -(-height >> ChromaShiftY) : height;

    /// <inheritdoc/>
    public override string ToString() => Name ?? Value.ToString();

    /// <summary>Wraps a native format.</summary>
    /// <param name="value">The native format.</param>
    public static implicit operator PixelFormat(AVPixelFormat value) => new(value);

    /// <summary>Unwraps to the native format.</summary>
    /// <param name="value">The format.</param>
    public static implicit operator AVPixelFormat(PixelFormat value) => value.Value;

    private bool HasFlag(int flag) =>
        Descriptor is not null && (Descriptor->flags & (ulong)flag) != 0;
}
