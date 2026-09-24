using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.AVChannelOrder;

namespace FFmpeg.Interop;

/// <summary>
/// An audio channel layout: how many channels there are and, for the native order, which speaker
/// each one is.
/// </summary>
/// <remarks>
/// Only the unspecified and native orders are represented. Custom and ambisonic layouts own a heap
/// allocated channel map; code that needs them uses <see cref="AVChannelLayout"/> directly.
/// </remarks>
public readonly unsafe record struct ChannelLayout
{
    private ChannelLayout(AVChannelOrder order, int channelCount, ulong mask)
    {
        IsNativeOrder = order == AV_CHANNEL_ORDER_NATIVE;
        ChannelCount = channelCount;
        Mask = mask;
    }

    /// <summary>One channel.</summary>
    public static ChannelLayout Mono => Default(1);

    /// <summary>Front left and right.</summary>
    public static ChannelLayout Stereo => Default(2);

    /// <summary>The number of channels.</summary>
    public int ChannelCount { get; }

    /// <summary>
    /// Whether the channels are identified by <see cref="Mask"/>; when false only the count is known.
    /// </summary>
    public bool IsNativeOrder { get; }

    /// <summary>The <c>AV_CH_*</c> bits of the channels present, for the native order; otherwise 0.</summary>
    public ulong Mask { get; }

    /// <summary>FFmpeg's default layout for a channel count, for example stereo for 2.</summary>
    /// <param name="channelCount">The number of channels.</param>
    /// <returns>The layout.</returns>
    public static ChannelLayout Default(int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        AVChannelLayout native = default;
        LibAVUtil.av_channel_layout_default(&native, channelCount);
        return FromNative(&native);
    }

    /// <summary>A native-order layout from its <c>AV_CH_*</c> bits.</summary>
    /// <param name="mask">The channel bits.</param>
    /// <returns>The layout.</returns>
    public static ChannelLayout FromMask(ulong mask)
    {
        AVChannelLayout native = default;
        FFmpegError.ThrowIfError(LibAVUtil.av_channel_layout_from_mask(&native, mask));
        return FromNative(&native);
    }

    /// <summary>FFmpeg's description of the layout, for example <c>stereo</c> or <c>5.1</c>.</summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        AVChannelLayout native = ToNative();
        sbyte* buffer = stackalloc sbyte[64];
        return LibAVUtil.av_channel_layout_describe(&native, buffer, 64) < 0
            ? $"{ChannelCount} channels"
            : NativeString.Read(buffer) ?? string.Empty;
    }

    internal static ChannelLayout FromNative(AVChannelLayout* native) =>
        native->order is AV_CHANNEL_ORDER_NATIVE or AV_CHANNEL_ORDER_UNSPEC
            ? new(
                native->order,
                native->nb_channels,
                native->order == AV_CHANNEL_ORDER_NATIVE ? native->u.mask : 0
            )
            : throw new NotSupportedException(
                $"Channel order {native->order} is not represented by ChannelLayout."
            );

    internal AVChannelLayout ToNative()
    {
        AVChannelLayout native = default;
        native.order = IsNativeOrder ? AV_CHANNEL_ORDER_NATIVE : AV_CHANNEL_ORDER_UNSPEC;
        native.nb_channels = ChannelCount;
        native.u.mask = Mask;
        return native;
    }
}
