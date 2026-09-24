namespace FFmpeg.Interop.Native;

/// <summary>
/// POSIX errno values FFmpeg reports through <c>AVERROR</c>, for the current OS.
/// </summary>
/// <remarks>
/// FFmpeg returns <c>AVERROR(errno)</c>, and POSIX does not fix errno values. Linux and the Windows
/// C runtime share the common values; macOS numbers several differently (EAGAIN is 35 there, not 11).
/// Comparing against a constant copied from one platform silently misreads the result on another.
/// </remarks>
public static class Errno
{
    /// <summary>Operation not permitted.</summary>
    public const int EPERM = 1;

    /// <summary>No such file or directory.</summary>
    public const int ENOENT = 2;

    /// <summary>Input/output error.</summary>
    public const int EIO = 5;

    /// <summary>Out of memory.</summary>
    public const int ENOMEM = 12;

    /// <summary>Permission denied.</summary>
    public const int EACCES = 13;

    /// <summary>Device or resource busy.</summary>
    public const int EBUSY = 16;

    /// <summary>File exists.</summary>
    public const int EEXIST = 17;

    /// <summary>Invalid argument.</summary>
    public const int EINVAL = 22;

    /// <summary>Broken pipe.</summary>
    public const int EPIPE = 32;

    /// <summary>Numerical argument out of domain.</summary>
    public const int EDOM = 33;

    /// <summary>Numerical result out of range.</summary>
    public const int ERANGE = 34;

    /// <summary>Resource temporarily unavailable: 11 on Linux and Windows, 35 on macOS.</summary>
    public static int EAGAIN { get; } = OperatingSystem.IsMacOS() ? 35 : 11;

    /// <summary>Function not implemented: 38 on Linux, 40 on Windows, 78 on macOS.</summary>
    public static int ENOSYS { get; } =
        OperatingSystem.IsMacOS() ? 78
        : OperatingSystem.IsWindows() ? 40
        : 38;
}
