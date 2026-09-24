// This file mirrors FFmpeg's static inline functions under their native names, like the generated
// bindings beside it, so FFmpeg's documentation applies unchanged.
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable IDE1006 // Naming rule violation

namespace FFmpeg.Interop.Native;

// Each bindings class installs the library resolver from its static constructor. An explicit static
// constructor makes the runtime run it before the first call to any member of the class, which is
// what guarantees the resolver is in place before the first P/Invoke binds.

public static unsafe partial class LibAVCodec
{
    static LibAVCodec() => global::FFmpeg.Interop.FFmpegLibraries.EnsureResolverInstalled();
}

public static unsafe partial class LibAVFormat
{
    // stdio's SEEK_CUR, which avio_seek takes as its whence argument.
    private const int SeekCurrent = 1;

    static LibAVFormat() => global::FFmpeg.Interop.FFmpegLibraries.EnsureResolverInstalled();

    /// <summary><c>avio_tell</c>: the current position in the stream.</summary>
    /// <param name="s">The I/O context.</param>
    /// <returns>The position, or a negative FFmpeg error code.</returns>
    public static long avio_tell(AVIOContext* s) => avio_seek(s, 0, SeekCurrent);
}

public static unsafe partial class LibAVDevice
{
    static LibAVDevice() => global::FFmpeg.Interop.FFmpegLibraries.EnsureResolverInstalled();
}

public static unsafe partial class LibSwScale
{
    static LibSwScale() => global::FFmpeg.Interop.FFmpegLibraries.EnsureResolverInstalled();
}

public static unsafe partial class LibSwResample
{
    static LibSwResample() => global::FFmpeg.Interop.FFmpegLibraries.EnsureResolverInstalled();
}
