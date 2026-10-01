using FFmpeg.Interop.Native;

namespace FFmpeg.Interop.Tests;

/// <summary>The send/receive contract and codec options, beyond the plain decode and encode loops.</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class CodecApiTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    // H.264 in MP4 is length-prefixed (AVCC) with the SPS and PPS out of band: without ExtraData the
    // decoder cannot parse a single packet.
    [TestMethod]
    public async Task Decoder_ExtraDataFromTheContainer_DecodesAvccPackets()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string raw = await TestMedia.RawVideoAsync("yuv420p", cancellationToken);
        using Scratch scratch = new();
        string mp4 = scratch["avcc.mp4"];
        _ = await FFmpegCli.FFmpegAsync(
            cancellationToken,
            [
                .. FFmpegCli.RawVideoInput(raw, "yuv420p", TestMedia.Width, TestMedia.Height),
                "-c:v",
                Media.FirstEncoder(Media.H264Encoders),
                mp4,
            ]
        );
        IReadOnlyList<string> expected = await FFmpegCli.FrameMd5sAsync(
            mp4,
            "h264",
            "yuv420p",
            cancellationToken
        );

        using MediaReader reader = MediaReader.Open(mp4);
        MediaStream stream = reader.Streams[0];
        byte[] extraData = stream.ExtraData.ToArray();
        Assert.IsGreaterThan(0, extraData.Length);
        Assert.AreEqual(1, extraData[0], "An avcC record starts with version 1.");

        using Decoder decoder = Decoder.Create(
            Codec.FindDecoder(CodecId.H264),
            new DecoderOptions
            {
                ExtraData = extraData,
                PacketTimeBase = stream.TimeBase,
                ThreadCount = 1,
            }
        );
        CollectionAssert.AreEqual(extraData, decoder.ExtraData.ToArray());

        List<string> actual = [];
        using Packet packet = new();
        using Frame frame = new();
        while (reader.TryReadPacket(packet))
        {
            foreach (Frame decoded in decoder.Decode(packet, frame))
            {
                actual.Add(Md5(decoded));
            }
        }

        foreach (Frame decoded in decoder.Decode(null, frame))
        {
            actual.Add(Md5(decoded));
        }

        CollectionAssert.AreEqual(expected.ToList(), actual);
        Assert.AreEqual(TestMedia.Width, decoder.Width);
        Assert.AreEqual(TestMedia.Height, decoder.Height);
        Assert.AreEqual(PixelFormat.Yuv420P, decoder.PixelFormat);
    }

    // Raw PCM has no header: the sample rate and channel layout can only come from the caller.
    [TestMethod]
    public void Decoder_RawPcmWithCallerSuppliedParameters_DecodesTheSamples()
    {
        using Decoder decoder = Decoder.Create(
            Codec.FindDecoder("pcm_s16le"),
            new DecoderOptions { SampleRate = 16000, ChannelLayout = ChannelLayout.Stereo }
        );
        Assert.AreEqual(16000, decoder.SampleRate);
        Assert.AreEqual(ChannelLayout.Stereo, decoder.ChannelLayout);
        Assert.AreEqual(SampleFormat.S16, decoder.SampleFormat);

        short[] samples = [1, -1, 2, -2, 3, -3];
        using Packet packet = new();
        packet.CopyFrom(System.Runtime.InteropServices.MemoryMarshal.AsBytes<short>(samples));
        using Frame frame = new();

        Assert.IsTrue(decoder.TrySend(packet));
        Assert.AreEqual(CodecStatus.Available, decoder.Receive(frame));
        CollectionAssert.AreEqual(samples, frame.GetSamples<short>().ToArray());
        Assert.AreEqual(3, frame.SampleCount);
        Assert.AreEqual(CodecStatus.NeedsInput, decoder.Receive(frame));
    }

    // A decoder that is sent packets without its output being received pushes back with EAGAIN, which
    // TrySend reports as false rather than an error.
    [TestMethod]
    public async Task Decoder_TrySendWithoutReceiving_EventuallyRefusesInput()
    {
        string path = await TestMedia.ClipAsync("h264", TestContext.CancellationToken);
        using MediaReader reader = MediaReader.Open(path);
        using Decoder decoder = reader
            .Streams[0]
            .CreateDecoder(options: new DecoderOptions { ThreadCount = 1 });
        using Packet packet = new();
        using Frame frame = new();

        bool refused = false;
        while (!refused && reader.TryReadPacket(packet))
        {
            refused = !decoder.TrySend(packet);
        }

        Assert.IsTrue(refused, "The decoder never pushed back.");
        int pending = 0;
        while (decoder.Receive(frame) == CodecStatus.Available)
        {
            pending++;
        }

        Assert.IsGreaterThanOrEqualTo(1, pending);
        Assert.IsTrue(
            decoder.TrySend(packet),
            "Once its output is drained the decoder takes the refused packet."
        );
    }

    [TestMethod]
    public void Encoder_EndOfStreamBeforeAnyFrame_FinishesWithNoPackets()
    {
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder(Media.FirstEncoder(Media.H264Encoders)),
            new VideoEncoderOptions
            {
                Width = 64,
                Height = 64,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 30),
                ThreadCount = 1,
            }
        );
        using Packet packet = new();

        int packets = 0;
        foreach (Packet _ in encoder.Encode(null, packet))
        {
            packets++;
        }

        Assert.AreEqual(0, packets);
        Assert.AreEqual(CodecStatus.EndOfStream, encoder.Receive(packet));
    }

    [TestMethod]
    public void Encoder_ManualSendAndReceive_ProducesPacketsThenEndOfStream()
    {
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder(Media.FirstEncoder(Media.H264Encoders)),
            new VideoEncoderOptions
            {
                Width = 64,
                Height = 64,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 30),
                MaxBFrames = 0,
                GopSize = 5,
                ThreadCount = 1,
            }
        );
        Assert.AreEqual(64, encoder.Width);
        Assert.AreEqual(64, encoder.Height);
        Assert.AreEqual(PixelFormat.Yuv420P, encoder.PixelFormat);
        Assert.AreEqual(new Rational(1, 30), encoder.TimeBase);
        Assert.AreEqual(0, encoder.FrameSize, "Video encoders take any frame.");

        using Frame frame = new();
        frame.AllocateVideo(64, 64, PixelFormat.Yuv420P);
        using Packet packet = new();
        int packets = 0;
        int keyFrames = 0;
        for (int i = 0; i < 10; i++)
        {
            frame.MakeWritable();
            frame.GetWritablePlane(0).GetRow(0).Fill((byte)(i * 20));
            frame.PresentationTimestamp = i;
            while (!encoder.TrySend(frame))
            {
                Assert.AreEqual(CodecStatus.Available, encoder.Receive(packet));
                packets++;
            }

            while (encoder.Receive(packet) == CodecStatus.Available)
            {
                packets++;
                keyFrames += packet.IsKeyFrame ? 1 : 0;
            }
        }

        encoder.SendEndOfStream();
        encoder.SendEndOfStream();
        CodecStatus status;
        // Encoders with lookahead (libx264) hand most packets back only while draining.
        while ((status = encoder.Receive(packet)) == CodecStatus.Available)
        {
            packets++;
            keyFrames += packet.IsKeyFrame ? 1 : 0;
        }

        Assert.AreEqual(CodecStatus.EndOfStream, status);
        Assert.AreEqual(10, packets);
        Assert.IsGreaterThanOrEqualTo(
            2,
            keyFrames,
            "A GOP of 5 over 10 frames starts at least two key frames."
        );
    }

    [TestMethod]
    public void Encoder_UnknownOptionOrBadSettings_FailClearly()
    {
        Codec encoder = Codec.FindEncoder(Media.FirstEncoder(Media.H264Encoders));
        VideoEncoderOptions options = new()
        {
            Width = 64,
            Height = 64,
            PixelFormat = PixelFormat.Yuv420P,
            TimeBase = new(1, 30),
        };

        ArgumentException unknown = Assert.ThrowsExactly<ArgumentException>(() =>
            Encoder.Create(
                encoder,
                options with
                {
                    CodecOptions = new Dictionary<string, string> { ["no_such_option"] = "1" },
                }
            )
        );
        Assert.Contains("no_such_option", unknown.Message);
        _ = Assert.ThrowsExactly<FFmpegException>(() =>
            Encoder.Create(encoder, options with { PixelFormat = PixelFormat.Rgb24 })
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            Encoder.Create(encoder, (VideoEncoderOptions)null!)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            Encoder.Create(encoder, (AudioEncoderOptions)null!)
        );
    }

    [TestMethod]
    public unsafe void NativeMemoryLease_Pin_UnpinsOnDispose()
    {
        using Packet packet = new();
        packet.CopyFrom([1, 2, 3]);
        using NativeMemoryLease lease = packet.LeaseData();

        using (System.Buffers.MemoryHandle handle = lease.Memory[1..].Pin())
        {
            Assert.AreEqual(2, *(byte*)handle.Pointer);
        }

        System.Buffers.MemoryManager<byte> manager = GetManager(lease);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            manager.Pin(lease.Memory.Length + 1)
        );
    }

    [TestMethod]
    public async Task AvioTell_ReportsTheReadPosition()
    {
        string path = await TestMedia.ClipAsync("h264", TestContext.CancellationToken);
        using MediaReader reader = MediaReader.Open(path);
        using Packet packet = new();
        _ = reader.TryReadPacket(packet);

        long position;
        unsafe
        {
            position = LibAVFormat.avio_tell(reader.NativePointer->pb);
        }

        Assert.IsGreaterThan(0L, position);
        Assert.IsLessThanOrEqualTo(new FileInfo(path).Length, position);
    }

    private static System.Buffers.MemoryManager<byte> GetManager(NativeMemoryLease lease)
    {
        Assert.IsTrue(
            System.Runtime.InteropServices.MemoryMarshal.TryGetMemoryManager(
                lease.Memory,
                out System.Buffers.MemoryManager<byte>? manager
            )
        );
        return manager!;
    }

    private static string Md5(Frame frame)
    {
        byte[] image = new byte[frame.GetImageSize()];
        _ = frame.CopyImageTo(image);
        return Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(image));
    }
}
