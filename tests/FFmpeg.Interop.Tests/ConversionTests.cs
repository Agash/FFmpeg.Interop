namespace FFmpeg.Interop.Tests;

/// <summary>
/// Scaling and resampling through the managed API, checked against the same conversions done by the
/// ffmpeg tool.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class ConversionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(TestMedia.Width, TestMedia.Height)]
    [DataRow(160, 120)]
    public async Task Scale_Rgb24ToYuv420P_MatchesTheFFmpegTool(int width, int height)
    {
        TestNatives.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string rgb = await TestMedia.RawVideoAsync("rgb24", cancellationToken);
        byte[] source = await File.ReadAllBytesAsync(rgb, cancellationToken);
        int sourceFrame = TestMedia.Width * TestMedia.Height * 3;
        using Scratch scratch = new();
        string ours = scratch["ours.yuv"];
        string theirs = scratch["theirs.yuv"];

        using (FileStream output = File.Create(ours))
        using (
            Scaler scaler = new(
                new ScalerOptions { Algorithm = ScaleAlgorithm.Bicubic, AccurateRounding = true }
            )
        )
        using (Frame destination = new())
        {
            byte[] packed = [];
            for (int i = 0; i < TestMedia.FrameCount; i++)
            {
                using Frame frame = Frame.WrapImage(
                    source.AsMemory(i * sourceFrame, sourceFrame),
                    TestMedia.Width,
                    TestMedia.Height,
                    PixelFormat.Rgb24
                );
                frame.PresentationTimestamp = i;
                destination.Width = width;
                destination.Height = height;
                destination.PixelFormat = PixelFormat.Yuv420P;

                scaler.Scale(frame, destination);

                Assert.AreEqual(i, destination.PresentationTimestamp);
                packed =
                    packed.Length == destination.GetImageSize()
                        ? packed
                        : new byte[destination.GetImageSize()];
                _ = destination.CopyImageTo(packed);
                output.Write(packed);
            }
        }

        _ = await FFmpegCli.FFmpegAsync(
            cancellationToken,
            [
                .. FFmpegCli.RawVideoInput(rgb, "rgb24", TestMedia.Width, TestMedia.Height),
                "-vf",
                $"scale={width}:{height}:flags=bicubic+accurate_rnd",
                "-pix_fmt",
                "yuv420p",
                "-f",
                "rawvideo",
                theirs,
            ]
        );
        double psnr = await FFmpegCli.PsnrAsync(
            FFmpegCli.RawVideoInput(ours, "yuv420p", width, height),
            FFmpegCli.RawVideoInput(theirs, "yuv420p", width, height),
            cancellationToken
        );

        Assert.IsGreaterThan(45.0, psnr, $"PSNR {psnr:F1} dB against the tool's own conversion.");
    }

    [TestMethod]
    public void Scale_ReusedDestinationWithAnotherSize_GetsFreshBuffers()
    {
        TestNatives.Require();
        using Scaler scaler = new();
        using Frame source = new();
        source.AllocateVideo(64, 64, PixelFormat.Yuv420P);
        using Frame destination = new();

        foreach ((int w, int h) in new[] { (32, 32), (128, 96), (32, 32) })
        {
            destination.Width = w;
            destination.Height = h;
            destination.PixelFormat = PixelFormat.Bgra;
            scaler.Scale(source, destination);

            Assert.AreEqual(w, destination.GetPlane(0).RowLength / 4);
            Assert.AreEqual(h, destination.GetPlane(0).Height);
            Assert.IsTrue(destination.IsWritable);
        }

        using Frame unset = new();
        _ = Assert.ThrowsExactly<ArgumentException>(() => scaler.Scale(source, unset));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => scaler.Scale(null!, destination));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => scaler.Scale(source, null!));
    }

    [TestMethod]
    public void Scale_DestinationColour_IsProducedAndUnsetFieldsFollowTheSource()
    {
        TestNatives.Require();
        using Scaler scaler = new();
        using Frame source = new();
        source.AllocateVideo(64, 64, PixelFormat.Yuv420P);
        source.GetWritablePlane(0).GetRow(0).Fill(16);
        source.ColorRange = ColorRange.Limited;
        source.ColorSpace = ColorSpace.Bt709;
        source.ColorPrimaries = ColorPrimaries.Bt709;
        using Frame destination = new();
        destination.Width = 64;
        destination.Height = 64;
        destination.PixelFormat = PixelFormat.Yuv420P;
        destination.ColorRange = ColorRange.Full;

        scaler.Scale(source, destination);

        Assert.AreEqual(
            0,
            destination.GetPlane(0).GetRow(0)[0],
            1,
            "video-range black is full-range 0"
        );
        Assert.AreEqual(ColorRange.Full, destination.ColorRange);
        Assert.AreEqual(ColorSpace.Bt709, destination.ColorSpace, "unset fields follow the source");
        Assert.AreEqual(ColorPrimaries.Bt709, destination.ColorPrimaries);
    }

    [TestMethod]
    public void Scaler_ReportsSupportedFormats()
    {
        TestNatives.Require();
        using Scaler scaler = new(
            new ScalerOptions
            {
                Algorithm = ScaleAlgorithm.Lanczos,
                FullChromaInterpolation = true,
                ThreadCount = 0,
            }
        );

        Assert.IsTrue(Scaler.IsSupportedInput(PixelFormat.Nv12));
        Assert.IsTrue(Scaler.IsSupportedOutput(PixelFormat.Bgra));
        Assert.IsFalse(Scaler.IsSupportedInput(PixelFormat.D3D11));
        unsafe
        {
            Assert.AreNotEqual(0u, scaler.NativePointer->flags);
        }
    }

    [TestMethod]
    public async Task Resample_InterleavedSpans_MatchesTheFFmpegTool()
    {
        TestNatives.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string sine = await TestMedia.RawSineAsync(cancellationToken);
        short[] input = ToSamples(await File.ReadAllBytesAsync(sine, cancellationToken));
        using Scratch scratch = new();
        string theirs = scratch["theirs.raw"];

        using Resampler resampler = new(
            ChannelLayout.Stereo,
            SampleFormat.S16,
            48000,
            ChannelLayout.Stereo,
            SampleFormat.S16,
            44100
        );
        short[] output = new short[resampler.GetMaxOutputSamples(input.Length / 2) * 2 + 4096];
        int written = resampler.Convert<short, short>(input, output);
        Assert.IsGreaterThanOrEqualTo(0L, resampler.Delay);
        written += resampler.Convert<short, short>([], output.AsSpan(written * 2));
        short[] ours = output[..(written * 2)];

        _ = await FFmpegCli.FFmpegAsync(
            cancellationToken,
            [
                "-f",
                "s16le",
                "-ar",
                "48000",
                "-ac",
                "2",
                "-i",
                sine,
                "-ar",
                "44100",
                "-f",
                "s16le",
                theirs,
            ]
        );
        short[] reference = ToSamples(await File.ReadAllBytesAsync(theirs, cancellationToken));

        Assert.IsTrue(
            Math.Abs(ours.Length - reference.Length) <= 2 * 64,
            $"{ours.Length} samples against the tool's {reference.Length}."
        );
        Assert.IsGreaterThan(
            60.0,
            SignalToNoise(reference, ours),
            "The resampled signal differs from the tool's."
        );
    }

    [TestMethod]
    public void Resample_Frames_ConvertsFormatLayoutAndRate()
    {
        TestNatives.Require();
        using Resampler resampler = new(
            ChannelLayout.Stereo,
            SampleFormat.S16,
            48000,
            ChannelLayout.Mono,
            SampleFormat.FloatPlanar,
            24000
        );
        using Frame input = new();
        input.AllocateAudio(960, SampleFormat.S16, ChannelLayout.Stereo, 48000);
        input.GetWritableSamples<short>().Fill(16384);
        using Frame output = new();

        resampler.Convert(input, output);
        int produced = output.SampleCount;
        resampler.Convert(null, output);
        produced += output.SampleCount;

        Assert.AreEqual(SampleFormat.FloatPlanar, output.SampleFormat);
        Assert.AreEqual(ChannelLayout.Mono, output.ChannelLayout);
        Assert.AreEqual(24000, output.SampleRate);
        Assert.IsTrue(Math.Abs(produced - 480) <= 32, $"{produced} samples.");
        Assert.AreEqual(24000, resampler.OutputRate);
        Assert.AreEqual(48000, resampler.InputRate);
        Assert.AreEqual(SampleFormat.S16, resampler.InputFormat);
        Assert.AreEqual(1, resampler.OutputChannels);
        Assert.AreEqual(2, resampler.InputChannels);
    }

    [TestMethod]
    public void Resample_SpansWithPlanarOrMismatchedTypes_Throw()
    {
        TestNatives.Require();
        using Resampler planar = new(
            ChannelLayout.Mono,
            SampleFormat.FloatPlanar,
            8000,
            ChannelLayout.Mono,
            SampleFormat.S16,
            8000
        );
        using Resampler packed = new(
            ChannelLayout.Mono,
            SampleFormat.S16,
            8000,
            ChannelLayout.Mono,
            SampleFormat.S16,
            8000
        );

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            planar.Convert<float, short>(new float[4], new short[4])
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            packed.Convert<int, short>(new int[4], new short[4])
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new Resampler(
                ChannelLayout.Mono,
                SampleFormat.S16,
                0,
                ChannelLayout.Mono,
                SampleFormat.S16,
                8000
            )
        );
        _ = Assert.ThrowsExactly<FFmpegException>(() =>
            new Resampler(
                ChannelLayout.Mono,
                SampleFormat.None,
                8000,
                ChannelLayout.Mono,
                SampleFormat.S16,
                8000
            )
        );
    }

    private static short[] ToSamples(byte[] bytes)
    {
        short[] samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    // The signal-to-noise ratio of one rendering of a signal against another, over their common length.
    private static double SignalToNoise(short[] reference, short[] candidate)
    {
        double signal = 0;
        double noise = 0;
        for (int i = 0; i < Math.Min(reference.Length, candidate.Length); i++)
        {
            signal += (double)reference[i] * reference[i];
            double difference = reference[i] - candidate[i];
            noise += difference * difference;
        }

        return noise == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / noise);
    }
}
