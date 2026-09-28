using System.Runtime.Versioning;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// VA-API interop for <see cref="HardwareDeviceType.Vaapi"/> devices and <see cref="PixelFormat.Vaapi"/>
/// frames (FFmpeg's <c>hwcontext_vaapi.h</c>).
/// </summary>
[SupportedOSPlatform("linux")]
public static unsafe class VaapiExtensions
{
    extension(HardwareDevice device)
    {
        /// <summary>The <c>VADisplay</c> of a <see cref="HardwareDeviceType.Vaapi"/> device.</summary>
        /// <param name="display">The display.</param>
        /// <returns>Whether the device is a VA-API device.</returns>
        public bool TryGetVaapiDisplay(out nint display)
        {
            display =
                device.Type == HardwareDeviceType.Vaapi
                    ? (nint)((AVVAAPIDeviceContext*)device.Context->hwctx)->display
                    : 0;
            return display != 0;
        }
    }

    extension(Frame frame)
    {
        /// <summary>The VA-API surface behind a <see cref="PixelFormat.Vaapi"/> frame.</summary>
        /// <param name="surfaceId">The <c>VASurfaceID</c>.</param>
        /// <returns>Whether the frame is a VA-API frame.</returns>
        public bool TryGetVaapiSurface(out uint surfaceId)
        {
            bool isVaapi = frame.PixelFormat == PixelFormat.Vaapi;
            surfaceId = isVaapi ? (uint)(nuint)frame.NativePointer->data[3] : 0;
            return isVaapi;
        }
    }
}
