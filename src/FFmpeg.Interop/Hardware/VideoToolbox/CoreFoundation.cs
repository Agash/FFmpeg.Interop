using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

// Ties a CoreFoundation object's lifetime to an FFmpeg buffer: the buffer holds one retain, released
// when FFmpeg frees the buffer, the way FFmpeg's own VideoToolbox frames hold their CVPixelBuffers.
[SupportedOSPlatform("macos")]
internal static unsafe partial class CoreFoundation
{
    private const string Library =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public static AVBufferRef* RetainAsBuffer(nint value)
    {
        _ = CFRetain(value);
        AVBufferRef* buffer = LibAVUtil.av_buffer_create(
            (byte*)value,
            0,
            &Release,
            (void*)value,
            0
        );
        if (buffer is null)
        {
            CFRelease(value);
            FFmpegError.ThrowOutOfMemory("av_buffer_create");
        }

        return buffer;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(void* opaque, byte* data) => CFRelease((nint)opaque);

    [LibraryImport(Library)]
    private static partial nint CFRetain(nint value);

    [LibraryImport(Library)]
    private static partial void CFRelease(nint value);
}
