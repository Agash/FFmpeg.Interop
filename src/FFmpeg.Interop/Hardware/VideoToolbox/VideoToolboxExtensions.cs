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

        /// <summary>
        /// Wraps an <c>IOSurfaceRef</c> (a Syphon frame, a capture session's or a Metal texture's surface)
        /// as a VideoToolbox frame of this pool, without copying, for a VideoToolbox encoder: a
        /// <c>CVPixelBuffer</c> over the surface's memory, which the frame holds until FFmpeg drops its
        /// last reference.
        /// </summary>
        /// <param name="surface">
        /// The <c>IOSurfaceRef</c>, of the pool's size, in the CoreVideo format of the pool's software
        /// format (<c>'BGRA'</c> for <see cref="PixelFormat.Bgra"/>, <c>'420v'</c> for
        /// <see cref="PixelFormat.Nv12"/>).
        /// </param>
        /// <param name="destination">The frame to receive it; its previous content is released.</param>
        public void WrapIOSurface(nint surface, Frame destination)
        {
            if (surface == 0)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            ArgumentNullException.ThrowIfNull(destination);
            if (pool.Format != PixelFormat.VideoToolbox)
            {
                throw new InvalidOperationException(
                    $"The pool holds {pool.Format} surfaces, not CVPixelBuffers."
                );
            }

            uint expected = CoreVideo.PixelFormatOf(pool.SoftwareFormat);
            (int width, int height, uint format) = CoreVideo.Describe(surface);
            if (width != pool.Width || height != pool.Height || format != expected)
            {
                throw new ArgumentException(
                    $"The surface is {width}x{height} '{CoreVideo.FourCC(format)}'; the pool needs {pool.Width}x{pool.Height} '{CoreVideo.FourCC(expected)}' ({pool.SoftwareFormat}).",
                    nameof(surface)
                );
            }

            nint pixelBuffer = CoreVideo.CreatePixelBuffer(surface);
            try
            {
                pool.WrapCVPixelBuffer(pixelBuffer, destination);
            }
            finally
            {
                // The frame took its own reference.
                CoreFoundation.ReleaseReference(pixelBuffer);
            }
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

        /// <summary>
        /// The <c>IOSurfaceRef</c> behind a <see cref="PixelFormat.VideoToolbox"/> frame, which Metal,
        /// Core Video and Syphon share without a copy. A VideoToolbox decoder's frames have one.
        /// </summary>
        /// <param name="surface">The surface; not retained, valid while the frame holds its pixel buffer.</param>
        /// <returns>Whether the frame is a VideoToolbox frame backed by an IOSurface.</returns>
        public bool TryGetIOSurface(out nint surface)
        {
            surface = frame.TryGetCVPixelBuffer(out nint pixelBuffer)
                ? CoreVideo.SurfaceOf(pixelBuffer)
                : 0;
            return surface != 0;
        }
    }
}
