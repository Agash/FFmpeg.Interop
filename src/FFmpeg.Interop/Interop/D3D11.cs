using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FFmpeg.Interop;

// The D3D11 calls needed to copy a texture into a pool surface on the GPU, through COM vtables.
[SupportedOSPlatform("windows")]
internal static unsafe class D3D11
{
    // ID3D11DeviceChild::GetDevice: IUnknown (3 slots) then GetDevice.
    public static nint GetDevice(nint child)
    {
        nint device;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)Slot(child, 3))(child, &device);
        return device;
    }

    // ID3D11Texture2D::GetDesc: IUnknown (3), ID3D11DeviceChild (4), ID3D11Resource (3), then GetDesc.
    public static TextureDescription GetDescription(nint texture)
    {
        TextureDescription description;
        ((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void>)Slot(texture, 10))(
            texture,
            &description
        );
        return description;
    }

    // ID3D11DeviceContext::CopySubresourceRegion: IUnknown (3), ID3D11DeviceChild (4), then 39 methods
    // before it in the interface's declaration order.
    public static void CopySubresourceRegion(
        nint context,
        nint destination,
        uint destinationSubresource,
        nint source,
        uint sourceSubresource,
        in Box box
    )
    {
        fixed (Box* region = &box)
        {
            (
                (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    uint,
                    uint,
                    uint,
                    uint,
                    nint,
                    uint,
                    Box*,
                    void>)Slot(context, 46)
            )(
                context,
                destination,
                destinationSubresource,
                0,
                0,
                0,
                source,
                sourceSubresource,
                region
            );
        }
    }

    public static uint Release(nint unknown) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 2))(unknown);

    private static void* Slot(nint unknown, int index) => (*(void***)unknown)[index];

    // D3D11_TEXTURE2D_DESC.
    [StructLayout(LayoutKind.Sequential)]
    public struct TextureDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    // D3D11_BOX.
    [StructLayout(LayoutKind.Sequential)]
    public struct Box
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }
}
