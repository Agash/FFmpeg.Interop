using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Security;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

// The D3D12 calls behind D3D12VAExtensions.
[SupportedOSPlatform("windows10.0.10240")]
internal static unsafe class D3D12
{
    // FFmpeg's D3D12 uninit leaves the device alone; only the free callback it installs on devices it
    // creates releases it. A wrapped device gets this one instead.
    public static delegate* unmanaged[Cdecl]<AVHWDeviceContext*, void> ReleaseDevice =>
        &ReleaseDeviceCallback;

    // The device an object belongs to (not AddRef'd on return: the reference GetDevice takes is dropped).
    public static nint GetDevice(nint deviceChild)
    {
        ((ID3D12DeviceChild*)deviceChild)->GetDevice(out ID3D12Device* device);
        _ = device->Release();
        return (nint)device;
    }

    public static D3D12_RESOURCE_DESC GetDescription(nint resource) =>
        ((ID3D12Resource*)resource)->GetDesc();

    // An AVD3D12VAFrame over the resource, the shape FFmpeg's own D3D12 pool gives its surfaces, owned by
    // the returned buffer. The frame gets a fence of its own: FFmpeg's encoder waits on the frame's fence
    // at its value before reading the resource, then signals the next value on that same fence, so the
    // producer's fence would have its timeline advanced by FFmpeg. The fence reaches 1 when the work
    // submitted to producerQueue so far completes, or at once without a queue.
    public static AVBufferRef* WrapResource(nint device, nint resource, nint producerQueue) =>
        WrapResource(device, resource, producerQueue, readyFence: 0, readyValue: 0);

    // As above, ready once readyFence reaches readyValue instead: producerQueue is then a queue of the
    // pool's own that waits for the producer's fence on the GPU and signals the frame's fence, so the
    // producer's timeline is only read, never advanced.
    public static AVBufferRef* WrapResource(
        nint device,
        nint resource,
        nint producerQueue,
        nint readyFence,
        ulong readyValue
    )
    {
        AVD3D12VAFrame* frame = (AVD3D12VAFrame*)av_mallocz((nuint)sizeof(AVD3D12VAFrame));
        if (frame is null)
        {
            FFmpegError.ThrowOutOfMemory("av_mallocz");
        }

        try
        {
            ((ID3D12Device*)device)->CreateFence(
                0,
                D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE,
                out ID3D12Fence* fence
            );
            frame->sync_ctx.fence = fence;

            // Consumers wait for the fence with SetEventOnCompletion on this event.
            HANDLE completion = Win32.CreateEvent(
                (SECURITY_ATTRIBUTES*)null,
                false,
                false,
                default(PCWSTR)
            );
            if (completion.IsNull)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }

            frame->sync_ctx.@event = (void*)completion.Value;
            frame->sync_ctx.fence_value = 1;
            if (readyFence != 0)
            {
                ((ID3D12CommandQueue*)producerQueue)->Wait((ID3D12Fence*)readyFence, readyValue);
                ((ID3D12CommandQueue*)producerQueue)->Signal(fence, 1);
            }
            else if (producerQueue != 0)
            {
                ((ID3D12CommandQueue*)producerQueue)->Signal(fence, 1);
            }
            else
            {
                fence->Signal(1);
            }

            _ = ((ID3D12Resource*)resource)->AddRef();
            frame->texture = (void*)resource;
        }
        catch
        {
            Release(frame);
            throw;
        }

        AVBufferRef* buffer = av_buffer_create(
            (byte*)frame,
            (nuint)sizeof(AVD3D12VAFrame),
            &FreeFrame,
            null,
            0
        );
        if (buffer is null)
        {
            Release(frame);
            FFmpegError.ThrowOutOfMemory("av_buffer_create");
        }

        return buffer;
    }

    // A command queue on a device that only waits and signals fences, for ordering one queue's work after
    // another's without the CPU.
    public static nint CreateOrderingQueue(nint device)
    {
        D3D12_COMMAND_QUEUE_DESC description = new()
        {
            Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COMPUTE,
        };
        Guid iid = ID3D12CommandQueue.IID_Guid;
        void* queue;
        ((ID3D12Device*)device)->CreateCommandQueue(&description, &iid, &queue);
        return (nint)queue;
    }

    public static void Release(nint unknown) => _ = ((ID3D12DeviceChild*)unknown)->Release();

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FreeFrame(void* opaque, byte* data) => Release((AVD3D12VAFrame*)data);

    private static void Release(AVD3D12VAFrame* frame)
    {
        if (frame->texture is not null)
        {
            _ = ((ID3D12Resource*)frame->texture)->Release();
        }

        if (frame->sync_ctx.fence is not null)
        {
            _ = ((ID3D12Fence*)frame->sync_ctx.fence)->Release();
        }

        if (frame->sync_ctx.@event is not null)
        {
            _ = Win32.CloseHandle(new HANDLE(frame->sync_ctx.@event));
        }

        av_free(frame);
    }
}
