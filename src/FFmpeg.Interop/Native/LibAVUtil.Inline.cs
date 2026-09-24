using System.Runtime.InteropServices;

// This file mirrors FFmpeg's static inline functions and function-like macros under their native
// names, like the generated bindings beside it, so FFmpeg's documentation applies unchanged.
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable IDE1006 // Naming rule violation

namespace FFmpeg.Interop.Native;

public static unsafe partial class LibAVUtil
{
    static LibAVUtil() => global::FFmpeg.Interop.FFmpegLibraries.EnsureResolverInstalled();

    /// <summary><c>AV_TIME_BASE_Q</c>: the internal time base, microseconds.</summary>
    public static AVRational AV_TIME_BASE_Q => new() { num = 1, den = AV_TIME_BASE };

    /// <summary>
    /// <c>AVERROR(EAGAIN)</c> for the current OS. POSIX leaves errno values to the platform:
    /// EAGAIN is 11 on Linux and Windows and 35 on macOS.
    /// </summary>
    public static int AVERROR_EAGAIN => AVERROR(Errno.EAGAIN);

    /// <summary><c>AVERROR(e)</c>: the negative FFmpeg error code for a POSIX errno value.</summary>
    /// <param name="errnum">A positive errno value; see <see cref="Errno"/>.</param>
    /// <returns>The FFmpeg error code.</returns>
    public static int AVERROR(int errnum) => -errnum;

    /// <summary><c>AVUNERROR(e)</c>: the POSIX errno value inside an FFmpeg error code.</summary>
    /// <param name="error">A negative FFmpeg error code.</param>
    /// <returns>The errno value.</returns>
    public static int AVUNERROR(int error) => -error;

    /// <summary><c>MKTAG(a, b, c, d)</c>: a little-endian four-character code.</summary>
    /// <param name="a">First character.</param>
    /// <param name="b">Second character.</param>
    /// <param name="c">Third character.</param>
    /// <param name="d">Fourth character.</param>
    /// <returns>The tag.</returns>
    public static uint MKTAG(byte a, byte b, byte c, byte d) =>
        a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    /// <summary><c>MKBETAG(a, b, c, d)</c>: a big-endian four-character code.</summary>
    /// <param name="a">First character.</param>
    /// <param name="b">Second character.</param>
    /// <param name="c">Third character.</param>
    /// <param name="d">Fourth character.</param>
    /// <returns>The tag.</returns>
    public static uint MKBETAG(byte a, byte b, byte c, byte d) =>
        d | ((uint)c << 8) | ((uint)b << 16) | ((uint)a << 24);

    /// <summary><c>av_make_q</c>: creates a rational.</summary>
    /// <param name="num">Numerator.</param>
    /// <param name="den">Denominator.</param>
    /// <returns>The rational.</returns>
    public static AVRational av_make_q(int num, int den) => new() { num = num, den = den };

    /// <summary>
    /// <c>av_cmp_q</c>: compares two rationals. Returns 0 when equal, 1 when <paramref name="a"/> is
    /// greater, -1 when it is smaller, and <see cref="int.MinValue"/> when either is 0/0.
    /// </summary>
    /// <param name="a">First rational.</param>
    /// <param name="b">Second rational.</param>
    /// <returns>The comparison result.</returns>
    public static int av_cmp_q(AVRational a, AVRational b)
    {
        long tmp = (a.num * (long)b.den) - (b.num * (long)a.den);
        if (tmp != 0)
        {
            return (int)((tmp ^ a.den ^ b.den) >> 63) | 1;
        }

        if (b.den != 0 && a.den != 0)
        {
            return 0;
        }

        return a.num != 0 && b.num != 0 ? (a.num >> 31) - (b.num >> 31) : int.MinValue;
    }

    /// <summary><c>av_q2d</c>: converts a rational to a double.</summary>
    /// <param name="a">The rational.</param>
    /// <returns>Its value.</returns>
    public static double av_q2d(AVRational a) => a.num / (double)a.den;

    /// <summary><c>av_inv_q</c>: inverts a rational.</summary>
    /// <param name="q">The rational.</param>
    /// <returns><c>1 / q</c>.</returns>
    public static AVRational av_inv_q(AVRational q) => new() { num = q.den, den = q.num };

    /// <summary><c>av_x_if_null</c>: returns <paramref name="p"/>, or <paramref name="x"/> when it is null.</summary>
    /// <param name="p">The preferred pointer.</param>
    /// <param name="x">The fallback.</param>
    /// <returns>The first non-null pointer.</returns>
    public static void* av_x_if_null(void* p, void* x) => p is not null ? p : x;

    /// <summary><c>av_make_error_string</c>: writes the description of an error code into a buffer.</summary>
    /// <param name="errbuf">The destination buffer.</param>
    /// <param name="errbuf_size">Its size in bytes.</param>
    /// <param name="errnum">The error code.</param>
    /// <returns><paramref name="errbuf"/>.</returns>
    public static sbyte* av_make_error_string(sbyte* errbuf, nuint errbuf_size, int errnum)
    {
        _ = av_strerror(errnum, errbuf, errbuf_size);
        return errbuf;
    }

    /// <summary>The description of an FFmpeg error code, as <c>av_err2str</c> gives it.</summary>
    /// <param name="errnum">The error code.</param>
    /// <returns>The description.</returns>
    public static string av_err2str(int errnum)
    {
        sbyte* buffer = stackalloc sbyte[AV_ERROR_MAX_STRING_SIZE];
        _ = av_strerror(errnum, buffer, AV_ERROR_MAX_STRING_SIZE);
        return Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty;
    }
}
