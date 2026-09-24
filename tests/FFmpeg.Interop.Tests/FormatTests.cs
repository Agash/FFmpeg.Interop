using FFmpeg.Interop.Native;

namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class FormatTests
{
    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    // Codec exposes FFmpeg's configuration tables in place as spans of these types, which is only sound
    // while each has exactly the layout of the native element.
    [TestMethod]
    public unsafe void ManagedValueTypes_HaveTheNativeLayout()
    {
        Assert.AreEqual(sizeof(AVPixelFormat), sizeof(PixelFormat));
        Assert.AreEqual(sizeof(AVSampleFormat), sizeof(SampleFormat));
        Assert.AreEqual(sizeof(AVRational), sizeof(Rational));
        Rational value = new(3, 7);
        AVRational native = *(AVRational*)&value;
        Assert.AreEqual(3, native.num);
        Assert.AreEqual(7, native.den);
    }

    [TestMethod]
    public unsafe void PixelFormat_ReportsFFmpegsDescriptor()
    {
        Assert.AreEqual("yuv420p", PixelFormat.Yuv420P.Name);
        Assert.AreEqual("yuv420p", PixelFormat.Yuv420P.ToString());
        Assert.IsTrue(PixelFormat.Yuv420P.IsPlanar);
        Assert.IsFalse(PixelFormat.Yuv420P.IsRgb);
        Assert.AreEqual(1, PixelFormat.Yuv420P.ChromaShiftX);
        Assert.AreEqual(1, PixelFormat.Yuv420P.ChromaShiftY);
        Assert.AreEqual(2, PixelFormat.Nv12.PlaneCount);
        Assert.IsTrue(PixelFormat.Rgba.HasAlpha);
        Assert.IsTrue(PixelFormat.Bgra.IsRgb);
        Assert.IsTrue(PixelFormat.Vulkan.IsHardware);
        Assert.IsFalse(PixelFormat.P010.IsHardware);
        Assert.AreEqual("yuv420p10le", PixelFormat.Yuv420P10.Name);

        Assert.IsNull(PixelFormat.None.Name);
        Assert.AreEqual(0, PixelFormat.None.PlaneCount);
        Assert.AreEqual(0, PixelFormat.None.ChromaShiftX);
        Assert.AreEqual(0, PixelFormat.None.ChromaShiftY);
        Assert.IsTrue(PixelFormat.None.Descriptor is null);
        Assert.IsFalse(PixelFormat.None.IsHardware);
        Assert.AreEqual(nameof(AVPixelFormat.AV_PIX_FMT_NONE), PixelFormat.None.ToString());
    }

    [TestMethod]
    public void PixelFormat_ParsesFFmpegNames()
    {
        Assert.AreEqual(PixelFormat.Nv12, PixelFormat.Parse("nv12"));
        Assert.IsTrue(PixelFormat.TryParse("d3d11", out PixelFormat d3d11));
        Assert.AreEqual(PixelFormat.D3D11, d3d11);
        Assert.IsFalse(PixelFormat.TryParse("not-a-format", out _));
        _ = Assert.ThrowsExactly<ArgumentException>(() => PixelFormat.Parse("not-a-format"));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => PixelFormat.TryParse(null!, out _));

        AVPixelFormat native = PixelFormat.Cuda;
        Assert.AreEqual(PixelFormat.Cuda, (PixelFormat)native);
        Assert.AreEqual("videotoolbox_vld", PixelFormat.VideoToolbox.Name);
        Assert.AreEqual("drm_prime", PixelFormat.DrmPrime.Name);
        Assert.AreEqual("vaapi", PixelFormat.Vaapi.Name);
    }

    [TestMethod]
    public void SampleFormat_ReportsFFmpegsFacts()
    {
        Assert.AreEqual("s16", SampleFormat.S16.Name);
        Assert.AreEqual(2, SampleFormat.S16.BytesPerSample);
        Assert.IsFalse(SampleFormat.S16.IsPlanar);
        Assert.AreEqual(SampleFormat.S16Planar, SampleFormat.S16.ToPlanar());
        Assert.AreEqual(SampleFormat.Float, SampleFormat.FloatPlanar.ToInterleaved());
        Assert.AreEqual(4, SampleFormat.S32Planar.BytesPerSample);
        Assert.AreEqual(8, SampleFormat.DoublePlanar.BytesPerSample);
        Assert.AreEqual(8, SampleFormat.Double.BytesPerSample);
        Assert.AreEqual(1, SampleFormat.U8.BytesPerSample);
        Assert.AreEqual(4, SampleFormat.S32.BytesPerSample);
        Assert.AreEqual(0, SampleFormat.None.BytesPerSample);
        Assert.IsNull(SampleFormat.None.Name);
        Assert.AreEqual("fltp", SampleFormat.FloatPlanar.ToString());
        Assert.AreEqual(nameof(AVSampleFormat.AV_SAMPLE_FMT_NONE), SampleFormat.None.ToString());

        Assert.IsTrue(SampleFormat.TryParse("dbl", out SampleFormat parsed));
        Assert.AreEqual(SampleFormat.Double, parsed);
        Assert.IsFalse(SampleFormat.TryParse("nope", out _));
        AVSampleFormat native = SampleFormat.S16;
        Assert.AreEqual(SampleFormat.S16, (SampleFormat)native);
    }

    [TestMethod]
    public void CodecId_ReportsNameAndMediaType()
    {
        Assert.AreEqual("h264", CodecId.H264.Name);
        Assert.AreEqual("hevc", CodecId.Hevc.ToString());
        Assert.AreEqual(MediaType.Video, CodecId.Av1.MediaType);
        Assert.AreEqual(MediaType.Video, CodecId.Vp8.MediaType);
        Assert.AreEqual(MediaType.Video, CodecId.Vp9.MediaType);
        Assert.AreEqual(MediaType.Audio, CodecId.Opus.MediaType);
        Assert.AreEqual(MediaType.Audio, CodecId.PcmS16LE.MediaType);
        Assert.AreEqual(MediaType.Unknown, CodecId.None.MediaType);
        AVCodecID native = CodecId.Aac;
        Assert.AreEqual(CodecId.Aac, (CodecId)native);
    }

    [TestMethod]
    public void HardwareDeviceType_RoundTripsNames()
    {
        foreach (HardwareDeviceType type in HardwareDeviceType.Compiled)
        {
            Assert.IsTrue(HardwareDeviceType.TryParse(type.Name!, out HardwareDeviceType parsed));
            Assert.AreEqual(type, parsed);
        }

        Assert.AreEqual("d3d11va", HardwareDeviceType.D3D11VA.Name);
        Assert.AreEqual("d3d12va", HardwareDeviceType.D3D12VA.ToString());
        Assert.AreEqual("videotoolbox", HardwareDeviceType.VideoToolbox.Name);
        Assert.AreEqual("qsv", HardwareDeviceType.Qsv.Name);
        Assert.AreEqual("amf", HardwareDeviceType.Amf.Name);
        Assert.AreEqual("opencl", HardwareDeviceType.OpenCL.Name);
        Assert.AreEqual("drm", HardwareDeviceType.Drm.Name);
        Assert.IsNull(HardwareDeviceType.None.Name);
        Assert.AreEqual(
            nameof(AVHWDeviceType.AV_HWDEVICE_TYPE_NONE),
            HardwareDeviceType.None.ToString()
        );
        Assert.IsFalse(HardwareDeviceType.TryParse("nope", out _));
        AVHWDeviceType native = HardwareDeviceType.Cuda;
        Assert.AreEqual(HardwareDeviceType.Cuda, (HardwareDeviceType)native);
    }

    [TestMethod]
    public void ChannelLayout_DescribesAndComparesLayouts()
    {
        Assert.AreEqual(1, ChannelLayout.Mono.ChannelCount);
        Assert.AreEqual("stereo", ChannelLayout.Stereo.ToString());
        Assert.AreEqual("5.1", ChannelLayout.Default(6).ToString());
        Assert.IsTrue(ChannelLayout.Stereo.IsNativeOrder);
        Assert.AreEqual(3UL, ChannelLayout.Stereo.Mask);
        Assert.AreEqual(ChannelLayout.Stereo, ChannelLayout.FromMask(3));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ChannelLayout.Default(0));
        _ = Assert.ThrowsExactly<FFmpegException>(() => ChannelLayout.FromMask(0));
    }

    [TestMethod]
    public unsafe void ChannelLayout_OtherOrders_AreHandled()
    {
        AVChannelLayout unspecified = new()
        {
            order = AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC,
            nb_channels = 3,
        };
        ChannelLayout layout = ChannelLayout.FromNative(&unspecified);
        Assert.IsFalse(layout.IsNativeOrder);
        Assert.AreEqual(3, layout.ChannelCount);
        Assert.AreEqual("3 channels", layout.ToString());

        AVChannelLayout* ambisonic = stackalloc AVChannelLayout[1];
        ambisonic->order = AVChannelOrder.AV_CHANNEL_ORDER_AMBISONIC;
        ambisonic->nb_channels = 4;
        nint pointer = (nint)ambisonic;
        _ = Assert.ThrowsExactly<NotSupportedException>(() =>
            ChannelLayout.FromNative((AVChannelLayout*)pointer)
        );
    }
}
