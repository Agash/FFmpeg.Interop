using System.Runtime.Versioning;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// CUDA interop for <see cref="HardwareDeviceType.Cuda"/> devices and <see cref="PixelFormat.Cuda"/>
/// frames (FFmpeg's <c>hwcontext_cuda.h</c>).
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public static unsafe class CudaExtensions
{
    extension(HardwareDevice device)
    {
        /// <summary>The <c>CUcontext</c> of a <see cref="HardwareDeviceType.Cuda"/> device.</summary>
        /// <param name="context">The context.</param>
        /// <returns>Whether the device is a CUDA device.</returns>
        public bool TryGetCudaContext(out nint context)
        {
            context =
                device.Type == HardwareDeviceType.Cuda
                    ? (nint)((AVCUDADeviceContext*)device.Context->hwctx)->cuda_ctx
                    : 0;
            return context != 0;
        }
    }

    extension(Frame frame)
    {
        /// <summary>A plane of a <see cref="PixelFormat.Cuda"/> frame in device memory.</summary>
        /// <param name="plane">The plane index.</param>
        /// <param name="devicePointer">The <c>CUdeviceptr</c> of the plane.</param>
        /// <param name="pitch">The row pitch in bytes.</param>
        /// <returns>Whether the frame is a CUDA frame with that plane.</returns>
        public bool TryGetCudaPlane(int plane, out nint devicePointer, out int pitch)
        {
            AVFrame* native = frame.NativePointer;
            if (
                frame.PixelFormat != PixelFormat.Cuda
                || (uint)plane >= LibAVUtil.AV_NUM_DATA_POINTERS
                || native->data[plane] is null
            )
            {
                devicePointer = 0;
                pitch = 0;
                return false;
            }

            devicePointer = (nint)native->data[plane];
            pitch = native->linesize[plane];
            return true;
        }
    }
}
