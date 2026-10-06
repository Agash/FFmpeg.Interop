using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FFmpeg.Interop.Tests;

// A DRM syncobj timeline on a render node, signalled from the CPU: the producer side of explicit sync.
[SupportedOSPlatform("linux")]
internal sealed partial class DrmTimeline : IDisposable
{
    private readonly int _drm;
    private readonly uint _handle;

    public DrmTimeline(string renderNode = "/dev/dri/renderD128")
    {
        _drm = Open(renderNode, ReadWrite | CloseOnExec);
        if (_drm < 0)
        {
            throw new InvalidOperationException(
                $"{renderNode} did not open (errno {Marshal.GetLastPInvokeError()})."
            );
        }

        Check(drmSyncobjCreate(_drm, 0, out _handle), "drmSyncobjCreate");
        Check(drmSyncobjHandleToFD(_drm, _handle, out int fd), "drmSyncobjHandleToFD");
        Fd = fd;
    }

    public int Fd { get; }

    public void Signal(ulong point)
    {
        uint handle = _handle;
        Check(drmSyncobjTimelineSignal(_drm, ref handle, ref point, 1), "drmSyncobjTimelineSignal");
    }

    public void Dispose()
    {
        _ = Close(Fd);
        _ = drmSyncobjDestroy(_drm, _handle);
        _ = Close(_drm);
    }

    private static void Check(int result, string call)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{call} failed: {result}.");
        }
    }

    private const int ReadWrite = 2;
    private const int CloseOnExec = 0x80000;

    [LibraryImport(
        "libc",
        EntryPoint = "open",
        StringMarshalling = StringMarshalling.Utf8,
        SetLastError = true
    )]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("libdrm.so.2")]
    private static partial int drmSyncobjCreate(int fd, uint flags, out uint handle);

    [LibraryImport("libdrm.so.2")]
    private static partial int drmSyncobjHandleToFD(int fd, uint handle, out int objectFd);

    [LibraryImport("libdrm.so.2")]
    private static partial int drmSyncobjTimelineSignal(
        int fd,
        ref uint handles,
        ref ulong points,
        uint count
    );

    [LibraryImport("libdrm.so.2")]
    private static partial int drmSyncobjDestroy(int fd, uint handle);
}
