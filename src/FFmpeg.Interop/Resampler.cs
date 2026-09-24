using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibSwResample;

namespace FFmpeg.Interop;

/// <summary>Converts audio between sample rates, sample formats and channel layouts (<see cref="SwrContext"/>).</summary>
public sealed unsafe class Resampler : IDisposable
{
    private readonly SafeResamplerHandle _handle;

    // A native- or unspecified-order layout, which owns no memory, so it can be copied by value.
    private readonly AVChannelLayout _outputLayout;

    /// <summary>Creates a resampler for one input and one output configuration.</summary>
    /// <param name="inputLayout">The input channel layout.</param>
    /// <param name="inputFormat">The input sample format.</param>
    /// <param name="inputRate">The input sample rate.</param>
    /// <param name="outputLayout">The output channel layout.</param>
    /// <param name="outputFormat">The output sample format.</param>
    /// <param name="outputRate">The output sample rate.</param>
    public Resampler(
        ChannelLayout inputLayout,
        SampleFormat inputFormat,
        int inputRate,
        ChannelLayout outputLayout,
        SampleFormat outputFormat,
        int outputRate
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        AVChannelLayout input = inputLayout.ToNative();
        AVChannelLayout output = outputLayout.ToNative();
        SwrContext* context = null;
        FFmpegError.ThrowIfError(
            swr_alloc_set_opts2(
                &context,
                &output,
                outputFormat,
                outputRate,
                &input,
                inputFormat,
                inputRate,
                0,
                null
            )
        );
        _handle = SafeResamplerHandle.Own(context);
        int initialized = swr_init(context);
        if (initialized < 0)
        {
            _handle.Dispose();
            FFmpegError.Throw(initialized, "swr_init");
        }

        _outputLayout = output;
        InputChannels = inputLayout.ChannelCount;
        OutputChannels = outputLayout.ChannelCount;
        InputFormat = inputFormat;
        OutputFormat = outputFormat;
        InputRate = inputRate;
        OutputRate = outputRate;
    }

    /// <summary>The native context, owned by this instance and invalid after <see cref="Dispose"/>.</summary>
    public SwrContext* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Pointer;
        }
    }

    /// <summary>The input sample rate.</summary>
    public int InputRate { get; }

    /// <summary>The output sample rate.</summary>
    public int OutputRate { get; }

    /// <summary>The input sample format.</summary>
    public SampleFormat InputFormat { get; }

    /// <summary>The output sample format.</summary>
    public SampleFormat OutputFormat { get; }

    /// <summary>The number of input channels.</summary>
    public int InputChannels { get; }

    /// <summary>The number of output channels.</summary>
    public int OutputChannels { get; }

    /// <summary>The samples buffered inside the resampler, in output samples per channel.</summary>
    public long Delay => swr_get_delay(NativePointer, OutputRate);

    /// <summary>An upper bound on the output samples per channel the next conversion can produce.</summary>
    /// <param name="inputSamples">The input samples per channel about to be converted.</param>
    /// <returns>The bound.</returns>
    public int GetMaxOutputSamples(int inputSamples) =>
        FFmpegError.ThrowIfError(swr_get_out_samples(NativePointer, inputSamples));

    /// <summary>
    /// Converts interleaved samples. Both formats must be interleaved; planar audio goes through
    /// <see cref="Convert(Frame?, Frame)"/>.
    /// </summary>
    /// <typeparam name="TIn">The input sample type, matching <see cref="InputFormat"/>.</typeparam>
    /// <typeparam name="TOut">The output sample type, matching <see cref="OutputFormat"/>.</typeparam>
    /// <param name="input">The input, all channels interleaved; empty to drain the buffered samples.</param>
    /// <param name="output">The output buffer.</param>
    /// <returns>The number of output samples written per channel.</returns>
    public int Convert<TIn, TOut>(ReadOnlySpan<TIn> input, Span<TOut> output)
        where TIn : unmanaged
        where TOut : unmanaged
    {
        CheckInterleaved<TIn>(InputFormat, nameof(input));
        CheckInterleaved<TOut>(OutputFormat, nameof(output));
        int inputSamples = input.Length / InputChannels;
        int outputCapacity = output.Length / OutputChannels;
        fixed (TIn* source = input)
        fixed (TOut* target = output)
        {
            byte* inPlane = (byte*)source;
            byte* outPlane = (byte*)target;
            return FFmpegError.ThrowIfError(
                swr_convert(
                    NativePointer,
                    &outPlane,
                    outputCapacity,
                    inputSamples == 0 ? null : &inPlane,
                    inputSamples
                )
            );
        }
    }

    /// <summary>
    /// Converts a frame, or drains buffered samples when <paramref name="source"/> is null. The
    /// destination's previous data is released; it receives new buffers in the output format, layout
    /// and rate, sized for what the conversion produces.
    /// </summary>
    /// <param name="source">The input frame, or null to drain.</param>
    /// <param name="destination">The output frame.</param>
    public void Convert(Frame? source, Frame destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        AVFrame* output = destination.NativePointer;
        LibAVUtil.av_frame_unref(output);
        output->format = (int)OutputFormat.Value;
        output->sample_rate = OutputRate;
        output->ch_layout = _outputLayout;
        FFmpegError.ThrowIfError(
            swr_convert_frame(NativePointer, output, source is null ? null : source.NativePointer)
        );
    }

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();

    private static void CheckInterleaved<T>(SampleFormat format, string parameter)
        where T : unmanaged
    {
        if (format.IsPlanar)
        {
            throw new InvalidOperationException(
                $"{format} is planar; convert planar audio with frames."
            );
        }

        if (sizeof(T) != format.BytesPerSample)
        {
            throw new ArgumentException(
                $"{typeof(T).Name} does not match {format} samples.",
                parameter
            );
        }
    }
}
