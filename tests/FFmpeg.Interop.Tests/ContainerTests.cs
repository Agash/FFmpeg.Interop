using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FFmpeg.Interop.Tests;

/// <summary>
/// Container work beyond reading packets in order: tags, chapters and dispositions as ffprobe reports
/// them, seeking to a keyframe, stream copy through any <see cref="Stream"/>, and cancelling a blocked
/// open.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class ContainerTests
{
    private const int Seconds = 3;
    private const int KeyframeInterval = 15;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MediaReader_ReadsTagsChaptersAndDisposition()
    {
        TestNatives.Require();
        string path = await TaggedClipAsync();

        using MediaReader reader = MediaReader.Open(path);

        Assert.AreEqual("StreamTransport test", reader.Metadata["title"]);
        Assert.HasCount(2, reader.Chapters);
        Assert.AreEqual("Opening", reader.Chapters[0].Title);
        Assert.AreEqual(TimeSpan.Zero, reader.Chapters[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(1), reader.Chapters[0].End);
        Assert.AreEqual("Rest", reader.Chapters[1].Title);
        Assert.AreEqual(TimeSpan.FromSeconds(1), reader.Chapters[1].Start);
        MediaStream video = reader.Streams.Single();
        Assert.AreEqual("eng", video.Metadata["language"]);
        Assert.IsTrue(video.Disposition.HasFlag(StreamDisposition.Default));
    }

    [TestMethod]
    public async Task MediaReader_Seek_LandsOnTheKeyframeAtOrBefore()
    {
        TestNatives.Require();
        string path = await LongClipAsync();
        using MediaReader reader = MediaReader.Open(path);
        MediaStream video = reader.Streams.Single();
        using Packet packet = new();

        reader.Seek(TimeSpan.FromSeconds(2.2), video);
        Assert.IsTrue(reader.TryReadPacket(packet));

        TimeSpan landed = video.TimeBase.ToTimeSpan(packet.PresentationTimestamp!.Value);
        Assert.IsTrue(packet.IsKeyFrame, "a seek lands on a keyframe");
        Assert.AreEqual(2.0, landed.TotalSeconds, 0.05, "the keyframe at or before 2.2 s");
    }

    [TestMethod]
    public async Task StreamCopy_ThroughStreams_KeepsEveryPacket()
    {
        TestNatives.Require();
        string path = await LongClipAsync();
        JsonElement probed = (
            await FFmpegCli.FFprobeAsync(
                TestContext.CancellationToken,
                "-count_packets",
                "-show_entries",
                "stream=nb_read_packets,codec_name",
                path
            )
        ).GetProperty("streams")[0];

        using MemoryStream copy = new();
        await using (FileStream source = File.OpenRead(path))
        {
            using MediaReader reader = MediaReader.OpenStream(source);
            using MediaWriter writer = MediaWriter.CreateStream(copy, "matroska");
            int stream = writer.AddStream(reader.Streams[0]);
            writer.WriteHeader();
            using Packet packet = new();
            while (reader.TryReadPacket(packet))
            {
                writer.Write(packet, stream);
            }

            writer.Complete();
        }

        copy.Position = 0;
        using MediaReader again = MediaReader.OpenStream(copy);
        using Packet read = new();
        int packets = 0;
        while (again.TryReadPacket(read))
        {
            packets++;
        }

        Assert.AreEqual(
            probed.GetProperty("codec_name").GetString(),
            again.Streams[0].CodecId.Name
        );
        Assert.AreEqual(probed.GetProperty("nb_read_packets").GetString(), $"{packets}");
    }

    [TestMethod]
    public void MediaReader_Open_BlockedInput_IsCancelled()
    {
        TestNatives.Require();

        // A server that accepts and never sends: the demuxer waits for its first bytes until cancelled.
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));

        _ = Assert.ThrowsExactly<OperationCanceledException>(() =>
            MediaReader.Open(
                $"tcp://127.0.0.1:{port}",
                format: "mpegts",
                cancellationToken: cancel.Token
            )
        );
    }

    // Three seconds of the synthetic picture, a keyframe every half second, in FFmpeg's own MPEG-4 Part 2
    // encoder so no external one is needed.
    private async Task<string> LongClipAsync()
    {
        string path = TestMedia.PathOf("long-mpeg4.mkv");
        if (!File.Exists(path))
        {
            _ = await FFmpegCli.FFmpegAsync(
                TestContext.CancellationToken,
                [
                    "-f",
                    "lavfi",
                    "-i",
                    $"testsrc2=size={TestMedia.Width}x{TestMedia.Height}:rate={TestMedia.FrameRate}",
                    "-frames:v",
                    $"{Seconds * TestMedia.FrameRate}",
                    "-c:v",
                    "mpeg4",
                    "-g",
                    $"{KeyframeInterval}",
                    path,
                ]
            );
        }

        return path;
    }

    // The long clip with a title, two chapters, a language and the default disposition, as ffmpeg writes them.
    private async Task<string> TaggedClipAsync()
    {
        string clip = await LongClipAsync();
        string path = TestMedia.PathOf("tagged.mkv");
        string metadata = TestMedia.PathOf("tagged.ffmetadata");
        await File.WriteAllTextAsync(
            metadata,
            """
            ;FFMETADATA1
            title=StreamTransport test

            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=1000
            title=Opening

            [CHAPTER]
            TIMEBASE=1/1000
            START=1000
            END=3000
            title=Rest
            """,
            TestContext.CancellationToken
        );
        _ = await FFmpegCli.FFmpegAsync(
            TestContext.CancellationToken,
            [
                "-i",
                clip,
                "-i",
                metadata,
                "-map",
                "0",
                "-map_metadata",
                "1",
                "-map_chapters",
                "1",
                "-c",
                "copy",
                "-metadata:s:v:0",
                "language=eng",
                "-disposition:v:0",
                "default",
                path,
            ]
        );
        return path;
    }
}
