using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

// Lends managed memory to FFmpeg without copying. The memory is pinned for as long as FFmpeg holds
// any reference to the buffer; FFmpeg's free callback, which runs when the last reference goes, unpins
// it. FFmpeg may keep a reference past the call that took it (an encoder queueing frames, a muxer
// interleaving packets), so the pin cannot be scoped to the call.
internal static unsafe class ManagedBuffer
{
    public static AVBufferRef* Create(ReadOnlyMemory<byte> memory, bool readOnly)
    {
        MemoryHandle pin = memory.Pin();
        GCHandle<PinOwner> owner = new(new PinOwner(pin));
        AVBufferRef* buffer = LibAVUtil.av_buffer_create(
            (byte*)pin.Pointer,
            (nuint)memory.Length,
            &Release,
            (void*)GCHandle<PinOwner>.ToIntPtr(owner),
            readOnly ? LibAVUtil.AV_BUFFER_FLAG_READONLY : 0
        );

        if (buffer is null)
        {
            owner.Dispose();
            pin.Dispose();
            FFmpegError.ThrowOutOfMemory("av_buffer_create");
        }

        return buffer;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(void* opaque, byte* data)
    {
        GCHandle<PinOwner> owner = GCHandle<PinOwner>.FromIntPtr((nint)opaque);
        owner.Target.Pin.Dispose();
        owner.Dispose();
    }

    private sealed class PinOwner(MemoryHandle pin)
    {
        public MemoryHandle Pin = pin;
    }
}

/// <summary>
/// A reference to memory FFmpeg allocated, held open independently of the frame or packet it came
/// from. Disposing drops the reference; FFmpeg frees the memory when no reference remains.
/// </summary>
public sealed unsafe class NativeMemoryLease : IDisposable
{
    private readonly Manager _manager;

    internal NativeMemoryLease(SafeBufferHandle buffer, byte* data, int length)
    {
        _manager = new Manager(buffer, data, length);
        Memory = _manager.Memory;
    }

    /// <summary>The memory. Valid until the lease is disposed.</summary>
    public ReadOnlyMemory<byte> Memory { get; }

    /// <inheritdoc/>
    public void Dispose() => ((IDisposable)_manager).Dispose();

    private sealed class Manager(SafeBufferHandle buffer, byte* data, int length)
        : MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(buffer.IsClosed, this);
            return new Span<byte>(data, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(buffer.IsClosed, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                (uint)elementIndex,
                (uint)length,
                nameof(elementIndex)
            );
            // Native memory needs no pinning, but passing this as the pinnable keeps MemoryHandle's
            // contract: disposing the handle calls Unpin.
            return new MemoryHandle(data + elementIndex, default, this);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                buffer.Dispose();
            }
        }
    }
}
