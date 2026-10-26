using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVFilter;

namespace FFmpeg.Interop;

/// <summary>What frames a video filter graph input takes.</summary>
/// <param name="Width">The picture width.</param>
/// <param name="Height">The picture height.</param>
/// <param name="PixelFormat">The pixel format.</param>
/// <param name="TimeBase">The frames' time base.</param>
public sealed record VideoFilterInput(
    int Width,
    int Height,
    PixelFormat PixelFormat,
    Rational TimeBase
)
{
    /// <summary>The pixel aspect ratio; square by default.</summary>
    public Rational SampleAspectRatio { get; init; } = new(1, 1);
}

/// <summary>What frames an audio filter graph input takes.</summary>
/// <param name="SampleRate">The sample rate.</param>
/// <param name="SampleFormat">The sample format.</param>
/// <param name="ChannelLayout">The channel layout.</param>
public sealed record AudioFilterInput(
    int SampleRate,
    SampleFormat SampleFormat,
    ChannelLayout ChannelLayout
)
{
    /// <summary>The frames' time base; one sample by default.</summary>
    public Rational? TimeBase { get; init; }
}

/// <summary>
/// A libavfilter graph from FFmpeg's filter syntax, between buffer sources and a buffer sink: scale, crop,
/// overlay, deinterlace, tone-map, aresample, amix and the rest. One input is labelled <c>[in]</c>,
/// several <c>[in0]</c>, <c>[in1]</c> and so on, and the output <c>[out]</c>; as in FFmpeg's own graphs,
/// <c>[in]</c> and <c>[out]</c> may be left out, as in <c>scale=1280:720,format=nv12</c>. Send frames to an input, then receive what comes out, as with
/// a codec.
/// </summary>
public sealed unsafe class FilterGraph : IDisposable
{
    private readonly ImmutableArray<nint> _sources;
    private readonly nint _sink;
    private AVFilterGraph* _graph;

    private FilterGraph(AVFilterGraph* graph, ImmutableArray<nint> sources, AVFilterContext* sink)
    {
        _graph = graph;
        _sources = sources;
        _sink = (nint)sink;
    }

