using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// A first-in first-out buffer of audio samples (<c>AVAudioFifo</c>): takes frames of any size and gives
/// frames of the size asked for, as an encoder's fixed frame size needs (AAC's 1024, Opus's 960).
/// </summary>
public sealed unsafe class AudioFifo : IDisposable
{
    private readonly SampleFormat _format;
    private readonly ChannelLayout _layout;
    private readonly int _sampleRate;
    private AVAudioFifo* _fifo;

    /// <summary>A buffer for samples of one format, layout and rate.</summary>
    /// <param name="format">The sample format.</param>
    /// <param name="layout">The channel layout.</param>
    /// <param name="sampleRate">The sample rate, stamped on the frames read.</param>
    /// <param name="capacity">The samples it holds before it grows.</param>
    public AudioFifo(SampleFormat format, ChannelLayout layout, int sampleRate, int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _format = format;
        _layout = layout;
        _sampleRate = sampleRate;
        _fifo = av_audio_fifo_alloc(format.Value, layout.ChannelCount, capacity);
        if (_fifo is null)
        {
            FFmpegError.ThrowOutOfMemory("av_audio_fifo_alloc");
        }
    }

    /// <summary>The samples held, per channel.</summary>
    public int Count => av_audio_fifo_size(Fifo);

    /// <summary>Appends a frame's samples.</summary>
    /// <param name="frame">A frame in the buffer's format and layout.</param>
    public void Write(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        AVFrame* native = frame.NativePointer;
        if (
            native->format != (int)_format.Value
            || native->ch_layout.nb_channels != _layout.ChannelCount
        )
        {
            throw new ArgumentException(
                "The frame's format or channels differ from the buffer's.",
                nameof(frame)
            );
        }

        int written = av_audio_fifo_write(Fifo, (void**)native->extended_data, native->nb_samples);
        FFmpegError.ThrowIfError(written, "av_audio_fifo_write");
    }

    /// <summary>
    /// Reads exactly <paramref name="samples"/> samples into a frame, which keeps its buffers between reads
    /// of one size, so a steady stream of reads allocates nothing.
    /// </summary>
    /// <param name="frame">The frame to fill.</param>
    /// <param name="samples">How many samples per channel.</param>
    /// <returns>False when fewer are held.</returns>
    public bool TryRead(Frame frame, int samples)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        if (Count < samples)
        {
            return false;
        }

        AVFrame* native = frame.NativePointer;
        bool fits =
            native->buf[0] is not null
            && native->nb_samples == samples
            && native->format == (int)_format.Value
            && native->ch_layout.nb_channels == _layout.ChannelCount;
        if (!fits)
        {
            av_frame_unref(native);
            native->nb_samples = samples;
            native->format = (int)_format.Value;
            native->sample_rate = _sampleRate;
            frame.ChannelLayout = _layout;
            FFmpegError.ThrowIfError(av_frame_get_buffer(native, 0), "av_frame_get_buffer");
        }
        else
        {
            FFmpegError.ThrowIfError(av_frame_make_writable(native), "av_frame_make_writable");
        }

        int read = av_audio_fifo_read(Fifo, (void**)native->extended_data, samples);
        FFmpegError.ThrowIfError(read, "av_audio_fifo_read");
        return true;
    }

    /// <summary>Drops every sample held.</summary>
    public void Clear() => av_audio_fifo_reset(Fifo);

    /// <inheritdoc/>
    public void Dispose()
    {
        AVAudioFifo* fifo = _fifo;
        _fifo = null;
        if (fifo is not null)
        {
            av_audio_fifo_free(fifo);
        }
    }

    private AVAudioFifo* Fifo
    {
        get
        {
            ObjectDisposedException.ThrowIf(_fifo is null, this);
            return _fifo;
        }
    }
}
