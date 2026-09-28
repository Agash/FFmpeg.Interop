using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D11;

namespace FFmpeg.Interop;

// The D3D11 calls needed to copy a texture into a pool surface on the GPU.
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
}
