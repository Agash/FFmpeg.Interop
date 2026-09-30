using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Security;

namespace FFmpeg.Interop;

// The GPU work a D3D12 pool does on its own behalf: a queue that orders imports after other queues'
// fences, and a copy queue with one command list for copying textures into the pool's surfaces.
// Created on first use and released with the pool.
[SupportedOSPlatform("windows10.0.10240")]
internal sealed unsafe class D3D12PoolWork(nint device) : IDisposable
{
    private readonly Lock _gate = new();
    private nint _orderingQueue;
    private ID3D12CommandQueue* _copyQueue;
    private ID3D12CommandAllocator* _allocator;
    private ID3D12GraphicsCommandList* _list;
    private ID3D12Fence* _copied;
    private HANDLE _copiedEvent;
    private ulong _submitted;

    // A queue that only waits and signals, for ordering one queue's work after another's.
    public nint OrderingQueue
    {
        get
        {
            lock (_gate)
            {
                return _orderingQueue != 0
                    ? _orderingQueue
                    : _orderingQueue = D3D12.CreateOrderingQueue(device);
            }
        }
    }

    // Copies the top-left of a source texture's subresource into a surface of the pool, on the GPU:
    // after the surface's last user is done with it and the source is ready, before the surface's
    // fence reaches its next value.
    public void Copy(
        nint source,
        int sourceSubresource,
        int sourceArraySize,
        nint readyFence,
        ulong readyValue,
        AVD3D12VAFrame* target,
        int targetArraySize,
        int planes,
        int width,
        int height
    )
    {
        lock (_gate)
        {
            EnsureCopier();

            // One allocator: the previous copy must be finished before it is reset. Copies are short,
            // so this is normally already so.
            if (_copied->GetCompletedValue() < _submitted)
            {
                _copied->SetEventOnCompletion(_submitted, _copiedEvent);
                _ = Win32.WaitForSingleObject(_copiedEvent, Win32.INFINITE);
            }

            _allocator->Reset();
            _list->Reset(_allocator, null);
            for (int plane = 0; plane < planes; plane++)
            {
                // Planes past the first (NV12's chroma) are subsampled by two in both directions.
                uint planeWidth = (uint)(plane == 0 ? width : (width + 1) / 2);
                uint planeHeight = (uint)(plane == 0 ? height : (height + 1) / 2);
                D3D12_TEXTURE_COPY_LOCATION from = new()
                {
                    pResource = (ID3D12Resource*)source,
                    Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
                };
                from.Anonymous.SubresourceIndex = (uint)(
                    sourceSubresource + (plane * sourceArraySize)
                );
                D3D12_TEXTURE_COPY_LOCATION to = new()
                {
                    pResource = (ID3D12Resource*)target->texture,
                    Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
                };
                to.Anonymous.SubresourceIndex = (uint)(
                    target->subresource_index + (plane * targetArraySize)
                );
                D3D12_BOX region = new()
                {
                    right = planeWidth,
                    bottom = planeHeight,
                    back = 1,
                };
                _list->CopyTextureRegion(&to, 0, 0, 0, &from, &region);
            }

            _list->Close();

            ID3D12Fence* surfaceFence = (ID3D12Fence*)target->sync_ctx.fence;
            _copyQueue->Wait(surfaceFence, target->sync_ctx.fence_value);
            if (readyFence != 0)
            {
                _copyQueue->Wait((ID3D12Fence*)readyFence, readyValue);
            }

            ID3D12CommandList* list = (ID3D12CommandList*)_list;
            _copyQueue->ExecuteCommandLists(1, &list);
            target->sync_ctx.fence_value++;
            _copyQueue->Signal(surfaceFence, target->sync_ctx.fence_value);
            _copyQueue->Signal(_copied, ++_submitted);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_copied is not null && _copied->GetCompletedValue() < _submitted)
            {
                _copied->SetEventOnCompletion(_submitted, _copiedEvent);
                _ = Win32.WaitForSingleObject(_copiedEvent, Win32.INFINITE);
            }

            Release(ref _list);
            Release(ref _allocator);
            Release(ref _copyQueue);
            Release(ref _copied);
            if (!_copiedEvent.IsNull)
            {
                _ = Win32.CloseHandle(_copiedEvent);
                _copiedEvent = default;
            }

            if (_orderingQueue != 0)
            {
                D3D12.Release(_orderingQueue);
                _orderingQueue = 0;
            }
        }
    }

    private void EnsureCopier()
    {
        if (_copyQueue is not null)
        {
            return;
        }

        ID3D12Device* d3d12 = (ID3D12Device*)device;
        D3D12_COMMAND_QUEUE_DESC description = new()
        {
            Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COPY,
        };
        d3d12->CreateCommandQueue(in description, out ID3D12CommandQueue* queue);
        _copyQueue = queue;
        d3d12->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COPY,
            out ID3D12CommandAllocator* allocator
        );
        _allocator = allocator;
        d3d12->CreateCommandList(
            0,
            D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_COPY,
            allocator,
            null,
            out ID3D12GraphicsCommandList* list
        );
        _list = list;
        _list->Close();
        d3d12->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, out ID3D12Fence* copied);
        _copied = copied;
        _copiedEvent = Win32.CreateEvent((SECURITY_ATTRIBUTES*)null, false, false, default(PCWSTR));
    }

    private static void Release<T>(ref T* unknown)
        where T : unmanaged
    {
        if (unknown is not null)
        {
            _ = ((ID3D12DeviceChild*)unknown)->Release();
            unknown = null;
        }
    }
}
