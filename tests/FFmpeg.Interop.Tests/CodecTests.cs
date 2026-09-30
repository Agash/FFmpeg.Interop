using System.Collections.Immutable;

namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class CodecTests
{
    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    public void FindDecoder_H264_DescribesFFmpegsNativeDecoder()
    {
        Codec codec = Codec.FindDecoder(CodecId.H264);

        Assert.AreEqual("h264", codec.Name);
        Assert.AreEqual("h264", codec.ToString());
        Assert.IsNotNull(codec.LongName);
        Assert.AreEqual(CodecId.H264, codec.Id);
        Assert.AreEqual(MediaType.Video, codec.MediaType);
        Assert.IsTrue(codec.IsDecoder);
        Assert.IsFalse(codec.IsEncoder);
        Assert.IsNull(codec.WrapperName);
        Assert.IsTrue(codec.Capabilities.HasFlag(CodecCapabilities.Delay));
        Assert.AreEqual(codec, Codec.FindDecoder("h264"));
        Assert.IsTrue(codec == Codec.FindDecoder("h264"));
        Assert.IsFalse(codec != Codec.FindDecoder("h264"));
        Assert.AreEqual(codec.GetHashCode(), Codec.FindDecoder("h264").GetHashCode());
        Assert.IsTrue(codec.Equals((object)Codec.FindDecoder("h264")));
        Assert.IsFalse(codec.Equals("h264"));
    }

    [TestMethod]
    public void WrappedCodecs_NameTheirLibrary()
    {
        Codec dav1d = Codec.FindDecoder("libdav1d");

        Assert.AreEqual("libdav1d", dav1d.WrapperName);
        Assert.AreEqual(CodecId.Av1, dav1d.Id);
    }

    [TestMethod]
    public void SupportedConfigs_AreReadFromTheCodecsTables()
    {
        Codec encoder = Codec.FindEncoder(Media.FirstEncoder(Media.H264Encoders));
        Codec aac = Codec.FindEncoder("aac");

        Assert.Contains(PixelFormat.Yuv420P, encoder.PixelFormats.ToArray());
        Assert.Contains(SampleFormat.FloatPlanar, aac.SampleFormats.ToArray());
        Assert.Contains(48000, aac.SampleRates.ToArray());
        Assert.IsTrue(Codec.FindDecoder(CodecId.H264).FrameRates.IsEmpty);
        Assert.IsTrue(
            Codec.FindEncoder("mpeg2video").FrameRates.ToArray().Contains(new Rational(30000, 1001))
        );
    }

    [TestMethod]
    public void HardwareConfigs_ListTheDeviceTypesADecoderCanUse()
    {
        ImmutableArray<HardwareConfig> configs = Codec.FindDecoder(CodecId.H264).HardwareConfigs;
        HardwareDeviceType expected = TestNatives.PlatformVideoApi;

        HardwareConfig config = configs.Single(c => c.DeviceType == expected);

        Assert.IsTrue(config.PixelFormat.IsHardware);
        Assert.IsTrue(config.Methods.HasFlag(HardwareConfigMethods.DeviceContext));
    }

    [TestMethod]
    public void All_EnumeratesEncodersAndDecoders()
    {
        List<Codec> all = [.. Codec.All];

        Assert.IsGreaterThan(100, all.Count);
        Assert.IsTrue(all.Any(c => c.Name == "libdav1d"));
        Assert.IsTrue(all.Any(c => c.IsEncoder && c.Id == CodecId.Av1));
    }

    [TestMethod]
    public void Find_UnknownNames_FailClearly()
    {
        Assert.IsFalse(Codec.TryFindDecoder("no-such-codec", out _));
        Assert.IsFalse(Codec.TryFindEncoder("no-such-codec", out _));
        Assert.IsFalse(Codec.TryFindDecoder(CodecId.None, out _));
        Assert.IsFalse(Codec.TryFindEncoder(CodecId.None, out _));
        _ = Assert.ThrowsExactly<NotSupportedException>(() => Codec.FindDecoder("no-such-codec"));
        _ = Assert.ThrowsExactly<NotSupportedException>(() => Codec.FindEncoder("no-such-codec"));
        _ = Assert.ThrowsExactly<NotSupportedException>(() => Codec.FindDecoder(CodecId.None));
        _ = Assert.ThrowsExactly<NotSupportedException>(() => Codec.FindEncoder(CodecId.None));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => Codec.TryFindDecoder(null!, out _));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => Codec.TryFindEncoder(null!, out _));
    }

    [TestMethod]
    public unsafe void Default_HasNoCodec()
    {
        Codec none = default;

        Assert.AreEqual("(none)", none.ToString());
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => (nint)none.NativePointer);
    }
}
