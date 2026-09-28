using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// DRM PRIME (DMA-BUF) interop for <see cref="HardwareDeviceType.Drm"/> devices and
/// <see cref="PixelFormat.DrmPrime"/> frames (FFmpeg's <c>hwcontext_drm.h</c>): the common currency
/// between Linux capture (PipeWire, V4L2), media engines and GPU APIs.
/// </summary>
[SupportedOSPlatform("linux")]
public static unsafe class DrmExtensions
{
    extension(HardwareDevice device)
    {
        /// <summary>The DRM file descriptor of a <see cref="HardwareDeviceType.Drm"/> device.</summary>
        /// <param name="fileDescriptor">The descriptor, owned by the device.</param>
        /// <returns>Whether the device is a DRM device.</returns>
        public bool TryGetDrmFileDescriptor(out int fileDescriptor)
        {
            bool isDrm = device.Type == HardwareDeviceType.Drm;
            fileDescriptor = isDrm ? ((AVDRMDeviceContext*)device.Context->hwctx)->fd : -1;
            return isDrm;
        }
    }

    extension(Frame)
    {
        /// <summary>
        /// A DRM PRIME frame over DMA-BUFs another component produced (a PipeWire screencast, a V4L2
        /// camera), for mapping into an encoder's surfaces with
        /// <see cref="Frame.MapTo(HardwareFramePool, Frame, HardwareMapAccess)"/> without a copy. The frame
        /// references the file descriptors and does not close them: keep them open until the frame and
        /// every mapping of it are released.
        /// </summary>
        /// <param name="image">The DMA-BUF objects and how the picture is laid out in them.</param>
        /// <param name="width">The picture width.</param>
        /// <param name="height">The picture height.</param>
        /// <returns>The frame.</returns>
        public static Frame FromDrmPrime(DrmPrimeImage image, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            AVDRMFrameDescriptor descriptor = image.ToNative();

            // FFmpeg's own DRM frames carry the descriptor in buf[0], and av_hwframe_map looks for it there.
            Frame result = new();
            AVFrame* frame = result.NativePointer;
            frame->buf.e0 = av_buffer_allocz((nuint)sizeof(AVDRMFrameDescriptor));
            if (frame->buf.e0 is null)
            {
                result.Dispose();
                FFmpegError.ThrowOutOfMemory("av_buffer_allocz");
            }

            *(AVDRMFrameDescriptor*)frame->buf.e0->data = descriptor;
            frame->data[0] = frame->buf.e0->data;
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
            frame->width = width;
            frame->height = height;
            return result;
        }
    }

    extension(Frame frame)
    {
        /// <summary>The DMA-BUF descriptors of a <see cref="PixelFormat.DrmPrime"/> frame.</summary>
        /// <param name="descriptor">A view of the descriptors.</param>
        /// <returns>Whether the frame is a DRM PRIME frame.</returns>
        public bool TryGetDrmFrame(out DrmFrameDescriptor descriptor)
        {
            AVFrame* native = frame.NativePointer;
            if (frame.PixelFormat != PixelFormat.DrmPrime || native->data[0] is null)
            {
                descriptor = default;
                return false;
            }

            descriptor = new((AVDRMFrameDescriptor*)native->data[0]);
            return true;
        }
    }
}
