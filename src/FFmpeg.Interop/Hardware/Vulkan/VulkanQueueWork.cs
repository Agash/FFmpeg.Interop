using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

// Records and submits small pieces of GPU work (barriers, copies) on one of the queues FFmpeg opened on
// a device, inside FFmpeg's synchronization: each submission waits for the frames' timeline semaphores
// and signals their next values, so FFmpeg's own work on the frames is ordered around it. Command
// buffers are reused once the GPU is done with them, which a timeline semaphore of this object's own
// tracks. Not thread-safe; its owners serialise their calls.
internal sealed unsafe class VulkanQueueWork : IDisposable
{
    private const uint ResetCommandBufferBit = 0x2;

    private readonly AVHWDeviceContext* _device;
    private readonly AVVulkanDeviceContext* _context;
    private readonly VulkanFunctions _vk;
    private readonly void* _queue;
    private readonly void* _pool;
    private readonly void* _done;
    private readonly Queue<(ulong Value, nint Commands)> _pending = new();
    private readonly Stack<nint> _free = new();
    private ulong _submitted;
    private bool _disposed;

    public VulkanQueueWork(AVHWDeviceContext* device)
    {
        _device = device;
        _context = (AVVulkanDeviceContext*)device->hwctx;
        _vk = new VulkanFunctions(_context);
        Family = TransferFamily(_context);

        // Queues FFmpeg created internally synchronized are only found with the flags they were created
        // with.
        VkDeviceQueueInfo2 queueInfo = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_DEVICE_QUEUE_INFO_2,
            flags = _context->queue_flags,
            queueFamilyIndex = Family,
            queueIndex = 0,
        };
        void* queue;
        _vk.GetDeviceQueue2(_context->act_dev, &queueInfo, &queue);
        _queue = queue;

        VkCommandPoolCreateInfo poolInfo = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
            flags =
                (uint)VkCommandPoolCreateFlagBits.VK_COMMAND_POOL_CREATE_TRANSIENT_BIT
                | (uint)VkCommandPoolCreateFlagBits.VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
            queueFamilyIndex = Family,
        };
        void* pool;
        VulkanFunctions.Check(
            _vk.CreateCommandPool(_context->act_dev, &poolInfo, _context->alloc, &pool),
            "vkCreateCommandPool"
        );
        _pool = pool;
        try
        {
            _done = Timeline(_vk, _context);
        }
        catch
        {
            _vk.DestroyCommandPool(_context->act_dev, _pool, _context->alloc);
            throw;
        }
    }

    // The queue family the work runs on.
    public uint Family { get; }

    public VulkanFunctions Functions => _vk;

    public AVVulkanDeviceContext* Context => _context;

    // A timeline semaphore starting at zero, on the device.
    public static void* Timeline(VulkanFunctions vk, AVVulkanDeviceContext* context)
    {
        VkSemaphoreTypeCreateInfo type = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO,
            semaphoreType = VkSemaphoreType.VK_SEMAPHORE_TYPE_TIMELINE,
        };
        VkSemaphoreCreateInfo info = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
            pNext = &type,
        };
        void* semaphore;
        VulkanFunctions.Check(
            vk.CreateSemaphore(context->act_dev, &info, context->alloc, &semaphore),
            "vkCreateSemaphore"
        );
        return semaphore;
    }

    // A command buffer to record into, begun for one submission.
    public void* Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Reclaim();
        void* commands;
        if (_free.TryPop(out nint reused))
        {
            commands = (void*)reused;
            VulkanFunctions.Check(_vk.ResetCommandBuffer(commands, 0), "vkResetCommandBuffer");
        }
        else
        {
            VkCommandBufferAllocateInfo allocate = new()
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                commandPool = _pool,
                level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                commandBufferCount = 1,
            };
            VulkanFunctions.Check(
                _vk.AllocateCommandBuffers(_context->act_dev, &allocate, &commands),
                "vkAllocateCommandBuffers"
            );
        }

        VkCommandBufferBeginInfo begin = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            flags = (uint)VkCommandBufferUsageFlagBits.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
        };
        VulkanFunctions.Check(_vk.BeginCommandBuffer(commands, &begin), "vkBeginCommandBuffer");
        return commands;
    }

    // Records image barriers into a begun command buffer.
    public void Barriers(
        void* commands,
        ReadOnlySpan<VkImageMemoryBarrier> barriers,
        VkPipelineStageFlagBits sourceStages,
        VkPipelineStageFlagBits destinationStages
    )
    {
        fixed (VkImageMemoryBarrier* barrier = barriers)
        {
            _vk.CmdPipelineBarrier(
                commands,
                (uint)sourceStages,
                (uint)destinationStages,
                0,
                0,
                null,
                0,
                null,
                (uint)barriers.Length,
                barrier
            );
        }
    }

    // Ends and submits a command buffer after the frames' pending work and any other timeline points,
    // signalling each image's next timeline value; returns the value of this object's timeline the work
    // completes at.
    public ulong Submit(
        void* commands,
        ReadOnlySpan<nint> frames,
        ReadOnlySpan<(nint Semaphore, ulong Value)> after = default
    )
    {
        VulkanFunctions.Check(_vk.EndCommandBuffer(commands), "vkEndCommandBuffer");
        int images = 0;
        foreach (nint frame in frames)
        {
            images += Images((AVVkFrame*)frame);
        }

        int waited = images + after.Length;
        int count = waited + 1;
        void** semaphores = stackalloc void*[count];
        ulong* waits = stackalloc ulong[count];
        ulong* signals = stackalloc ulong[count];
        uint* stages = stackalloc uint[count];
        int n = 0;
        foreach (nint frame in frames)
        {
            AVVkFrame* vkf = (AVVkFrame*)frame;
            for (int i = 0; i < Images(vkf); i++, n++)
            {
                semaphores[n] = vkf->sem[i];
                waits[n] = vkf->sem_value[i];
                signals[n] = ++vkf->sem_value[i];
                stages[n] = (uint)VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT;
            }
        }

        // The other points are waited on only; their entries in the signal list are skipped below.
        foreach ((nint semaphore, ulong value) in after)
        {
            semaphores[n] = (void*)semaphore;
            waits[n] = value;
            stages[n] = (uint)VkPipelineStageFlagBits.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT;
            n++;
        }

        ulong done = ++_submitted;
        void** signalled = stackalloc void*[images + 1];
        for (int i = 0; i < images; i++)
        {
            signalled[i] = semaphores[i];
        }

        signalled[images] = _done;
        signals[images] = done;

        // The work's own timeline is only signalled, so the wait list leaves it out.
        VkTimelineSemaphoreSubmitInfo timeline = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO,
            waitSemaphoreValueCount = (uint)waited,
            pWaitSemaphoreValues = waits,
            signalSemaphoreValueCount = (uint)(images + 1),
            pSignalSemaphoreValues = signals,
        };
        VkSubmitInfo submit = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SUBMIT_INFO,
            pNext = &timeline,
            waitSemaphoreCount = (uint)waited,
            pWaitSemaphores = semaphores,
            pWaitDstStageMask = stages,
            commandBufferCount = 1,
            pCommandBuffers = &commands,
            signalSemaphoreCount = (uint)(images + 1),
            pSignalSemaphores = signalled,
        };

        // FFmpeg 9 still locks every queue around its own submissions, so this does too.
