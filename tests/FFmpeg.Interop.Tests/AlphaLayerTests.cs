namespace FFmpeg.Interop.Tests;

// HEVC with an auxiliary alpha layer, as VideoToolbox encodes it, decodes back with its alpha in software
// as Yuva420P. FFmpeg's VideoToolbox decoding takes the base layer only.
[TestClass]
[TestCategory("RequiresVideoToolbox")]
[OSCondition(OperatingSystems.OSX)]
public sealed class AlphaLayerTests
{
    private const int Width = 256;
    private const int Height = 128;

    [TestMethod]
    public void VideoToolboxAlphaLayer_DecodesBackTheAlphaInSoftware()
    {
        TestNatives.Require();
        List<Packet> packets = Encode();
        using Decoder decoder = Decoder.Create(Codec.FindDecoder(CodecId.Hevc));
        using Frame decoded = new();
        Frame? last = null;
        foreach (Packet packet in packets)
        {
            foreach (Frame frame in decoder.Decode(packet, decoded))
            {
                last?.Dispose();
                last = frame.Clone();
            }

            packet.Dispose();
        }

        foreach (Frame frame in decoder.Decode(null, decoded))
        {
            last?.Dispose();
            last = frame.Clone();
        }

        Assert.IsNotNull(last, "nothing decoded");
        using (last)
        {
            Assert.AreEqual(PixelFormat.Yuva420P, last.PixelFormat, "decoded with its alpha layer");
            ReadOnlySpan<byte> row = last.GetPlane(3).GetRow(Height / 2);
            for (int x = 8; x < Width - 8; x += 16)
            {
                Assert.AreEqual(Alpha(x), row[x], 12, $"alpha at {x}");
            }
        }
    }

    // A horizontal alpha ramp over grey.
    private static byte Alpha(int x) => (byte)(x * 255 / (Width - 1));

    private static List<Packet> Encode()
    {
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder("hevc_videotoolbox"),
            new VideoEncoderOptions
            {
                Width = Width,
                Height = Height,
                PixelFormat = PixelFormat.Ayuv,
                TimeBase = new Rational(1, 30),
                FrameRate = new Rational(30, 1),
                BitRate = 2_000_000,
                ColorRange = ColorRange.Limited,
                ColorSpace = ColorSpace.Bt709,
                MaxBFrames = 0,
                CodecOptions = new Dictionary<string, string> { ["alpha_quality"] = "0.9" },
            }
        );
        List<Packet> packets = [];
        using Frame frame = new();
        using Packet packet = new();
        for (int i = 0; i < 6; i++)
        {
            frame.AllocateVideo(Width, Height, PixelFormat.Ayuv);
            ImagePlane plane = frame.GetWritablePlane(0);
            for (int y = 0; y < Height; y++)
            {
                Span<byte> row = plane.GetRow(y);
                for (int x = 0; x < Width; x++)
                {
                    row[(x * 4) + 0] = Alpha(x);
                    row[(x * 4) + 1] = 128;
                    row[(x * 4) + 2] = 128;
                    row[(x * 4) + 3] = 128;
                }
            }

            frame.PresentationTimestamp = i;
            foreach (Packet encoded in encoder.Encode(frame, packet))
            {
                packets.Add(encoded.Clone());
            }
        }

        foreach (Packet encoded in encoder.Encode(null, packet))
        {
            packets.Add(encoded.Clone());
        }

        return packets;
    }
}
