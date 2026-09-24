using System.Text.Json;

namespace FFmpeg.Interop.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class MediaIoTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MediaReader_DescribesStreamsAsFFprobeDoes()
    {
        TestNatives.Require();
        string path = await TestMedia.ClipAsync("h264", TestContext.CancellationToken);
        JsonElement probed = (
            await FFmpegCli.FFprobeAsync(
                TestContext.CancellationToken,
                "-show_streams",
                "-show_format",
                path
            )
        );
        JsonElement stream = probed.GetProperty("streams")[0];

        using MediaReader reader = MediaReader.Open(path);
        MediaStream video = reader.Streams.Single();

        Assert.AreEqual(0, video.Index);
        Assert.AreEqual(MediaType.Video, video.MediaType);
        Assert.AreEqual(stream.GetProperty("codec_name").GetString(), video.CodecId.Name);
        Assert.AreEqual(stream.GetProperty("width").GetInt32(), video.Width);
        Assert.AreEqual(stream.GetProperty("height").GetInt32(), video.Height);
        Assert.AreEqual(stream.GetProperty("time_base").GetString(), video.TimeBase.ToString());
        Assert.AreEqual(
            stream.GetProperty("avg_frame_rate").GetString(),
            video.FrameRate.ToString()
        );
        Assert.AreEqual(0, video.SampleRate);
        Assert.IsNull(reader.FindBestStream(MediaType.Audio));
        double duration = double.Parse(
            probed.GetProperty("format").GetProperty("duration").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture
        );
        Assert.AreEqual(duration, reader.Duration!.Value.TotalSeconds, 0.001);
    }

    [TestMethod]
    public async Task MediaReader_ReadsEveryPacketFFprobeCounts()
    {
        TestNatives.Require();
        string path = await TestMedia.ClipAsync("av1", TestContext.CancellationToken);
        JsonElement probed = (
            await FFmpegCli.FFprobeAsync(
                TestContext.CancellationToken,
                "-count_packets",
                "-show_entries",
                "stream=nb_read_packets",
                path
            )
        ).GetProperty("streams")[0];

        using MediaReader reader = MediaReader.Open(path);
        using Packet packet = new();
        int packets = 0;
        int keyFrames = 0;
        while (reader.TryReadPacket(packet))
        {
            Assert.AreEqual(reader.Streams[0].TimeBase, packet.TimeBase);
            Assert.IsNotNull(packet.PresentationTimestamp);
            packets++;
            keyFrames += packet.IsKeyFrame ? 1 : 0;
        }

        Assert.AreEqual(probed.GetProperty("nb_read_packets").GetString(), $"{packets}");
        Assert.IsGreaterThanOrEqualTo(1, keyFrames);
        Assert.IsFalse(reader.TryReadPacket(packet));
    }

    [TestMethod]
    public void MediaReader_Failures_AreReported()
    {
        TestNatives.Require();

        FFmpegException missing = Assert.ThrowsExactly<FFmpegException>(() =>
            MediaReader.Open(Path.Combine(Path.GetTempPath(), "no-such-file.mkv"))
        );
        Assert.AreEqual("avformat_open_input", missing.Operation);
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => MediaReader.Open(null!));
    }

    [TestMethod]
    public async Task MediaReader_UnknownOption_Throws()
    {
        TestNatives.Require();
        string path = await TestMedia.ClipAsync("h264", TestContext.CancellationToken);

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() =>
            MediaReader.Open(path, new Dictionary<string, string> { ["no_such_option"] = "1" })
        );

        Assert.Contains("no_such_option", error.Message);
    }

    [TestMethod]
    public void MediaWriter_MisuseOrder_Throws()
    {
        TestNatives.Require();
        using Scratch scratch = new();
        using MediaWriter writer = MediaWriter.Create(scratch["out.mkv"]);
        using Packet packet = new();
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder("rawvideo"),
            new VideoEncoderOptions
            {
                Width = 16,
                Height = 16,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 25),
            }
        );

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => writer.Write(packet, 0));
        _ = Assert.ThrowsExactly<InvalidOperationException>(writer.Complete);
        int stream = writer.AddStream(encoder);
        writer.WriteHeader();
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => writer.AddStream(encoder));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            writer.Write(packet, stream + 1)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
        {
            using MediaWriter other = MediaWriter.Create(scratch["other.mkv"]);
            _ = other.AddStream(encoder);
            other.WriteHeader(new Dictionary<string, string> { ["no_such_option"] = "1" });
        });
        _ = Assert.ThrowsExactly<FFmpegException>(() =>
            MediaWriter.Create(scratch["out.unknown-extension"])
        );
        using MediaWriter empty = MediaWriter.Create(scratch["empty.mkv"]);
        _ = Assert.ThrowsExactly<FFmpegException>(() => empty.WriteHeader());
    }
}