#pragma warning disable CS0612 // Deprecated for VK_KHR_internally_synchronized_queues, which FFmpeg 9 itself still pairs with these locks.
        _context->lock_queue(_device, Family, 0);
        try
        {
            VulkanFunctions.Check(_vk.QueueSubmit(_queue, 1, &submit, null), "vkQueueSubmit");
        }
        finally
        {
            _context->unlock_queue(_device, Family, 0);
        }
#pragma warning restore CS0612

        _pending.Enqueue((done, (nint)commands));
        return done;
    }

    // The value of this object's timeline the GPU has reached.
    public ulong Completed
    {
        get
        {
            ulong value;
            VulkanFunctions.Check(
                _vk.GetSemaphoreCounterValue(_context->act_dev, _done, &value),
                "vkGetSemaphoreCounterValue"
            );
            return value;
        }
    }

    // Blocks until the work that completes at a value of this object's timeline has run.
    public void Wait(ulong value)
    {
        void* done = _done;
        VkSemaphoreWaitInfo wait = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_WAIT_INFO,
            semaphoreCount = 1,
            pSemaphores = &done,
            pValues = &value,
        };
        VulkanFunctions.Check(
            _vk.WaitSemaphores(_context->act_dev, &wait, ulong.MaxValue),
            "vkWaitSemaphores"
        );
    }

    public static int Images(AVVkFrame* frame)
    {
        int count = 0;
        while (count < 8 && frame->img[count] is not null)
        {
            count++;
        }

        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Wait(_submitted);
        _vk.DestroyCommandPool(_context->act_dev, _pool, _context->alloc);
        _vk.DestroySemaphore(_context->act_dev, _done, _context->alloc);
    }

    private void Reclaim()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        ulong completed;
        VulkanFunctions.Check(
            _vk.GetSemaphoreCounterValue(_context->act_dev, _done, &completed),
            "vkGetSemaphoreCounterValue"
        );
        while (
            _pending.TryPeek(out (ulong Value, nint Commands) oldest) && oldest.Value <= completed
        )
        {
            _ = _pending.Dequeue();
            _free.Push(oldest.Commands);
        }
    }

    // The first queue family FFmpeg opened that can transfer; graphics and compute queues always can.
    private static uint TransferFamily(AVVulkanDeviceContext* context)
    {
        const VkQueueFlagBits transfer =
            VkQueueFlagBits.VK_QUEUE_TRANSFER_BIT
            | VkQueueFlagBits.VK_QUEUE_COMPUTE_BIT
            | VkQueueFlagBits.VK_QUEUE_GRAPHICS_BIT;
        for (int i = 0; i < context->nb_qf; i++)
        {
            AVVulkanDeviceQueueFamily queues = context->qf[i];
            if (queues.num > 0 && (queues.flags & transfer) != 0)
            {
                return (uint)queues.idx;
            }
        }

        throw new NotSupportedException("The Vulkan device has no queue that can copy images.");
    }
}
