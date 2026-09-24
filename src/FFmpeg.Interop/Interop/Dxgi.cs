using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FFmpeg.Interop;

// The slice of DXGI needed to list adapters in the order FFmpeg's D3D11VA, D3D12VA and DXVA2 devices
// number them: they take an adapter index into IDXGIFactory's enumeration.
[SupportedOSPlatform("windows")]
internal static unsafe partial class Dxgi
{
    private const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND
    private const uint SoftwareAdapter = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

    // IDXGIFactory1
    private static readonly Guid s_factory1 = new(
        0x770aae78,
        0xf26f,
        0x4dba,
        0xa8,
        0x29,
        0x25,
        0x3c,
        0x83,
        0xd1,
        0xb3,
        0x87
    );

    public static List<GpuAdapter> EnumerateAdapters()
    {
        List<GpuAdapter> adapters = [];
        Guid iid = s_factory1;
        nint factory;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(&iid, &factory));
        try
        {
            for (uint index = 0; ; index++)
            {
                nint adapter;

                // IDXGIFactory1::EnumAdapters1 is slot 12: IUnknown (3), IDXGIObject (4), IDXGIFactory (5).
                int result = (
                    (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12)
                )(factory, index, &adapter);
                if (result == NotFound)
                {
                    return adapters;
                }

                Marshal.ThrowExceptionForHR(result);
                try
                {
                    AdapterDescription description;

                    // IDXGIAdapter1::GetDesc1 is slot 10: IUnknown (3), IDXGIObject (4), IDXGIAdapter (3).
                    Marshal.ThrowExceptionForHR(
                        (
                            (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)Slot(
                                adapter,
                                10
                            )
                        )(adapter, &description)
                    );
                    adapters.Add(
                        new GpuAdapter
                        {
                            Name = description.Name,
                            VendorId = (int)description.VendorId,
                            DeviceId = (int)description.DeviceId,
                            Luid = ((long)description.LuidHighPart << 32) | description.LuidLowPart,
                            DxgiIndex = (int)index,
                            DedicatedVideoMemory = (long)description.DedicatedVideoMemory,
                            IsSoftware = (description.Flags & SoftwareAdapter) != 0,
                        }
                    );
                }
                finally
                {
                    _ = Release(adapter);
                }
            }
        }
        finally
        {
            _ = Release(factory);
        }
    }

    // IUnknown::AddRef, for handing a caller's COM object to FFmpeg, which releases what it is given.
    public static uint AddRef(nint unknown) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 1))(unknown);

    private static uint Release(nint unknown) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 2))(unknown);

    private static void* Slot(nint unknown, int index) => (*(void***)unknown)[index];

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, nint* factory);

    // DXGI_ADAPTER_DESC1.
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public DescriptionText Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;

        public readonly string Name
        {
            get
            {
                ReadOnlySpan<char> text = Description;
                int end = text.IndexOf('\0');
                return new string(end < 0 ? text : text[..end]);
            }
        }
    }

    [InlineArray(128)]
    private struct DescriptionText
    {
        private char _first;
    }
}
