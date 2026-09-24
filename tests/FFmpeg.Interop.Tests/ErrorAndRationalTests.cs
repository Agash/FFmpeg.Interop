using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class ErrorAndRationalTests
{
    [TestMethod]
    public void AvErr2Str_Eof_MatchesFFmpegsOwnDescription()
    {
        TestNatives.Require();

        Assert.AreEqual("End of file", av_err2str(AVERROR_EOF));
    }

    // FFmpeg describes AVERROR(errno) with the C runtime's strerror, so a wrong errno value for this OS
    // shows up as the wrong description. This is what checks the per-OS table against the real library.
    [TestMethod]
    [DataRow(nameof(Errno.EAGAIN))]
    [DataRow(nameof(Errno.ENOSYS))]
    [DataRow(nameof(Errno.EINVAL))]
    [DataRow(nameof(Errno.ENOMEM))]
    public void AVERROR_ForErrnoOnThisOs_IsDescribedAsThatErrno(string name)
    {
        TestNatives.Require();
        (int value, string[] descriptions) = name switch
        {
            nameof(Errno.EAGAIN) => (Errno.EAGAIN, new[] { "Resource temporarily unavailable" }),
            nameof(Errno.ENOSYS) => (Errno.ENOSYS, ["Function not implemented"]),
            nameof(Errno.EINVAL) => (Errno.EINVAL, ["Invalid argument"]),
            nameof(Errno.ENOMEM) => (
                Errno.ENOMEM,
                ["Cannot allocate memory", "Not enough space", "Not enough memory"]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        string actual = av_err2str(AVERROR(value));

        Assert.Contains(actual, descriptions, $"{name} ({value}) was described as '{actual}'.");
    }

    [TestMethod]
    public void AVERROR_EAGAIN_EqualsAVERRORofErrnoEAGAIN() =>
        Assert.AreEqual(-Errno.EAGAIN, AVERROR_EAGAIN);

    [TestMethod]
    [DataRow(1, 2, 1, 2, 0)]
    [DataRow(1, 2, 1, 3, 1)]
    [DataRow(1, 3, 1, 2, -1)]
    [DataRow(-1, 2, 1, 2, -1)]
    [DataRow(1, 0, 2, 0, 0)]
    [DataRow(0, 0, 1, 2, int.MinValue)]
    public void AvCmpQ_MatchesTheCDefinition(int an, int ad, int bn, int bd, int expected) =>
        Assert.AreEqual(expected, av_cmp_q(av_make_q(an, ad), av_make_q(bn, bd)));

    [TestMethod]
    public void AvQ2dAndInvQ_OnAThird_AreConsistent()
    {
        AVRational third = av_make_q(1, 3);

        Assert.AreEqual(1.0 / 3.0, av_q2d(third), 1e-15);
        Assert.AreEqual(3.0, av_q2d(av_inv_q(third)), 1e-15);
    }

    [TestMethod]
    public void MkTag_Eof_BuildsTheErrorTag() =>
        Assert.AreEqual(AVERROR_EOF, -(int)MKTAG((byte)'E', (byte)'O', (byte)'F', (byte)' '));

    [TestMethod]
    public void MkBeTag_IsTheByteSwappedTag() =>
        Assert.AreEqual(
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(MKTAG(1, 2, 3, 4)),
            MKBETAG(1, 2, 3, 4)
        );

    [TestMethod]
    public void AvUnError_InvertsAverror() =>
        Assert.AreEqual(Errno.EINVAL, AVUNERROR(AVERROR(Errno.EINVAL)));

    [TestMethod]
    public unsafe void AvXIfNull_PrefersTheFirstPointer()
    {
        int a = 1;
        int b = 2;

        Assert.IsTrue(av_x_if_null(&a, &b) == &a);
        Assert.IsTrue(av_x_if_null(null, &b) == &b);
    }

    [TestMethod]
    public unsafe void AvMakeErrorString_WritesTheDescriptionIntoTheBuffer()
    {
        TestNatives.Require();
        sbyte* buffer = stackalloc sbyte[AV_ERROR_MAX_STRING_SIZE];

        sbyte* result = av_make_error_string(buffer, AV_ERROR_MAX_STRING_SIZE, AVERROR_EOF);

        Assert.IsTrue(result == buffer);
        Assert.AreEqual("End of file", new string(buffer));
    }

    [TestMethod]
    public void AvTimeBaseQ_IsMicroseconds() =>
        Assert.AreEqual(1_000_000, av_q2d(av_inv_q(AV_TIME_BASE_Q)), 0);

    [TestMethod]
    public void Rational_ConvertsAndCompares()
    {
        Rational third = new(1, 3);

        Assert.AreEqual(1.0 / 3.0, third.ToDouble(), 1e-15);
        Assert.AreEqual(new Rational(3, 1), third.Invert());
        Assert.AreNotEqual(new Rational(2, 6), third, "Equality is by representation.");
        Assert.IsTrue(third.IsEquivalentTo(new Rational(2, 6)));
        Assert.IsFalse(third.IsEquivalentTo(new Rational(0, 0)));
        Assert.AreEqual("1/3", third.ToString());
        Assert.AreEqual(new Rational(1, 1_000_000), Rational.Microseconds);
        AVRational native = third;
        Assert.AreEqual(third, (Rational)native);
    }

    [TestMethod]
    public void Rational_RescalesTimestamps()
    {
        TestNatives.Require();

        Assert.AreEqual(3000, Rational.Rescale(1, new(1, 30), new(1, 90000)));
        Assert.AreEqual(TimeSpan.FromSeconds(2), new Rational(1, 90000).ToTimeSpan(180000));
    }

    [TestMethod]
    public void FFmpegException_CarriesTheCodeAndOperation()
    {
        TestNatives.Require();
        FFmpegException error = new(AVERROR_EOF, "av_read_frame");

        Assert.AreEqual(AVERROR_EOF, error.ErrorCode);
        Assert.AreEqual("av_read_frame", error.Operation);
        Assert.Contains("End of file", error.Message);
        Assert.AreEqual(0, new FFmpegException().ErrorCode);
        Assert.AreEqual("m", new FFmpegException("m").Message);
        Assert.IsInstanceOfType<InvalidOperationException>(
            new FFmpegException("m", new InvalidOperationException()).InnerException
        );
    }

    [TestMethod]
    public void FFmpegError_NamesTheFunctionFromTheCall()
    {
        TestNatives.Require();

        Assert.AreEqual(5, FFmpegError.ThrowIfError(5));
        FFmpegException named = Assert.ThrowsExactly<FFmpegException>(() =>
            FFmpegError.ThrowIfError(AVERROR_EOF, "avcodec_open2(context, codec, &options)")
        );
        FFmpegException bare = Assert.ThrowsExactly<FFmpegException>(() =>
            FFmpegError.ThrowIfError(AVERROR_EOF, "something")
        );
        FFmpegException unknown = Assert.ThrowsExactly<FFmpegException>(() =>
            FFmpegError.Throw(AVERROR_EOF, null)
        );
        _ = Assert.ThrowsExactly<OutOfMemoryException>(() =>
            FFmpegError.ThrowOutOfMemory("av_malloc")
        );

        Assert.AreEqual("avcodec_open2", named.Operation);
        Assert.AreEqual("something", bare.Operation);
        Assert.AreEqual("FFmpeg call", unknown.Operation);
    }

    // AVIOContext holds an unsigned long, 32-bit on Windows and 64-bit elsewhere, so its layout is the
    // one generated struct whose size depends on the OS. Expected values were measured with the platform
    // C compilers against the FFmpeg 9.0.2 headers.
    [TestMethod]
    public unsafe void AVIOContext_Layout_MatchesTheNativeAbiOfThisOs()
    {
        bool windows = OperatingSystem.IsWindows();
        AVIOContext context = default;
        byte* start = (byte*)&context;

        Assert.AreEqual(windows ? 200 : 208, sizeof(AVIOContext));
        Assert.AreEqual(windows ? 100 : 104, (int)((byte*)&context.checksum - start));
        Assert.AreEqual(windows ? 104 : 112, (int)((byte*)&context.checksum_ptr - start));
        Assert.AreEqual(windows ? 112 : 120, (int)((byte*)&context.update_checksum - start));
    }
}