    /// <summary>The native graph, owned by this instance and invalid after <see cref="Dispose"/>.</summary>
    public AVFilterGraph* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_graph is null, this);
            return _graph;
        }
    }

    /// <summary>How many inputs the graph takes.</summary>
    public int InputCount => _sources.Length;

    /// <summary>The time base of the frames that come out.</summary>
    public Rational OutputTimeBase => av_buffersink_get_time_base(Sink);

    /// <summary>A video graph.</summary>
    /// <param name="description">The graph, in FFmpeg's filter syntax.</param>
    /// <param name="inputs">What each input takes, in label order.</param>
    /// <param name="outputFormats">The pixel formats the output may come in, most preferred first; any when empty.</param>
    /// <returns>The graph.</returns>
    public static FilterGraph Video(
        string description,
        ReadOnlySpan<VideoFilterInput> inputs,
        ReadOnlySpan<PixelFormat> outputFormats = default
    )
    {
        string[] arguments = new string[inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            VideoFilterInput input = inputs[i];
            arguments[i] = string.Create(
                CultureInfo.InvariantCulture,
                $"video_size={input.Width}x{input.Height}:pix_fmt={(int)input.PixelFormat.Value}:time_base={input.TimeBase}:pixel_aspect={input.SampleAspectRatio}"
            );
        }

        return Create(
            description,
            "buffer",
            arguments,
            "buffersink",
            "pixel_formats",
            AVOptionType.AV_OPT_TYPE_PIXEL_FMT,
            MemoryMarshal.Cast<PixelFormat, int>(outputFormats)
        );
    }

    /// <summary>An audio graph.</summary>
    /// <param name="description">The graph, in FFmpeg's filter syntax.</param>
    /// <param name="inputs">What each input takes, in label order.</param>
    /// <param name="outputFormats">The sample formats the output may come in; any when empty.</param>
    /// <returns>The graph.</returns>
    public static FilterGraph Audio(
        string description,
        ReadOnlySpan<AudioFilterInput> inputs,
        ReadOnlySpan<SampleFormat> outputFormats = default
    )
    {
        string[] arguments = new string[inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            AudioFilterInput input = inputs[i];
            Rational timeBase = input.TimeBase ?? new Rational(1, input.SampleRate);
            arguments[i] = string.Create(
                CultureInfo.InvariantCulture,
                $"time_base={timeBase}:sample_rate={input.SampleRate}:sample_fmt={input.SampleFormat.Name}:channel_layout={input.ChannelLayout}"
            );
        }

        return Create(
            description,
            "abuffer",
            arguments,
            "abuffersink",
            "sample_formats",
            AVOptionType.AV_OPT_TYPE_SAMPLE_FMT,
            MemoryMarshal.Cast<SampleFormat, int>(outputFormats)
        );
    }

    /// <summary>
    /// Sends a frame to an input. The graph keeps its own reference, so the frame stays the caller's.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="input">The input, in label order.</param>
    public void Send(Frame frame, int input = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        FFmpegError.ThrowIfError(
            av_buffersrc_add_frame_flags(
                Source(input),
                frame.NativePointer,
                AV_BUFFERSRC_FLAG_KEEP_REF
            ),
            "av_buffersrc_add_frame_flags"
        );
    }

    /// <summary>Signals end of stream on an input, so the graph puts out what it still holds.</summary>
    /// <param name="input">The input, in label order.</param>
    public void SendEndOfStream(int input = 0) =>
        FFmpegError.ThrowIfError(
            av_buffersrc_add_frame_flags(Source(input), null, 0),
            "av_buffersrc_add_frame_flags"
        );

    /// <summary>Receives the next filtered frame, if there is one.</summary>
    /// <param name="frame">The frame to fill, in <see cref="OutputTimeBase"/>; its previous content is released.</param>
    /// <returns>Whether a frame was produced, more input is needed, or the stream is finished.</returns>
    public CodecStatus Receive(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        LibAVUtil.av_frame_unref(frame.NativePointer);
        int result = av_buffersink_get_frame(Sink, frame.NativePointer);
        return result switch
        {
            >= 0 => CodecStatus.Available,
            _ when result == LibAVUtil.AVERROR_EAGAIN => CodecStatus.NeedsInput,
            _ when result == LibAVUtil.AVERROR_EOF => CodecStatus.EndOfStream,
            _ => throw FFmpegError.Create(result, "av_buffersink_get_frame"),
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        AVFilterGraph* graph = _graph;
        _graph = null;
        if (graph is not null)
        {
            avfilter_graph_free(&graph);
        }
    }

    private AVFilterContext* Sink
    {
        get
        {
            _ = NativePointer;
            return (AVFilterContext*)_sink;
        }
    }

    private AVFilterContext* Source(int input)
    {
        _ = NativePointer;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)input,
            (uint)_sources.Length,
            nameof(input)
        );
        return (AVFilterContext*)_sources[input];
    }

    // The graph as filtering_video.c builds it: a buffer source per input and one buffer sink, the
    // description parsed between them, then configured.
    private static FilterGraph Create(
        string description,
        string sourceFilter,
        string[] sourceArguments,
        string sinkFilter,
        string formatOption,
        AVOptionType formatType,
        ReadOnlySpan<int> formats
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        if (sourceArguments.Length == 0)
        {
            throw new ArgumentException(
                "A graph needs at least one input.",
                nameof(sourceArguments)
            );
        }

        AVFilterGraph* graph = avfilter_graph_alloc();
        if (graph is null)
        {
            FFmpegError.ThrowOutOfMemory("avfilter_graph_alloc");
        }

        AVFilterInOut* outputs = null;
        AVFilterInOut* inputs = null;
        try
        {
            ImmutableArray<nint>.Builder sources = ImmutableArray.CreateBuilder<nint>(
                sourceArguments.Length
            );
            AVFilter* source = Find(sourceFilter);
            for (int i = 0; i < sourceArguments.Length; i++)
            {
                AVFilterContext* context = null;
                using Utf8String name = new(sourceArguments.Length == 1 ? "in" : $"in{i}");
                using Utf8String arguments = new(sourceArguments[i]);
                FFmpegError.ThrowIfError(
                    avfilter_graph_create_filter(&context, source, name, arguments, null, graph),
                    "avfilter_graph_create_filter"
                );
                sources.Add((nint)context);

                // The graph's open outputs are the sources. The parser links an unlabelled first input
                // to the one named "in" (graphparser.c), so a single source takes that name.
                AVFilterInOut* open = avfilter_inout_alloc();
                if (open is null)
                {
                    FFmpegError.ThrowOutOfMemory("avfilter_inout_alloc");
                }

                open->name = LibAVUtil.av_strdup(name);
                open->filter_ctx = context;
                open->pad_idx = 0;
                open->next = null;
                AVFilterInOut** tail = &outputs;
                while (*tail is not null)
                {
                    tail = &(*tail)->next;
                }

                *tail = open;
            }

            // The sink, its formats set before it is initialised, so the graph converts to one of them.
            AVFilterContext* sink;
            using (Utf8String sinkName = new("out"))
            {
                sink = avfilter_graph_alloc_filter(graph, Find(sinkFilter), sinkName);
            }

            if (sink is null)
            {
                FFmpegError.ThrowOutOfMemory("avfilter_graph_alloc_filter");
            }

            if (!formats.IsEmpty)
            {
                using Utf8String option = new(formatOption);
                fixed (int* values = formats)
                {
                    FFmpegError.ThrowIfError(
                        LibAVUtil.av_opt_set_array(
                            sink,
                            option,
                            LibAVUtil.AV_OPT_SEARCH_CHILDREN,
                            0,
                            (uint)formats.Length,
                            formatType,
                            values
                        ),
                        "av_opt_set_array"
                    );
                }
            }

            FFmpegError.ThrowIfError(avfilter_init_str(sink, null), "avfilter_init_str");
            inputs = avfilter_inout_alloc();
            if (inputs is null)
            {
                FFmpegError.ThrowOutOfMemory("avfilter_inout_alloc");
            }

            using (Utf8String outName = new("out"))
            {
                inputs->name = LibAVUtil.av_strdup(outName);
            }

            inputs->filter_ctx = sink;
            inputs->pad_idx = 0;
            inputs->next = null;

            using (Utf8String native = new(description))
            {
                FFmpegError.ThrowIfError(
                    avfilter_graph_parse_ptr(graph, native, &inputs, &outputs, null),
                    "avfilter_graph_parse_ptr"
                );
            }

            FFmpegError.ThrowIfError(avfilter_graph_config(graph, null), "avfilter_graph_config");
            return new FilterGraph(graph, sources.MoveToImmutable(), sink);
        }
        catch
        {
            avfilter_graph_free(&graph);
            throw;
        }
        finally
        {
            avfilter_inout_free(&inputs);
            avfilter_inout_free(&outputs);
        }
    }

    private static AVFilter* Find(string name)
    {
        using Utf8String native = new(name);
        AVFilter* filter = avfilter_get_by_name(native);
        return filter is not null
            ? filter
            : throw new InvalidOperationException($"FFmpeg has no {name} filter.");
    }
}
