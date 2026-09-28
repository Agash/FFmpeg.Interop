using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace FFmpeg.Interop;

// Lists adapters in the order FFmpeg's D3D11VA, D3D12VA and DXVA2 devices number them: they take an
// adapter index into IDXGIFactory's enumeration.
[SupportedOSPlatform("windows6.1")]
internal static unsafe class Dxgi
{
    public static List<GpuAdapter> EnumerateAdapters()
    {
        List<GpuAdapter> adapters = [];
        Win32.CreateDXGIFactory1(out IDXGIFactory1* factory).ThrowOnFailure();
        try
        {
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1* adapter;
                HRESULT result = factory->EnumAdapters1(index, &adapter);
                if (result == HRESULT.DXGI_ERROR_NOT_FOUND)
                {
                    return adapters;
                }

                result.ThrowOnFailure();
                try
                {
                    DXGI_ADAPTER_DESC1 description = adapter->GetDesc1();
                    adapters.Add(
                        new GpuAdapter
                        {
                            Name = description.Description.ToString(),
                            VendorId = (int)description.VendorId,
                            DeviceId = (int)description.DeviceId,
                            Luid =
                                ((long)description.AdapterLuid.HighPart << 32)
                                | description.AdapterLuid.LowPart,
                            DxgiIndex = (int)index,
                            DedicatedVideoMemory = (long)description.DedicatedVideoMemory,
                            IsSoftware = description.Flags.HasFlag(
                                DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE
                            ),
                        }
                    );
                }
                finally
                {
                    _ = adapter->Release();
                }
            }
        }
        finally
        {
            _ = factory->Release();
        }
    }

    // For handing a caller's COM object to FFmpeg, which releases what it is given.
    public static uint AddRef(nint unknown) => ((IUnknown*)unknown)->AddRef();

    public static uint Release(nint unknown) => ((IUnknown*)unknown)->Release();
}
