using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using Windows.Win32.Graphics.Direct3D12;

namespace FFmpeg.Interop;

// The D3D12 calls behind D3D12VAExtensions.
[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class D3D12
{
    // FFmpeg's D3D12 uninit leaves the device alone; only the free callback it installs on devices it
    // creates releases it. A wrapped device gets this one instead.
    public static delegate* unmanaged[Cdecl]<AVHWDeviceContext*, void> ReleaseDevice =>
        &ReleaseDeviceCallback;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseDeviceCallback(AVHWDeviceContext* context)
    {
        AVD3D12VADeviceContext* hwctx = (AVD3D12VADeviceContext*)context->hwctx;
        if (hwctx->device is not null)
        {
            _ = ((ID3D12Device*)hwctx->device)->Release();
            hwctx->device = null;
        }
    }
}
