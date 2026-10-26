using FFmpeg.Interop.Native;

namespace FFmpeg.Interop.Tests;

/// <summary>
/// The libavfilter graph, the bitstream filter and the audio FIFO, each against what FFmpeg's own
/// tools give for the same filter.
/// </summary>
[TestClass]
public sealed class FilteringTests
{
    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    public void FilterGraph_ScalesAndConvertsToTheSinksFormat()
    {
        using FilterGraph graph = FilterGraph.Video(
            "scale=32:16",
            [new VideoFilterInput(64, 32, PixelFormat.Yuv420P, new Rational(1, 30))],
            [PixelFormat.Nv12]
        );
        using Frame input = new();
        input.AllocateVideo(64, 32, PixelFormat.Yuv420P);
        input.FillBlack();
        input.PresentationTimestamp = 7;
        using Frame output = new();

        Assert.AreEqual(CodecStatus.NeedsInput, graph.Receive(output));
        graph.Send(input);
        Assert.AreEqual(CodecStatus.Available, graph.Receive(output));

        Assert.AreEqual(32, output.Width);
        Assert.AreEqual(16, output.Height);
        Assert.AreEqual(PixelFormat.Nv12, output.PixelFormat);
        Assert.AreEqual(7, output.PresentationTimestamp);
        Assert.AreEqual(new Rational(1, 30), graph.OutputTimeBase);
        Assert.IsTrue(input.HasData, "the sent frame stays the caller's");

        graph.SendEndOfStream();
        Assert.AreEqual(CodecStatus.EndOfStream, graph.Receive(output));
    }

    [TestMethod]
    public void FilterGraph_TwoInputs_OverlaysTheSecondOnTheFirst()
    {
        VideoFilterInput input = new(16, 16, PixelFormat.Yuv420P, new Rational(1, 30));
        using FilterGraph graph = FilterGraph.Video(
            "[in0][in1]overlay=8:0",
            [input, input],
            [PixelFormat.Yuv420P]
        );
        using Frame background = Filled(0);
        using Frame overlay = Filled(200);
        using Frame output = new();

        Assert.AreEqual(2, graph.InputCount);
        graph.Send(background, 0);
        graph.Send(overlay, 1);
        graph.SendEndOfStream(0);
        graph.SendEndOfStream(1);
        Assert.AreEqual(CodecStatus.Available, graph.Receive(output));

        ReadOnlySpan<byte> row = output.GetPlane(0).GetRow(4);
        Assert.AreEqual(0, row[3], "left of the overlay");
        Assert.AreEqual(200, row[12], "under the overlay");
    }

    [TestMethod]
    public void FilterGraph_Audio_ResamplesToTheSinksFormat()
    {
        using FilterGraph graph = FilterGraph.Audio(
            "aresample=24000",
            [new AudioFilterInput(48000, SampleFormat.S16, ChannelLayout.Stereo)],
            [SampleFormat.Float]
        );
        using Frame input = new();
        input.AllocateAudio(960, SampleFormat.S16, ChannelLayout.Stereo, 48000);
        input.PresentationTimestamp = 0;
        using Frame output = new();
        int samples = 0;

        graph.Send(input);
        graph.SendEndOfStream();
        while (graph.Receive(output) == CodecStatus.Available)
        {
            Assert.AreEqual(SampleFormat.Float, output.SampleFormat);
            Assert.AreEqual(24000, output.SampleRate);
            samples += output.SampleCount;
        }

        Assert.AreEqual(480, samples, "half the rate, half the samples");
    }

    [TestMethod]
    public void FilterGraph_UnknownFilter_Throws() =>
        _ = Assert.ThrowsExactly<FFmpegException>(() =>
            FilterGraph.Video(
                "no_such_filter",
                [new VideoFilterInput(16, 16, PixelFormat.Yuv420P, new Rational(1, 30))]
            )
        );

    [TestMethod]
    public void AudioFifo_GivesFramesOfTheSizeAsked()
    {
        using AudioFifo fifo = new(SampleFormat.Float, ChannelLayout.Stereo, 48000);
        using Frame input = new();
        input.AllocateAudio(500, SampleFormat.Float, ChannelLayout.Stereo, 48000);
        using Frame output = new();

        fifo.Write(input);
        fifo.Write(input);
        Assert.IsFalse(fifo.TryRead(output, 1024), "1000 held");
        fifo.Write(input);
        Assert.IsTrue(fifo.TryRead(output, 1024));

        Assert.AreEqual(1024, output.SampleCount);
        Assert.AreEqual(SampleFormat.Float, output.SampleFormat);
        Assert.AreEqual(48000, output.SampleRate);
        Assert.AreEqual(476, fifo.Count);
        fifo.Clear();
        Assert.AreEqual(0, fifo.Count);
    }

    [TestMethod]
    public void AudioFifo_FrameInAnotherFormat_Throws()
    {
        using AudioFifo fifo = new(SampleFormat.Float, ChannelLayout.Stereo, 48000);
        using Frame input = new();
        input.AllocateAudio(10, SampleFormat.S16, ChannelLayout.Stereo, 48000);

        _ = Assert.ThrowsExactly<ArgumentException>(() => fifo.Write(input));
    }

    [TestMethod]
    public unsafe void BitstreamFilter_PassesPacketsThroughInOrder()
    {
        AVCodecParameters* parameters = LibAVCodec.avcodec_parameters_alloc();
        try
        {
            parameters->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            parameters->codec_id = AVCodecID.AV_CODEC_ID_MPEG4;
            using BitstreamFilter filter = BitstreamFilter.Create("null", parameters, new(1, 30));
            using Packet packet = new();
            using Packet filtered = new();

            for (int i = 0; i < 3; i++)
            {
                packet.CopyFrom([(byte)i, 1, 2, 3]);
                packet.PresentationTimestamp = i;
                Assert.IsTrue(filter.TrySend(packet));
                Assert.AreEqual(CodecStatus.Available, filter.Receive(filtered));
                Assert.AreEqual(i, filtered.PresentationTimestamp);
                Assert.AreEqual((byte)i, filtered.Data[0]);
                Assert.AreEqual(new Rational(1, 30), filtered.TimeBase);
            }

            filter.SendEndOfStream();
            Assert.AreEqual(CodecStatus.EndOfStream, filter.Receive(filtered));
            Assert.AreEqual(new Rational(1, 30), filter.OutputTimeBase);
        }
        finally
        {
            LibAVCodec.avcodec_parameters_free(&parameters);
        }
    }

    [TestMethod]
    public unsafe void BitstreamFilter_UnknownName_Throws() =>
        _ = Assert.ThrowsExactly<FFmpegException>(() =>
            BitstreamFilter.Create("no_such_bsf", null, new Rational(1, 30))
        );

    private static Frame Filled(byte value)
    {
        Frame frame = new();
        frame.AllocateVideo(16, 16, PixelFormat.Yuv420P);
        frame.PresentationTimestamp = 0;
        ImagePlane plane = frame.GetWritablePlane(0);
        for (int row = 0; row < plane.Height; row++)
        {
            plane.GetRow(row).Fill(value);
        }

        return frame;
    }
}
