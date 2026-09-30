using System.Security.Cryptography;
using System.Text.Json;

namespace FFmpeg.Interop.Tests;

/// <summary>
/// Decoding and encoding through the managed API, checked against the ffmpeg and ffprobe tools: the
/// tools and the library load the same FFmpeg, so the same decoder must produce the same frames bit for
/// bit, and what the library writes must be what ffprobe reads back.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class CodingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("h264", "h264", "yuv420p")]
    [DataRow("hevc", "hevc", "yuv420p")]
    [DataRow("av1", "libdav1d", "yuv420p")]
    [DataRow("av1", "libaom-av1", "yuv420p")]
    [DataRow("av1-10bit", "libdav1d", "yuv420p10le")]
    public async Task Decode_Clip_MatchesFFmpegFrameMd5s(
        string clip,
        string decoder,
        string pixelFormat
    )
    {
        TestNatives.Require();
        TestNatives.RequireDecoder(decoder);
        string path = await TestMedia.ClipAsync(clip, TestContext.CancellationToken);
        IReadOnlyList<string> expected = await FFmpegCli.FrameMd5sAsync(
            path,
            decoder,
            pixelFormat,
            TestContext.CancellationToken
        );

        List<string> actual = DecodeFrameMd5s(path, decoder, PixelFormat.Parse(pixelFormat));

        Assert.HasCount(TestMedia.FrameCount, expected);
        CollectionAssert.AreEqual(expected.ToList(), actual);
    }

    // The raw bindings, driven the way C code would drive them, must agree with the tool too: this is
    // what checks the generated structs and signatures independently of the managed layer.
    [TestMethod]
    [DataRow("h264", "h264", "yuv420p")]
    [DataRow("av1-10bit", "libdav1d", "yuv420p10le")]
    public async Task DecodeWithRawBindings_Clip_MatchesFFmpegFrameMd5s(
        string clip,
        string decoder,
        string pixelFormat
    )
    {
        TestNatives.Require();
        TestNatives.RequireDecoder(decoder);
        string path = await TestMedia.ClipAsync(clip, TestContext.CancellationToken);
        IReadOnlyList<string> expected = await FFmpegCli.FrameMd5sAsync(
            path,
            decoder,
            pixelFormat,
            TestContext.CancellationToken
        );

        List<string> actual = Media.DecodeFrameMd5s(path, decoder, PixelFormat.Parse(pixelFormat));

        CollectionAssert.AreEqual(expected.ToList(), actual);
    }

    [TestMethod]
    [DataRow("h264", "mp4")]
    [DataRow("av1", "mkv")]
    public async Task Encode_RawFrames_WritesAStreamFFprobeReadsBack(
        string family,
        string container
    )
    {
        TestNatives.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string raw = await TestMedia.RawVideoAsync("yuv420p", cancellationToken);
        string encoderName = Media.FirstEncoder(
            family == "h264" ? Media.H264Encoders : Media.Av1Encoders
        );
        using Scratch scratch = new();
        string output = scratch[$"encoded.{container}"];

        int packets = EncodeRawVideo(
            await File.ReadAllBytesAsync(raw, cancellationToken),
            encoderName,
            output
        );

        JsonElement stream = (
            await FFmpegCli.FFprobeAsync(
                cancellationToken,
                "-count_frames",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=codec_name,width,height,pix_fmt,nb_read_frames",
                output
            )
        ).GetProperty("streams")[0];
        Assert.AreEqual(family, stream.GetProperty("codec_name").GetString());
        Assert.AreEqual(TestMedia.Width, stream.GetProperty("width").GetInt32());
        Assert.AreEqual(TestMedia.Height, stream.GetProperty("height").GetInt32());
        Assert.AreEqual("yuv420p", stream.GetProperty("pix_fmt").GetString());
        Assert.AreEqual(
            $"{TestMedia.FrameCount}",
            stream.GetProperty("nb_read_frames").GetString()
        );
        Assert.IsGreaterThanOrEqualTo(1, packets);

        double psnr = await FFmpegCli.PsnrAsync(
            ["-i", output],
            FFmpegCli.RawVideoInput(raw, "yuv420p", TestMedia.Width, TestMedia.Height),
            cancellationToken
        );
        // Separates "encoded the pictures it was given" from wrong input: the fastest AV1 presets reach about
        // 30 dB on this detailed synthetic source, while shifted, swapped-plane or stale frames score 21 dB or less.
        Assert.IsGreaterThan(
            26.0,
            psnr,
            $"PSNR {psnr:F1} dB: the encoder did not encode the picture it was given."
        );

        string decoder = family == "h264" ? "h264" : "libdav1d";
        IReadOnlyList<string> expected = await FFmpegCli.FrameMd5sAsync(
            output,
            decoder,
            "yuv420p",
            cancellationToken
        );
        CollectionAssert.AreEqual(
            expected.ToList(),
            DecodeFrameMd5s(output, decoder, PixelFormat.Yuv420P)
        );
    }

    [TestMethod]
    public async Task EncodeAudio_Sine_WritesAacFFprobeReadsBack()
    {
        TestNatives.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        short[] interleaved = ReadSamples(await TestMedia.RawSineAsync(cancellationToken));
        using Scratch scratch = new();
        string output = scratch["sine.m4a"];

        int decodedSamples;
        using (MediaWriter writer = MediaWriter.Create(output))
        {
            Codec aac = Codec.FindEncoder("aac");
            using Encoder encoder = Encoder.Create(
                aac,
                new AudioEncoderOptions
                {
                    SampleRate = TestMedia.SampleRate,
                    SampleFormat = SampleFormat.FloatPlanar,
                    ChannelLayout = ChannelLayout.Stereo,
                    BitRate = 128_000,
                    GlobalHeader = writer.RequiresGlobalHeader,
                }
            );
            Assert.IsGreaterThan(0, encoder.FrameSize);
            int stream = writer.AddStream(encoder);
            writer.WriteHeader();

            using Frame frame = new();
            using Packet packet = new();
            int total = interleaved.Length / 2;
            for (int start = 0; start < total; start += encoder.FrameSize)
            {
                int count = Math.Min(encoder.FrameSize, total - start);
                frame.AllocateAudio(
                    count,
                    SampleFormat.FloatPlanar,
                    ChannelLayout.Stereo,
                    TestMedia.SampleRate
                );
                Span<float> l = frame.GetWritableSamples<float>(0);
                Span<float> r = frame.GetWritableSamples<float>(1);
                for (int i = 0; i < count; i++)
                {
                    l[i] = interleaved[2 * (start + i)] / 32768f;
                    r[i] = interleaved[(2 * (start + i)) + 1] / 32768f;
                }

                frame.PresentationTimestamp = start;
                foreach (Packet encoded in encoder.Encode(frame, packet))
                {
                    writer.Write(encoded, stream);
                }
            }

            foreach (Packet encoded in encoder.Encode(null, packet))
            {
                writer.Write(encoded, stream);
            }

            writer.Complete();
        }

        using (MediaReader reader = MediaReader.Open(output))
        {
            MediaStream audio = reader.FindBestStream(MediaType.Audio)!;
            Assert.AreEqual(CodecId.Aac, audio.CodecId);
            Assert.AreEqual(TestMedia.SampleRate, audio.SampleRate);
            Assert.IsFalse(audio.ExtraData.IsEmpty, "MP4 carries the AAC config out of band.");
            decodedSamples = DecodeSampleCount(reader, audio);
        }

        JsonElement probed = (
            await FFmpegCli.FFprobeAsync(
                cancellationToken,
                "-show_entries",
                "stream=codec_name,sample_rate,channels",
                output
            )
        ).GetProperty("streams")[0];
        Assert.AreEqual("aac", probed.GetProperty("codec_name").GetString());
        Assert.AreEqual("48000", probed.GetProperty("sample_rate").GetString());
        Assert.AreEqual(2, probed.GetProperty("channels").GetInt32());

        // AAC works in 1024-sample frames and adds a priming delay, so the decoded length is a whole
        // number of frames close to, not equal to, the input.
        Assert.IsTrue(
            Math.Abs(decodedSamples - TestMedia.SampleRate) <= 2048,
            $"Decoded {decodedSamples} samples."
        );
    }

    [TestMethod]
    public unsafe void Decoder_LowDelay_SetsTheLowDelayFlag()
    {
        TestNatives.Require();

        using Decoder lowDelay = Decoder.Create(
            Codec.FindDecoder(CodecId.H264),
            new DecoderOptions { LowDelay = true }
        );
        using Decoder buffered = Decoder.Create(Codec.FindDecoder(CodecId.H264));

        Assert.AreNotEqual(
            0,
            lowDelay.NativePointer->flags & Native.LibAVCodec.AV_CODEC_FLAG_LOW_DELAY
        );
        Assert.AreEqual(
            0,
            buffered.NativePointer->flags & Native.LibAVCodec.AV_CODEC_FLAG_LOW_DELAY
        );
    }

    [TestMethod]
    public void Decoder_UnknownOption_Throws()
    {
        TestNatives.Require();

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() =>
            Decoder.Create(
                Codec.FindDecoder(CodecId.H264),
                new DecoderOptions
                {
                    CodecOptions = new Dictionary<string, string> { ["no_such_option"] = "1" },
                }
            )
        );

        Assert.Contains("no_such_option", error.Message);
    }

    [TestMethod]
    public void Encoder_WrongMediaType_Throws()
    {
        TestNatives.Require();

        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            Encoder.Create(
                Codec.FindEncoder("aac"),
                new VideoEncoderOptions
                {
                    Width = 16,
                    Height = 16,
                    PixelFormat = PixelFormat.Yuv420P,
                    TimeBase = new(1, 30),
                }
            )
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() => Decoder.Create(Codec.FindEncoder("aac")));
    }

    [TestMethod]
    public void Decoder_TrySendAndReceive_ReportsNeedsInputThenEndOfStream()
    {
        TestNatives.Require();
        using Decoder decoder = Decoder.Create(Codec.FindDecoder(CodecId.H264));
        using Frame frame = new();

        Assert.AreEqual(CodecStatus.NeedsInput, decoder.Receive(frame));
        decoder.SendEndOfStream();
        decoder.SendEndOfStream();
        Assert.AreEqual(CodecStatus.EndOfStream, decoder.Receive(frame));

        decoder.Flush();
        Assert.AreEqual(CodecStatus.NeedsInput, decoder.Receive(frame));
    }

    [TestMethod]
    public void Decoder_CorruptPacket_ThrowsFFmpegException()
    {
        TestNatives.Require();
        using Decoder decoder = Decoder.Create(
            Codec.FindDecoder(CodecId.Av1),
            new DecoderOptions
            {
                CodecOptions = new Dictionary<string, string> { ["err_detect"] = "explode" },
            }
        );
        using Packet packet = new();
        packet.CopyFrom([0x12, 0x00, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        FFmpegException error = Assert.ThrowsExactly<FFmpegException>(() =>
        {
            decoder.TrySend(packet);
            decoder.SendEndOfStream();
            using Frame frame = new();
            while (decoder.Receive(frame) == CodecStatus.Available) { }
        });

        Assert.IsLessThan(0, error.ErrorCode);
        Assert.IsNotNull(error.Operation);
    }

    internal static List<string> DecodeFrameMd5s(
        string path,
        string decoderName,
        PixelFormat expectedFormat
    )
    {
        using MediaReader reader = MediaReader.Open(path);
        MediaStream stream = reader.FindBestStream(MediaType.Video)!;
        using Decoder decoder = stream.CreateDecoder(Codec.FindDecoder(decoderName));
        using Packet packet = new();
        using Frame frame = new();
        List<string> md5s = [];
        while (reader.TryReadPacket(packet))
        {
            if (packet.StreamIndex != stream.Index)
            {
                continue;
            }

            foreach (Frame decoded in decoder.Decode(packet, frame))
            {
                md5s.Add(Md5(decoded, expectedFormat));
            }
        }

        foreach (Frame decoded in decoder.Decode(null, frame))
        {
            md5s.Add(Md5(decoded, expectedFormat));
        }

        return md5s;
    }

    private static string Md5(Frame frame, PixelFormat expectedFormat)
    {
        Assert.AreEqual(expectedFormat, frame.PixelFormat);
        byte[] image = new byte[frame.GetImageSize()];
        Assert.AreEqual(image.Length, frame.CopyImageTo(image));
        return Convert.ToHexStringLower(MD5.HashData(image));
    }

    private static int EncodeRawVideo(byte[] video, string encoderName, string output)
    {
        int frameSize = TestMedia.Width * TestMedia.Height * 3 / 2;
        using MediaWriter writer = MediaWriter.Create(output);
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder(encoderName),
            new VideoEncoderOptions
            {
                Width = TestMedia.Width,
                Height = TestMedia.Height,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, TestMedia.FrameRate),
                FrameRate = new(TestMedia.FrameRate, 1),
                BitRate = 2_000_000,
                GopSize = 10,
                GlobalHeader = writer.RequiresGlobalHeader,
                CodecOptions = SpeedOptions(encoderName),
            }
        );
        Assert.AreEqual(writer.RequiresGlobalHeader, !encoder.ExtraData.IsEmpty);
        int stream = writer.AddStream(encoder);
        writer.WriteHeader();

        int packets = 0;
        using Packet packet = new();
        for (int i = 0; i < TestMedia.FrameCount; i++)
        {
            // The raw frames are handed to the encoder in place; the array stays pinned for as long as
            // the encoder holds each frame.
            using Frame frame = Frame.WrapImage(
                video.AsMemory(i * frameSize, frameSize),
                TestMedia.Width,
                TestMedia.Height,
                PixelFormat.Yuv420P
            );
            frame.PresentationTimestamp = i;
            foreach (Packet encoded in encoder.Encode(frame, packet))
            {
                Assert.AreEqual(encoder.TimeBase, encoded.TimeBase);
                writer.Write(encoded, stream);
                packets++;
            }
        }

        foreach (Packet encoded in encoder.Encode(null, packet))
        {
            writer.Write(encoded, stream);
            packets++;
        }

        writer.Complete();
        return packets;
    }

    private static Dictionary<string, string>? SpeedOptions(string encoder) =>
        encoder switch
        {
            "libsvtav1" => new() { ["preset"] = "12" },
            "libaom-av1" => new() { ["cpu-used"] = "8" },
            "libx264" => new() { ["preset"] = "ultrafast" },
            _ => null,
        };

    private static short[] ReadSamples(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        short[] samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    private static int DecodeSampleCount(MediaReader reader, MediaStream stream)
    {
        using Decoder decoder = stream.CreateDecoder();
        using Packet packet = new();
        using Frame frame = new();
        int samples = 0;
        while (reader.TryReadPacket(packet))
        {
            foreach (Frame decoded in decoder.Decode(packet, frame))
            {
                Assert.AreEqual(2, decoded.ChannelLayout.ChannelCount);
                samples += decoded.SampleCount;
            }
        }

        foreach (Frame decoded in decoder.Decode(null, frame))
        {
            samples += decoded.SampleCount;
        }

        return samples;
    }
}
