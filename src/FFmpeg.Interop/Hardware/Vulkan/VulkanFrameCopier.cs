using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// Copies Vulkan frames of one device into each other on the GPU without waiting for the copies. Each
/// copy is ordered by the frames' timeline semaphores: FFmpeg's later work on the destination (an encoder
/// reading it) waits for the copy on the GPU, and so does work queued on the source after it, such as
/// <see cref="VulkanDmaBufImporter.Release"/> handing an imported buffer back to its producer. One command
/// pool and queue serve every copy for the copier's lifetime. Thread-safe.
/// </summary>
/// <remarks>
/// <see cref="Frame.CopyTo"/> is the one-off form, which waits for its copy before returning.
/// </remarks>
public sealed unsafe class VulkanFrameCopier : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SafeBufferHandle _device;
    private readonly VulkanQueueWork _work;
    private bool _disposed;

    /// <summary>Creates a copier for a device's frames.</summary>
    /// <param name="device">A Vulkan device. The copier keeps its own reference to it.</param>
    /// <exception cref="ArgumentException">The device is not a Vulkan device.</exception>
    public VulkanFrameCopier(HardwareDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Type != HardwareDeviceType.Vulkan)
        {
            throw new ArgumentException($"{device.Type} is not a Vulkan device.", nameof(device));
        }

        _device = SafeBufferHandle.Own(av_buffer_ref(device.NativePointer), "av_buffer_ref");
        try
        {
            _work = new VulkanQueueWork((AVHWDeviceContext*)_device.Pointer->data);
        }
        catch
        {
            _device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Queues a copy of <paramref name="source"/>'s picture into <paramref name="destination"/>, which
    /// holds a surface of the same size and format, and copies the frame properties. Returns once the copy
    /// is queued.
    /// </summary>
    /// <param name="source">A Vulkan frame of the copier's device.</param>
    /// <param name="destination">A surface from a <see cref="HardwareFramePool"/> of the same device.</param>
    /// <returns>
    /// The point the copy completes at, for <see cref="IsComplete"/>: a source whose next user does not
    /// wait on its timeline semaphores (an unmapped DMA-BUF) is released only once it is reached.
    /// </returns>
    /// <exception cref="ArgumentException">The frames differ in size, format or device.</exception>
    public ulong Copy(Frame source, Frame destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ulong done = VulkanCopy.Record(_work, source, destination);
            FFmpegError.ThrowIfError(
                av_frame_copy_props(destination.NativePointer, source.NativePointer)
            );
            return done;
        }
    }

    /// <summary>Whether the copy that completes at <paramref name="point"/> has run.</summary>
    /// <param name="point">A value from <see cref="Copy"/>.</param>
    /// <returns>True once the GPU has finished it.</returns>
    public bool IsComplete(ulong point)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _work.Completed >= point;
        }
    }

    /// <summary>Waits for the copies already queued, then releases the copier's queue and command pool.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _work.Dispose();
            _device.Dispose();
        }
    }
}
