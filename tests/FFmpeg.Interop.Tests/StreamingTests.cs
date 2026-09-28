using System.Collections.Concurrent;
using FFmpeg.Interop.Native;
using Microsoft.Extensions.Logging;

namespace FFmpeg.Interop.Tests;

/// <summary>What a real-time sender needs from an encoder and its inputs, on the CPU paths CI can run.</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class StreamingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    // A receiver that lost a packet asks for a key frame (RTCP PLI); the sender answers by marking the
    // next frame intra. With a long GOP, only the forced frames may be key frames.
    [TestMethod]
    public void PictureTypeI_ForcesAKeyFrameWhereAsked()
    {
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder(Media.FirstEncoder(Media.H264Encoders)),
            new VideoEncoderOptions
            {
                Width = 64,
                Height = 64,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 30),
                GopSize = 1000,
                MaxBFrames = 0,
                ThreadCount = 1,
                LowDelay = true,
            }
        );
        using Frame frame = new();
        using Packet packet = new();
        HashSet<long> keyFrames = [];
        for (int i = 0; i < 12; i++)
        {
            frame.AllocateVideo(64, 64, PixelFormat.Yuv420P);
            frame.GetWritablePlane(0).GetRow(i % 64).Fill(0xFF);
            frame.PresentationTimestamp = i;
            frame.PictureType = i is 5 or 9 ? PictureType.I : PictureType.None;
            foreach (Packet encoded in encoder.Encode(frame, packet))
            {
                if (encoded.IsKeyFrame)
                {
                    _ = keyFrames.Add(encoded.PresentationTimestamp!.Value);
                }
            }
        }

        foreach (Packet encoded in encoder.Encode(null, packet))
        {
            if (encoded.IsKeyFrame)
            {
                _ = keyFrames.Add(encoded.PresentationTimestamp!.Value);
            }
        }

        CollectionAssert.AreEquivalent(new long[] { 0, 5, 9 }, keyFrames.ToArray());
    }

    [TestMethod]
    public unsafe void RateControlOptions_ReachTheCodecContext()
    {
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder(Media.FirstEncoder(Media.H264Encoders)),
            new VideoEncoderOptions
            {
                Width = 64,
                Height = 64,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 30),
                BitRate = 500_000,
                MaxRate = 750_000,
                BufferSize = 250_000,
                LowDelay = true,
            }
        );

        Assert.AreEqual(500_000, encoder.BitRate);
        Assert.AreEqual(750_000, encoder.NativePointer->rc_max_rate);
        Assert.AreEqual(250_000, encoder.NativePointer->rc_buffer_size);
        Assert.AreNotEqual(0, encoder.NativePointer->flags & LibAVCodec.AV_CODEC_FLAG_LOW_DELAY);
    }

    // Encoders that read rate control only when opened must refuse a change, not accept it silently.
    [TestMethod]
    public void SetRateControl_OnAnEncoderThatReadsItOnce_Throws()
    {
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder(Media.FirstEncoder(Media.Av1Encoders)),
            new VideoEncoderOptions
            {
                Width = 64,
                Height = 64,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 30),
            }
        );

        Assert.IsFalse(encoder.SupportsRateControlChanges);
        _ = Assert.ThrowsExactly<NotSupportedException>(() => encoder.SetRateControl(1_000_000));
    }

    [TestMethod]
    public void SetRateControl_Arguments_AreChecked()
    {
        if (!Codec.TryFindEncoder("libx264", out Codec x264))
        {
            // Only the NVENC encoders and libx264 reconfigure; the pinned LGPL builds have neither, so
            // the change itself is exercised by the NVIDIA suite.
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                Encoder
                    .Create(Codec.FindEncoder(Media.FirstEncoder(Media.H264Encoders)), Options())
                    .SetRateControl(0)
            );
            return;
        }

        using Encoder encoder = Encoder.Create(x264, Options());
        Assert.IsTrue(encoder.SupportsRateControlChanges);
        encoder.SetRateControl(300_000, 400_000, 200_000);
        Assert.AreEqual(300_000, encoder.BitRate);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            encoder.SetRateControl(300_000, maxRate: 100_000)
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            encoder.SetRateControl(300_000, bufferSize: 0)
        );

        static VideoEncoderOptions Options() =>
            new()
            {
                Width = 64,
                Height = 64,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 30),
                BitRate = 500_000,
            };
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void DrmPrimeImage_BuildsTheDescriptorFFmpegMapsFrom()
    {
        DrmPrimeImage image = new(
            [new DrmObject(17, 4096 * 3 / 2, 0)],
            [new DrmLayer(0x3231564E, [new DrmPlane(0, 0, 64), new DrmPlane(0, 4096, 64)])]
        );

        using Frame frame = Frame.FromDrmPrime(image, 64, 64);

        Assert.AreEqual(PixelFormat.DrmPrime, frame.PixelFormat);
        Assert.IsTrue(frame.HasData);
        unsafe
        {
            DrmFrameDescriptor view = new((AVDRMFrameDescriptor*)frame.NativePointer->data[0]);
            Assert.AreEqual(new DrmObject(17, 6144, 0), view.GetObject(0));
            Assert.AreEqual(0x3231564Eu, view.GetLayerFormat(0));
            Assert.AreEqual(new DrmPlane(0, 4096, 64), view.GetPlane(0, 1));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void DrmPrimeImage_OutsideTheDescriptorsLimits_IsRefused()
    {
        DrmObject dmaBuf = new(3, 100, 0);
        DrmLayer layer = new(1, [new DrmPlane(0, 0, 16)]);

        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            Frame.FromDrmPrime(new([], [layer]), 16, 16)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            Frame.FromDrmPrime(new([.. Enumerable.Repeat(dmaBuf, 5)], [layer]), 16, 16)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            Frame.FromDrmPrime(new([dmaBuf], [new DrmLayer(1, [])]), 16, 16)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            Frame.FromDrmPrime(new([dmaBuf], [new DrmLayer(1, [new DrmPlane(1, 0, 16)])]), 16, 16)
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            Frame.FromDrmPrime(new([dmaBuf], [layer]), 0, 16)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => Frame.FromDrmPrime(null!, 16, 16));
    }

    // lavfi is libavdevice's filter graph source: the capture-device path with no device behind it.
    [TestMethod]
    public void OpenDevice_Lavfi_ReadsTheGeneratedPictures()
    {
        using MediaReader reader = MediaReader.OpenDevice(
            "lavfi",
            "testsrc2=size=64x48:rate=10:duration=1"
        );
        MediaStream video = reader.FindBestStream(MediaType.Video)!;
        using Decoder decoder = video.CreateDecoder();
        using Packet packet = new();
        using Frame frame = new();
        int frames = 0;
        while (reader.TryReadPacket(packet))
        {
            foreach (Frame decoded in decoder.Decode(packet, frame))
            {
                Assert.AreEqual(64, decoded.Width);
                frames++;
            }
        }

        Assert.AreEqual(10, frames);
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            MediaReader.OpenDevice("no-such-format", "x")
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => MediaReader.OpenDevice(null!, "x"));
    }

    [TestMethod]
    public async Task Open_WithAnExplicitFormat_ReadsARawElementaryStream()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using Scratch scratch = new();
        string raw = scratch["stream.h264"];
        _ = await FFmpegCli.FFmpegAsync(
            cancellationToken,
            "-i",
            await TestMedia.ClipAsync("h264", cancellationToken),
            "-c",
            "copy",
            "-bsf:v",
            "h264_mp4toannexb",
            "-f",
            "h264",
            raw
        );

        using MediaReader reader = MediaReader.Open(raw, format: "h264");

        Assert.AreEqual(CodecId.H264, reader.Streams[0].CodecId);
    }

    [TestMethod]
    [DataRow(Rounding.Zero, -1)]
    [DataRow(Rounding.Infinity, -2)]
    [DataRow(Rounding.Down, -2)]
    [DataRow(Rounding.Up, -1)]
    [DataRow(Rounding.NearInfinity, -2)]
    public void Rescale_WithRounding_FollowsTheMode(Rounding rounding, long expected) =>
        // -1.5 units: 3 in 1/2 converted to 1/1.
        Assert.AreEqual(expected, Rational.Rescale(-3, new(1, 2), new(1, 1), rounding));

    [TestMethod]
    public void VersionInfo_NamesTheLoadedBuild() =>
        Assert.IsFalse(string.IsNullOrWhiteSpace(FFmpegLibraries.VersionInfo));

    [TestMethod]
    public unsafe void Logging_RoutesFFmpegsLogToILoggerWithStructuredComponent()
    {
        CapturingLoggerFactory factory = new();
        FFmpegLogLevel previous = FFmpegLogging.Level;
        try
        {
            FFmpegLogging.UseLoggerFactory(factory);
            FFmpegLogging.Level = FFmpegLogLevel.Info;
            Assert.AreEqual(FFmpegLogLevel.Info, FFmpegLogging.Level);

            // av_dump_format writes each stream line in several calls: this checks the reassembly.
            using MediaReader reader = MediaReader.OpenDevice(
                "lavfi",
                "testsrc2=size=64x48:rate=10:duration=1"
            );
            using Utf8String name = new("lavfi");
            LibAVFormat.av_dump_format(reader.NativePointer, 0, name, 0);

            // A decoder fed garbage logs an error under its own class, naming itself.
            using Decoder decoder = Decoder.Create(Codec.FindDecoder(CodecId.H264));
            using Packet packet = new();
            packet.CopyFrom([0, 0, 0, 1, 0x65, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            _ = decoder.TrySend(packet);
        }
        finally
        {
            FFmpegLogging.UseLoggerFactory(null);
            FFmpegLogging.Level = previous;
        }

        Assert.IsTrue(
            factory.Entries.Any(e =>
                e.Message.Contains("Stream #0:0", StringComparison.Ordinal)
                && e.Message.Contains("64x48", StringComparison.Ordinal)
            ),
            "The stream line arrives whole: "
                + string.Join(" | ", factory.Entries.Select(e => e.Message))
        );
        (string Category, LogLevel Level, string? Component, string Message) decoderError =
            factory.Entries.First(e =>
                e.Category == "FFmpeg.AVCodecContext" && e.Level >= LogLevel.Warning
            );
        Assert.AreEqual("h264", decoderError.Component);
        Assert.IsFalse(decoderError.Message.EndsWith('\n'));
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<(
            string Category,
            LogLevel Level,
            string? Component,
            string Message
        )> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, Entries);

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<(
            string Category,
            LogLevel Level,
            string? Component,
            string Message
        )> entries
    ) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            string? component = state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(v => v.Key == "Component").Value as string
                : null;
            string message = state is IReadOnlyList<KeyValuePair<string, object?>> fields
                ? fields.First(v => v.Key == "Message").Value as string ?? string.Empty
                : formatter(state, exception);
            entries.Enqueue((category, logLevel, component, message));
        }
    }
}
