using System.Runtime.Versioning;

namespace FFmpeg.Interop;

/// <summary>
/// VideoToolbox interop for <see cref="PixelFormat.VideoToolbox"/> pools and frames (FFmpeg's
/// <c>hwcontext_videotoolbox.h</c>), whose surfaces are <c>CVPixelBufferRef</c>s.
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe class VideoToolboxExtensions
{
    extension(HardwareFramePool pool)
    {
        /// <summary>
        /// Wraps a <c>CVPixelBufferRef</c> (for example an IOSurface from a Syphon server or a capture
        /// session) as a VideoToolbox frame of this pool, without copying, for a VideoToolbox encoder. The
        /// frame retains the pixel buffer and releases it when FFmpeg drops the frame's last reference.
        /// </summary>
        /// <param name="pixelBuffer">The <c>CVPixelBufferRef</c>, in the pool's format and size.</param>
        /// <param name="destination">The frame to receive it; its previous content is released.</param>
        public void WrapCVPixelBuffer(nint pixelBuffer, Frame destination)
        {
            if (pixelBuffer == 0)
            {
                throw new ArgumentNullException(nameof(pixelBuffer));
            }

            ArgumentNullException.ThrowIfNull(destination);
            if (pool.Format != PixelFormat.VideoToolbox)
            {
                throw new InvalidOperationException(
                    $"The pool holds {pool.Format} surfaces, not CVPixelBuffers."
                );
            }

            // FFmpeg's VideoToolbox frames carry the pixel buffer in data[3].
            pool.Adopt(
                destination,
                CoreFoundation.RetainAsBuffer(pixelBuffer),
                3,
                (void*)pixelBuffer
            );
        }
    }

    extension(Frame frame)
    {
        /// <summary>The <c>CVPixelBufferRef</c> behind a <see cref="PixelFormat.VideoToolbox"/> frame.</summary>
        /// <param name="pixelBuffer">The pixel buffer; the frame holds the retain on it.</param>
        /// <returns>Whether the frame is a VideoToolbox frame.</returns>
        public bool TryGetCVPixelBuffer(out nint pixelBuffer)
        {
            pixelBuffer =
                frame.PixelFormat == PixelFormat.VideoToolbox
                    ? (nint)frame.NativePointer->data[3]
                    : 0;
            return pixelBuffer != 0;
        }
    }
}
