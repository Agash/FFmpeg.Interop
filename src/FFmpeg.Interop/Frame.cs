using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// A decoded video picture or block of audio samples (<see cref="AVFrame"/>).
/// </summary>
/// <remarks>
/// <para>
/// A frame is a reusable container: decoders, scalers and resamplers fill one the caller owns, and the
/// same instance is meant to be passed to them again for the next picture, so a steady-state loop
/// allocates nothing. The picture data is reference counted by FFmpeg; <see cref="Reference"/> shares
/// it with another frame without copying.
/// </para>
/// <para>
/// Data exposed as spans and planes is valid until the frame is next filled, reset or disposed. Not
/// thread-safe.
/// </para>
/// </remarks>
public sealed unsafe class Frame : IDisposable
{
    private readonly SafeFrameHandle _handle;

    /// <summary>Creates an empty frame.</summary>
    public Frame() => _handle = SafeFrameHandle.Allocate();

    private Frame(SafeFrameHandle handle) => _handle = handle;

    /// <summary>
    /// The native frame. For fields and functions the managed API does not cover; the pointer is owned
    /// by this instance and invalid after <see cref="Dispose"/>.
    /// </summary>
    public AVFrame* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Pointer;
        }
    }

    /// <summary>The picture width in pixels; 0 for audio.</summary>
    public int Width
    {
        get => NativePointer->width;
        set => NativePointer->width = value;
    }

    /// <summary>The picture height in pixels; 0 for audio.</summary>
    public int Height
    {
        get => NativePointer->height;
        set => NativePointer->height = value;
    }

    /// <summary>The pixel format of a video frame.</summary>
    public PixelFormat PixelFormat
    {
        get => (AVPixelFormat)NativePointer->format;
        set => NativePointer->format = (int)value.Value;
    }

    /// <summary>The sample format of an audio frame.</summary>
    public SampleFormat SampleFormat
    {
        get => (AVSampleFormat)NativePointer->format;
        set => NativePointer->format = (int)value.Value;
    }

    /// <summary>The number of samples per channel in an audio frame.</summary>
    public int SampleCount
    {
        get => NativePointer->nb_samples;
        set => NativePointer->nb_samples = value;
    }

    /// <summary>The sample rate of an audio frame.</summary>
    public int SampleRate
    {
        get => NativePointer->sample_rate;
        set => NativePointer->sample_rate = value;
    }

    /// <summary>The channel layout of an audio frame.</summary>
    public ChannelLayout ChannelLayout
    {
        get => ChannelLayout.FromNative(&NativePointer->ch_layout);
        set
        {
            AVFrame* frame = NativePointer;
            av_channel_layout_uninit(&frame->ch_layout);
            frame->ch_layout = value.ToNative();
        }
    }

    /// <summary>The presentation timestamp in <see cref="TimeBase"/> units, or null when unset.</summary>
    public long? PresentationTimestamp
    {
        get => NativePointer->pts == AV_NOPTS_VALUE ? null : NativePointer->pts;
        set => NativePointer->pts = value ?? AV_NOPTS_VALUE;
    }

    /// <summary>The duration in <see cref="TimeBase"/> units, or 0 when unknown.</summary>
    public long Duration
    {
        get => NativePointer->duration;
        set => NativePointer->duration = value;
    }

    /// <summary>The time base of <see cref="PresentationTimestamp"/> and <see cref="Duration"/>.</summary>
    public Rational TimeBase
    {
        get => NativePointer->time_base;
        set => NativePointer->time_base = value;
    }

    /// <summary>Whether the frame is a key frame.</summary>
    public bool IsKeyFrame
    {
        get => (NativePointer->flags & AV_FRAME_FLAG_KEY) != 0;
        set =>
            NativePointer->flags = value
                ? NativePointer->flags | AV_FRAME_FLAG_KEY
                : NativePointer->flags & ~AV_FRAME_FLAG_KEY;
    }

    /// <summary>
    /// The picture type. Decoders report what they decoded; for an encoder, setting
    /// <see cref="PictureType.I"/> on a frame forces it to start a key frame, which is how a receiver's
    /// request for a new key frame (an RTCP PLI or FIR) is answered.
    /// </summary>
    public PictureType PictureType
    {
        get => (PictureType)NativePointer->pict_type;
        set => NativePointer->pict_type = (AVPictureType)value;
    }

    /// <summary>The range of the frame's sample values.</summary>
    public ColorRange ColorRange
    {
        get => (ColorRange)NativePointer->color_range;
        set => NativePointer->color_range = (AVColorRange)value;
    }

    /// <summary>The frame's colour primaries.</summary>
    public ColorPrimaries ColorPrimaries
    {
        get => (ColorPrimaries)NativePointer->color_primaries;
        set => NativePointer->color_primaries = (AVColorPrimaries)value;
    }

    /// <summary>The frame's transfer characteristics.</summary>
    public ColorTransfer ColorTransfer
    {
        get => (ColorTransfer)NativePointer->color_trc;
        set => NativePointer->color_trc = (AVColorTransferCharacteristic)value;
    }

    /// <summary>The matrix from RGB to the frame's luma and chroma.</summary>
    public ColorSpace ColorSpace
    {
        get => (ColorSpace)NativePointer->colorspace;
        set => NativePointer->colorspace = (AVColorSpace)value;
    }

    /// <summary>Whether the frame holds picture or sample data.</summary>
    public bool HasData => NativePointer->buf.e0 is not null;

    /// <summary>Whether the data can be written in place: this frame holds the only reference to it.</summary>
    public bool IsWritable => av_frame_is_writable(NativePointer) != 0;

    /// <summary>Whether the frame's data is in a hardware surface rather than system memory.</summary>
    public bool IsHardwareFrame => NativePointer->hw_frames_ctx is not null;

    /// <summary>
    /// Allocates new picture buffers, releasing any the frame held. Their contents are undefined until
    /// written; <see cref="FillBlack"/> gives them a defined picture.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">A system-memory pixel format.</param>
    /// <param name="alignment">The row alignment in bytes, or 0 for the best for this CPU.</param>
    public void AllocateVideo(int width, int height, PixelFormat format, int alignment = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (format.IsHardware)
        {
            throw new ArgumentException(
                $"{format} is a hardware format; get hardware frames from a {nameof(HardwareFramePool)}.",
                nameof(format)
            );
        }

        AVFrame* frame = NativePointer;
        av_frame_unref(frame);
        frame->width = width;
        frame->height = height;
        frame->format = (int)format.Value;
        FFmpegError.ThrowIfError(av_frame_get_buffer(frame, alignment));
    }

    /// <summary>
    /// Sets the whole picture to black in the frame's pixel format, made writable first, at the black
    /// level its <see cref="ColorRange"/> gives (16 for limited-range luma, 0 for full range).
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame holds no system-memory picture.</exception>
    public void FillBlack()
    {
        AVFrame* frame = SoftwareVideoFrame();
        MakeWritable();
        nint* lineSizes = stackalloc nint[4];
        for (int plane = 0; plane < 4; plane++)
        {
            lineSizes[plane] = frame->linesize[plane];
        }

        FFmpegError.ThrowIfError(
            av_image_fill_black(
                (byte**)&frame->data,
                lineSizes,
                (AVPixelFormat)frame->format,
                frame->color_range,
                frame->width,
                frame->height
            )
        );
    }

    /// <summary>Allocates new sample buffers, releasing any the frame held.</summary>
    /// <param name="sampleCount">The number of samples per channel.</param>
    /// <param name="format">The sample format.</param>
    /// <param name="layout">The channel layout.</param>
    /// <param name="sampleRate">The sample rate.</param>
    public void AllocateAudio(
        int sampleCount,
        SampleFormat format,
        ChannelLayout layout,
        int sampleRate
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        AVFrame* frame = NativePointer;
        av_frame_unref(frame);
        frame->nb_samples = sampleCount;
        frame->format = (int)format.Value;
        frame->ch_layout = layout.ToNative();
        frame->sample_rate = sampleRate;
        FFmpegError.ThrowIfError(av_frame_get_buffer(frame, 0));
    }

    /// <summary>
    /// Wraps an image in managed memory as a frame without copying it. The memory stays pinned until
    /// FFmpeg releases its last reference to the frame's data, which may be after this frame is
    /// disposed (an encoder can hold frames it is still encoding). The frame is read-only;
    /// <see cref="MakeWritable"/> copies it.
    /// </summary>
    /// <param name="image">The image, laid out as <c>av_image_fill_arrays</c> describes for the alignment.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="alignment">The row alignment of <paramref name="image"/> in bytes.</param>
    /// <returns>The frame.</returns>
    public static Frame WrapImage(
        ReadOnlyMemory<byte> image,
        int width,
        int height,
        PixelFormat format,
        int alignment = 1
    )
    {
        int size = FFmpegError.ThrowIfError(
            av_image_get_buffer_size(format, width, height, alignment)
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(image.Length, size, nameof(image));

        Frame result = new();
        try
        {
            AVFrame* frame = result._handle.Pointer;
            frame->buf.e0 = ManagedBuffer.Create(image[..size], readOnly: true);
            frame->width = width;
            frame->height = height;
            frame->format = (int)format.Value;
            FFmpegError.ThrowIfError(
                av_image_fill_arrays(
                    (byte**)&frame->data,
                    (int*)&frame->linesize,
                    frame->buf.e0->data,
                    format,
                    width,
                    height,
                    alignment
                )
            );
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Releases the frame's data and clears its properties, keeping the instance for reuse.</summary>
    public void Reset() => av_frame_unref(NativePointer);

    /// <summary>Copies the data if it is shared, so it can be written without affecting other references.</summary>
    public void MakeWritable() => FFmpegError.ThrowIfError(av_frame_make_writable(NativePointer));

    /// <summary>Makes this frame another reference to <paramref name="source"/>'s data, without copying it.</summary>
    /// <param name="source">The frame to share.</param>
    public void Reference(Frame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
        {
            return;
        }

        AVFrame* frame = NativePointer;
        av_frame_unref(frame);
        FFmpegError.ThrowIfError(av_frame_ref(frame, source.NativePointer));
    }

    /// <summary>Moves <paramref name="source"/>'s data and properties into this frame, leaving it empty.</summary>
    /// <param name="source">The frame to take from.</param>
    public void MoveFrom(Frame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
        {
            return;
        }

        AVFrame* frame = NativePointer;
        av_frame_unref(frame);
        av_frame_move_ref(frame, source.NativePointer);
    }

    /// <summary>A new frame referencing the same data, without copying it.</summary>
    /// <returns>The new frame.</returns>
    public Frame Clone() =>
        new(SafeFrameHandle.Wrap(av_frame_clone(NativePointer), "av_frame_clone"));

    /// <summary>Copies timestamps, flags and metadata, but not data, from another frame.</summary>
    /// <param name="source">The frame to copy from.</param>
    public void CopyPropertiesFrom(Frame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        FFmpegError.ThrowIfError(av_frame_copy_props(NativePointer, source.NativePointer));
    }

    /// <summary>One plane of a video frame in system memory, read-only.</summary>
    /// <param name="plane">The plane index, from 0 to <see cref="PixelFormat"/>'s plane count - 1.</param>
    /// <returns>A view of the plane, valid until the frame is next filled, reset or disposed.</returns>
    public ReadOnlyImagePlane GetPlane(int plane)
    {
        AVFrame* frame = VideoPlaneFrame(plane);
        return new(
            frame->data[plane],
            frame->linesize[plane],
            RowLength(frame, plane),
            PixelFormat.PlaneHeight(plane, frame->height)
        );
    }

    /// <summary>One plane of a video frame in system memory, made writable first.</summary>
    /// <param name="plane">The plane index.</param>
    /// <returns>A view of the plane, valid until the frame is next filled, reset or disposed.</returns>
    public ImagePlane GetWritablePlane(int plane)
    {
        AVFrame* frame = VideoPlaneFrame(plane);
        MakeWritable();
        return new(
            frame->data[plane],
            frame->linesize[plane],
            RowLength(frame, plane),
            PixelFormat.PlaneHeight(plane, frame->height)
        );
    }

    /// <summary>The size of the picture packed with the given row alignment.</summary>
    /// <param name="alignment">The row alignment in bytes.</param>
    /// <returns>The size in bytes.</returns>
    public int GetImageSize(int alignment = 1) =>
        FFmpegError.ThrowIfError(av_image_get_buffer_size(PixelFormat, Width, Height, alignment));

    /// <summary>Copies the picture into a packed buffer, as <c>av_image_copy_to_buffer</c> lays it out.</summary>
    /// <param name="destination">The buffer, at least <see cref="GetImageSize"/> bytes.</param>
    /// <param name="alignment">The row alignment of the packed layout.</param>
    /// <returns>The number of bytes written.</returns>
    public int CopyImageTo(Span<byte> destination, int alignment = 1)
    {
        AVFrame* frame = SoftwareVideoFrame();
        fixed (byte* target = destination)
        {
            return FFmpegError.ThrowIfError(
                av_image_copy_to_buffer(
                    target,
                    destination.Length,
                    (byte**)&frame->data,
                    (int*)&frame->linesize,
                    (AVPixelFormat)frame->format,
                    frame->width,
                    frame->height,
                    alignment
                )
            );
        }
    }

    /// <summary>Fills the picture from a packed buffer, making the frame writable first.</summary>
    /// <param name="source">The packed picture, at least <see cref="GetImageSize"/> bytes.</param>
    /// <param name="alignment">The row alignment of the packed layout.</param>
    public void CopyImageFrom(ReadOnlySpan<byte> source, int alignment = 1)
    {
        AVFrame* frame = SoftwareVideoFrame();
        ArgumentOutOfRangeException.ThrowIfLessThan(
            source.Length,
            GetImageSize(alignment),
            nameof(source)
        );
        MakeWritable();

        byte** planes = stackalloc byte*[4];
        int* strides = stackalloc int[4];
        fixed (byte* packed = source)
        {
            FFmpegError.ThrowIfError(
                av_image_fill_arrays(
                    planes,
                    strides,
                    packed,
                    (AVPixelFormat)frame->format,
                    frame->width,
                    frame->height,
                    alignment
                )
            );
            av_image_copy(
                (byte**)&frame->data,
                (int*)&frame->linesize,
                planes,
                strides,
                (AVPixelFormat)frame->format,
                frame->width,
                frame->height
            );
        }
    }

    /// <summary>The samples of one plane of an audio frame, read-only.</summary>
    /// <typeparam name="T">The sample type, whose size must match the sample format.</typeparam>
    /// <param name="plane">The channel for a planar format; 0 for an interleaved one.</param>
    /// <returns>The samples, valid until the frame is next filled, reset or disposed.</returns>
    public ReadOnlySpan<T> GetSamples<T>(int plane = 0)
        where T : unmanaged => SamplePlane<T>(plane);

    /// <summary>The samples of one plane of an audio frame, made writable first.</summary>
    /// <typeparam name="T">The sample type, whose size must match the sample format.</typeparam>
    /// <param name="plane">The channel for a planar format; 0 for an interleaved one.</param>
    /// <returns>The samples, valid until the frame is next filled, reset or disposed.</returns>
    public Span<T> GetWritableSamples<T>(int plane = 0)
        where T : unmanaged
    {
        _ = SamplePlane<T>(plane);
        MakeWritable();
        return SamplePlane<T>(plane);
    }

    /// <summary>
    /// Copies between a hardware frame and a system-memory frame: downloads when this frame is in
    /// hardware, uploads when <paramref name="destination"/> is.
    /// </summary>
    /// <param name="destination">
    /// The target. A system-memory target may be empty, which picks the surface's own format; a hardware
    /// target must already have a surface from a <see cref="HardwareFramePool"/>.
    /// </param>
    public void TransferTo(Frame destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        FFmpegError.ThrowIfError(
            av_hwframe_transfer_data(destination.NativePointer, NativePointer, 0)
        );
        FFmpegError.ThrowIfError(av_frame_copy_props(destination.NativePointer, NativePointer));
    }

    /// <summary>
    /// Copies this frame's picture and properties into <paramref name="destination"/>, which already holds
    /// a picture of the same size and format: on the CPU for system-memory frames, on the GPU for two
    /// Vulkan frames of one device. A GPU copy has finished when this returns, so the source may be
    /// released (a mapped frame unmapped) straight after.
    /// </summary>
    /// <param name="destination">
    /// The target: an allocated system-memory picture, or a surface from a <see cref="HardwareFramePool"/>.
    /// </param>
    /// <exception cref="ArgumentException">The frames differ in size, format or device.</exception>
    /// <exception cref="NotSupportedException">Hardware frames other than Vulkan, or a hardware and a
    /// system-memory frame (use <see cref="TransferTo"/>).</exception>
    public void CopyTo(Frame destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        PixelFormat source = PixelFormat;
        PixelFormat target = destination.PixelFormat;
        if (source.IsHardware || target.IsHardware)
        {
            if (source != PixelFormat.Vulkan || target != PixelFormat.Vulkan)
            {
                throw new NotSupportedException(
                    $"Copying {source} to {target} is not supported: hardware frames copy between Vulkan "
                        + $"surfaces of one device; {nameof(TransferTo)} moves pictures to and from system memory."
                );
            }

            VulkanCopy.Copy(this, destination);
        }
        else
        {
            if (Width != destination.Width || Height != destination.Height || source != target)
            {
                throw new ArgumentException(
                    $"The frames differ ({Width}x{Height} {source}, {destination.Width}x{destination.Height} {target}).",
                    nameof(destination)
                );
            }

            destination.MakeWritable();
            FFmpegError.ThrowIfError(av_frame_copy(destination.NativePointer, NativePointer));
        }

        FFmpegError.ThrowIfError(av_frame_copy_props(destination.NativePointer, NativePointer));
    }

    /// <summary>
    /// Maps this hardware frame into another representation without copying, for example a VA-API
    /// surface to DRM PRIME descriptors, or a hardware surface into system memory.
    /// </summary>
    /// <param name="destination">The target, with <see cref="PixelFormat"/> set to the format to map to.</param>
    /// <param name="access">How the mapping will be used.</param>
    public void MapTo(Frame destination, HardwareMapAccess access)
    {
        ArgumentNullException.ThrowIfNull(destination);
        FFmpegError.ThrowIfError(
            av_hwframe_map(destination.NativePointer, NativePointer, (int)access)
        );
    }

    /// <summary>
    /// Maps this frame into a surface of <paramref name="pool"/>'s device without copying: a DMA-BUF
    /// imported with <see cref="DrmExtensions.FromDrmPrime"/> into a VA-API or Vulkan surface an encoder takes, or a
    /// D3D11 surface into QSV.
    /// </summary>
    /// <param name="pool">The pool whose device and format the mapping is for.</param>
    /// <param name="destination">The frame to receive the mapping; its previous content is released.</param>
    /// <param name="access">How the mapping will be used.</param>
    public void MapTo(HardwareFramePool pool, Frame destination, HardwareMapAccess access)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(destination);
        AVFrame* target = destination.NativePointer;
        av_frame_unref(target);
        target->format = (int)pool.Format.Value;
        target->hw_frames_ctx = pool.NewReference();
        FFmpegError.ThrowIfError(av_hwframe_map(target, NativePointer, (int)access));
        FFmpegError.ThrowIfError(av_frame_copy_props(target, NativePointer));
    }

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();

    private static int RowLength(AVFrame* frame, int plane) =>
        FFmpegError.ThrowIfError(
            av_image_get_linesize((AVPixelFormat)frame->format, frame->width, plane)
        );

    private AVFrame* SoftwareVideoFrame()
    {
        AVFrame* frame = NativePointer;
        PixelFormat format = (AVPixelFormat)frame->format;
        if (frame->width <= 0 || format == PixelFormat.None)
        {
            throw new InvalidOperationException("The frame is not a video frame.");
        }

        if (format.IsHardware)
        {
            throw new InvalidOperationException(
                $"The frame is a {format} hardware surface; {nameof(TransferTo)} a system-memory frame to read its pixels."
            );
        }

        if (frame->data[0] is null)
        {
            throw new InvalidOperationException("The frame has no picture data.");
        }

        return frame;
    }

    private AVFrame* VideoPlaneFrame(int plane)
    {
        AVFrame* frame = SoftwareVideoFrame();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)plane,
            (uint)PixelFormat.PlaneCount,
            nameof(plane)
        );
        return frame;
    }

    private Span<T> SamplePlane<T>(int plane)
        where T : unmanaged
    {
        AVFrame* frame = NativePointer;
        SampleFormat format = (AVSampleFormat)frame->format;
        if (
            frame->nb_samples <= 0
            || frame->width != 0
            || format == SampleFormat.None
            || frame->extended_data is null
        )
        {
            throw new InvalidOperationException("The frame is not an audio frame with samples.");
        }

        if (sizeof(T) != format.BytesPerSample)
        {
            throw new ArgumentException(
                $"{typeof(T).Name} is {sizeof(T)} bytes; {format} samples are {format.BytesPerSample}.",
                nameof(T)
            );
        }

        int channels = frame->ch_layout.nb_channels;
        int planes = format.IsPlanar ? channels : 1;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)plane,
            (uint)planes,
            nameof(plane)
        );
        int count = format.IsPlanar ? frame->nb_samples : frame->nb_samples * channels;
        return new Span<T>(frame->extended_data[plane], count);
    }
}

/// <summary>How a mapped hardware frame will be accessed.</summary>
[Flags]
public enum HardwareMapAccess
{
    /// <summary>The mapping will be read.</summary>
    Read = LibAVUtil.AV_HWFRAME_MAP_READ,

    /// <summary>The mapping will be written.</summary>
    Write = LibAVUtil.AV_HWFRAME_MAP_WRITE,

    /// <summary>The previous contents will be overwritten entirely, so they need not be mapped.</summary>
    Overwrite = LibAVUtil.AV_HWFRAME_MAP_OVERWRITE,

    /// <summary>Fail rather than fall back to a copy.</summary>
    Direct = LibAVUtil.AV_HWFRAME_MAP_DIRECT,
}
