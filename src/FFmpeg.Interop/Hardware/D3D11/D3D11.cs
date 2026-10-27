using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D11;

namespace FFmpeg.Interop;

// The D3D11 calls needed to copy a texture into a pool surface on the GPU, or to wrap one as a surface.
[SupportedOSPlatform("windows6.1")]
internal static unsafe class D3D11
{
    // The device a texture belongs to (not AddRef'd on return: the reference GetDevice takes is dropped).
    public static nint GetDevice(nint texture)
    {
        ID3D11Device* device;
        ((ID3D11Texture2D*)texture)->GetDevice(&device);
        _ = device->Release();
        return (nint)device;
    }

    // Releases a wrapped texture when the last frame referencing it goes.
    public static delegate* unmanaged[Cdecl]<void*, byte*, void> TextureRelease => &ReleaseTexture;

    public static D3D11_TEXTURE2D_DESC GetDescription(nint texture)
    {
        D3D11_TEXTURE2D_DESC description;
        ((ID3D11Texture2D*)texture)->GetDesc(&description);
        return description;
    }

    public static void CopySubresourceRegion(
        nint context,
        nint destination,
        uint destinationSubresource,
        nint source,
        uint sourceSubresource,
        uint width,
        uint height
    )
    {
        D3D11_BOX region = new()
        {
            right = width,
            bottom = height,
            back = 1,
        };
        ((ID3D11DeviceContext*)context)->CopySubresourceRegion(
            (ID3D11Resource*)destination,
            destinationSubresource,
            0,
            0,
            0,
            (ID3D11Resource*)source,
            sourceSubresource,
            &region
        );
    }

    // Queues a GPU wait on the context for a fence (an ID3D11Fence of the context's device) to reach a
    // value: work the context records after it runs once the producer's work is done, and the CPU never
    // waits.
    public static void Wait(nint context, nint fence, ulong value)
    {
        Guid iid = typeof(ID3D11DeviceContext4).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(context, in iid, out nint queued));
        try
        {
            ((ID3D11DeviceContext4*)queued)->Wait((ID3D11Fence*)fence, value);
        }
        finally
        {
            _ = Marshal.Release(queued);
        }
    }

    // Queues a signal of a fence on the context: it reaches the value once the work recorded before it
    // has run. The context is flushed, since Direct3D 11 batches its commands and another API's queue
    // waiting on the fence would otherwise wait for whatever submits them next.
    public static void Signal(nint context, nint fence, ulong value)
    {
        Guid iid = typeof(ID3D11DeviceContext4).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(context, in iid, out nint queued));
        try
        {
            ((ID3D11DeviceContext4*)queued)->Signal((ID3D11Fence*)fence, value);
            ((ID3D11DeviceContext4*)queued)->Flush();
        }
        finally
        {
            _ = Marshal.Release(queued);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseTexture(void* opaque, byte* data) => _ = Dxgi.Release((nint)data);
}
