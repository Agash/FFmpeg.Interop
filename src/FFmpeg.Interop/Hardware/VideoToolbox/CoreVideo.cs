using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

// The CoreVideo and IOSurface calls that turn an IOSurface into the CVPixelBuffer a VideoToolbox frame
// carries, and FFmpeg's mapping between its pixel formats and CoreVideo's (hwcontext_videotoolbox.h,
// which the generated bindings leave out because it includes Apple's headers).
[SupportedOSPlatform("macos")]
internal static partial class CoreVideo
{
    private const string CoreVideoLibrary =
        "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string IOSurfaceLibrary =
        "/System/Library/Frameworks/IOSurface.framework/IOSurface";

    // The CoreVideo pixel format (a four-character code) of an FFmpeg format, or 0 when there is none.
    public static uint PixelFormatOf(PixelFormat format) =>
        av_map_videotoolbox_format_from_pixfmt(format.Value);

    // A +1 CVPixelBuffer over the surface, sharing its memory.
    public static nint CreatePixelBuffer(nint surface)
    {
        int result = CVPixelBufferCreateWithIOSurface(0, surface, 0, out nint pixelBuffer);
        return result == 0 && pixelBuffer != 0
            ? pixelBuffer
            : throw new FFmpegException(
                $"CVPixelBufferCreateWithIOSurface failed (CVReturn {result})."
            );
    }

    public static (int Width, int Height, uint PixelFormat) Describe(nint surface) =>
        (
            (int)IOSurfaceGetWidth(surface),
            (int)IOSurfaceGetHeight(surface),
            IOSurfaceGetPixelFormat(surface)
        );

    // A four-character code as its characters, as CoreVideo names formats ('BGRA', '420v').
    public static string FourCC(uint code) =>
        code == 0
            ? "none"
            : string.Create(
                4,
                code,
                static (span, value) =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        span[i] = (char)((value >> (24 - (8 * i))) & 0xFF);
                    }
                }
            );

    [LibraryImport("avutil")]
    private static partial uint av_map_videotoolbox_format_from_pixfmt(AVPixelFormat format);

    [LibraryImport(CoreVideoLibrary)]
    private static partial int CVPixelBufferCreateWithIOSurface(
        nint allocator,
        nint surface,
        nint attributes,
        out nint pixelBuffer
    );

    // The IOSurface behind a pixel buffer, or 0 when it has none; not retained.
    public static nint SurfaceOf(nint pixelBuffer) => CVPixelBufferGetIOSurface(pixelBuffer);

    [LibraryImport(CoreVideoLibrary)]
    private static partial nint CVPixelBufferGetIOSurface(nint pixelBuffer);

    [LibraryImport(IOSurfaceLibrary)]
    private static partial nuint IOSurfaceGetWidth(nint surface);

    [LibraryImport(IOSurfaceLibrary)]
    private static partial nuint IOSurfaceGetHeight(nint surface);

    [LibraryImport(IOSurfaceLibrary)]
    private static partial uint IOSurfaceGetPixelFormat(nint surface);
}
