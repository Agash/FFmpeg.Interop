namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class ColorTests
{
    [TestMethod]
    public void Frame_ColorProperties_RoundTrip()
    {
        TestNatives.Require();
        using Frame frame = new()
        {
            ColorRange = ColorRange.Full,
            ColorPrimaries = ColorPrimaries.Bt2020,
            ColorTransfer = ColorTransfer.Pq,
            ColorSpace = ColorSpace.Bt2020Ncl,
        };

        Assert.AreEqual(ColorRange.Full, frame.ColorRange);
        Assert.AreEqual(ColorPrimaries.Bt2020, frame.ColorPrimaries);
        Assert.AreEqual(ColorTransfer.Pq, frame.ColorTransfer);
        Assert.AreEqual(ColorSpace.Bt2020Ncl, frame.ColorSpace);
    }

    // The encoder writes the colour it was opened with into the bitstream (the AV1 sequence header's
    // colour config), and a decoder that knows nothing of the encoder reads it back.
    [TestMethod]
    [TestCategory("Integration")]
    public void Encoder_SignalledColor_IsWhatADecoderReads()
    {
        TestNatives.Require();
        string encoderName = Media.FirstEncoder(Media.Av1Encoders);
        const int width = 128;
        const int height = 96;
        using Encoder encoder = Encoder.Create(
            Codec.FindEncoder(encoderName),
            new VideoEncoderOptions
            {
                Width = width,
                Height = height,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new Rational(1, 30),
                ColorRange = ColorRange.Limited,
                ColorPrimaries = ColorPrimaries.Bt709,
                ColorTransfer = ColorTransfer.Bt709,
                ColorSpace = ColorSpace.Bt709,
            }
        );
        using Decoder decoder = Decoder.Create(Codec.FindDecoder("libdav1d"));
        using Frame input = new();
        using Packet packet = new();
        using Frame output = new();
        Frame? decoded = null;
        for (int i = 0; i < 8; i++)
        {
            input.AllocateVideo(width, height, PixelFormat.Yuv420P);
            for (int plane = 0; plane < 3; plane++)
            {
                ImagePlane pixels = input.GetWritablePlane(plane);
                for (int row = 0; row < pixels.Height; row++)
                {
                    pixels.GetRow(row).Fill((byte)(64 + (i * 8) + (plane * 16)));
                }
            }

            input.PresentationTimestamp = i;
            foreach (Packet encoded in encoder.Encode(input, packet))
            {
                foreach (Frame frame in decoder.Decode(encoded, output))
                {
                    decoded ??= frame.Clone();
                }
            }
        }

        foreach (Packet encoded in encoder.Encode(null, packet))
        {
            foreach (Frame frame in decoder.Decode(encoded, output))
            {
                decoded ??= frame.Clone();
            }
        }

        using Frame first = decoded ?? throw new AssertFailedException("Nothing was decoded.");
        Assert.AreEqual(ColorRange.Limited, first.ColorRange);
        Assert.AreEqual(ColorPrimaries.Bt709, first.ColorPrimaries);
        Assert.AreEqual(ColorTransfer.Bt709, first.ColorTransfer);
        Assert.AreEqual(ColorSpace.Bt709, first.ColorSpace);
    }
}
