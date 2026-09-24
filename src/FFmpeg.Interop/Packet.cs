using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>A unit of compressed data: one encoded frame, or part of one (<see cref="AVPacket"/>).</summary>
/// <remarks>
/// Like <see cref="Frame"/>, a reusable container: encoders and readers fill one the caller owns. The
/// data is reference counted by FFmpeg. <see cref="Data"/> is valid until the packet is next filled,
/// reset or disposed; <see cref="LeaseData"/> keeps it alive independently. Not thread-safe.
/// </remarks>
public sealed unsafe class Packet : IDisposable
{
    /// <summary>
    /// The number of zero bytes that must follow packet data a decoder reads
    /// (<c>AV_INPUT_BUFFER_PADDING_SIZE</c>): bitstream readers read ahead in whole words.
    /// </summary>
    public const int PaddingSize = AV_INPUT_BUFFER_PADDING_SIZE;

    private readonly SafePacketHandle _handle;

    /// <summary>Creates an empty packet.</summary>
    public Packet() => _handle = SafePacketHandle.Allocate();

    /// <summary>The native packet, owned by this instance and invalid after <see cref="Dispose"/>.</summary>
    public AVPacket* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Pointer;
        }
    }

    /// <summary>The compressed data.</summary>
    public ReadOnlySpan<byte> Data
    {
        get
        {
            AVPacket* packet = NativePointer;
            return new(packet->data, packet->size);
        }
    }

    /// <summary>The size of the data in bytes.</summary>
    public int Size => NativePointer->size;

    /// <summary>The presentation timestamp in <see cref="TimeBase"/> units, or null when unset.</summary>
    public long? PresentationTimestamp
    {
        get => NativePointer->pts == AV_NOPTS_VALUE ? null : NativePointer->pts;
        set => NativePointer->pts = value ?? AV_NOPTS_VALUE;
    }

    /// <summary>The decode timestamp in <see cref="TimeBase"/> units, or null when unset.</summary>
    public long? DecodeTimestamp
    {
        get => NativePointer->dts == AV_NOPTS_VALUE ? null : NativePointer->dts;
        set => NativePointer->dts = value ?? AV_NOPTS_VALUE;
    }

    /// <summary>The duration in <see cref="TimeBase"/> units, or 0 when unknown.</summary>
    public long Duration
    {
        get => NativePointer->duration;
        set => NativePointer->duration = value;
    }

    /// <summary>The time base of the timestamps and duration.</summary>
    public Rational TimeBase
    {
        get => NativePointer->time_base;
        set => NativePointer->time_base = value;
    }

    /// <summary>The index of the stream the packet belongs to in a container.</summary>
    public int StreamIndex
    {
        get => NativePointer->stream_index;
        set => NativePointer->stream_index = value;
    }

    /// <summary>Whether the packet starts a key frame.</summary>
    public bool IsKeyFrame
    {
        get => (NativePointer->flags & AV_PKT_FLAG_KEY) != 0;
        set =>
            NativePointer->flags = value
                ? NativePointer->flags | AV_PKT_FLAG_KEY
                : NativePointer->flags & ~AV_PKT_FLAG_KEY;
    }

    /// <summary>Replaces the data with a copy of <paramref name="data"/>, zero-padded for decoders.</summary>
    /// <param name="data">The compressed data.</param>
    public void CopyFrom(ReadOnlySpan<byte> data)
    {
        AVPacket* packet = NativePointer;
        av_packet_unref(packet);
        FFmpegError.ThrowIfError(av_new_packet(packet, data.Length));
        data.CopyTo(new Span<byte>(packet->data, packet->size));
    }

    /// <summary>
    /// Points the packet at managed memory without copying. The memory stays pinned until FFmpeg drops
    /// its last reference to it, which may be after this packet is disposed.
    /// </summary>
    /// <param name="buffer">
    /// The data followed by at least <see cref="PaddingSize"/> zero bytes, which decoders read past the
    /// end of the data. The padding is checked, because a decoder that reads non-zero bytes there
    /// misparses the stream rather than failing.
    /// </param>
    /// <param name="length">The length of the data, excluding the padding.</param>
    public void SetData(ReadOnlyMemory<byte> buffer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            buffer.Length - PaddingSize,
            length,
            nameof(buffer)
        );
        if (buffer.Span.Slice(length, PaddingSize).ContainsAnyExcept((byte)0))
        {
            throw new ArgumentException(
                $"The {PaddingSize} bytes after the data must be zero.",
                nameof(buffer)
            );
        }

        AVPacket* packet = NativePointer;
        av_packet_unref(packet);
        packet->buf = ManagedBuffer.Create(buffer, readOnly: true);
        packet->data = packet->buf->data;
        packet->size = length;
    }

    /// <summary>
    /// A reference to the data that stays valid after the packet is reused or disposed, without copying.
    /// </summary>
    /// <returns>The lease; dispose it to release the reference.</returns>
    public NativeMemoryLease LeaseData()
    {
        AVPacket* packet = NativePointer;
        FFmpegError.ThrowIfError(av_packet_make_refcounted(packet));
        SafeBufferHandle buffer = SafeBufferHandle.Own(av_buffer_ref(packet->buf), "av_buffer_ref");
        return new NativeMemoryLease(buffer, packet->data, packet->size);
    }

    /// <summary>Converts the timestamps and duration to another time base.</summary>
    /// <param name="destination">The time base to convert to.</param>
    public void RescaleTimestamps(Rational destination)
    {
        AVPacket* packet = NativePointer;
        av_packet_rescale_ts(packet, packet->time_base, destination);
        packet->time_base = destination;
    }

    /// <summary>Releases the data and clears the properties, keeping the instance for reuse.</summary>
    public void Reset() => av_packet_unref(NativePointer);

    /// <summary>Makes this packet another reference to <paramref name="source"/>'s data, without copying it.</summary>
    /// <param name="source">The packet to share.</param>
    public void Reference(Packet source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
        {
            return;
        }

        AVPacket* packet = NativePointer;
        av_packet_unref(packet);
        FFmpegError.ThrowIfError(av_packet_ref(packet, source.NativePointer));
    }

    /// <summary>Moves <paramref name="source"/>'s data and properties into this packet, leaving it empty.</summary>
    /// <param name="source">The packet to take from.</param>
    public void MoveFrom(Packet source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
        {
            return;
        }

        AVPacket* packet = NativePointer;
        av_packet_unref(packet);
        av_packet_move_ref(packet, source.NativePointer);
    }

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();
}
