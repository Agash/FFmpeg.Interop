using FFmpeg.Interop.Native;

namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class HardwareDeviceTests
{
    [TestMethod]
    public unsafe void Share_KeepsTheDeviceOpen_AfterTheOriginalIsDisposed()
    {
        HardwareDeviceType[] types =
        [
            HardwareDeviceType.D3D11VA,
            HardwareDeviceType.VideoToolbox,
            HardwareDeviceType.Vaapi,
            HardwareDeviceType.Vulkan,
        ];
        HardwareDevice? original = null;
        foreach (HardwareDeviceType type in types)
        {
            if (HardwareDevice.TryCreate(type, out original))
            {
                break;
            }
        }

        if (original is null)
        {
            Assert.Inconclusive("This machine opens none of the hardware device types.");
        }

        AVHWDeviceContext* context = original.Context;
        using HardwareDevice shared = original.Share();
        original.Dispose();

        Assert.IsTrue(shared.Context == context, "both references hold the same device");
        Assert.AreEqual(new HardwareDeviceType(context->type), shared.Type);
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => original.Type);
    }
}
