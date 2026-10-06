using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

// The Vulkan entry points this library calls on a device FFmpeg opened, resolved through that device so
// they dispatch to its driver. Entry points of extensions the device may lack are null when it does.
internal readonly unsafe struct VulkanFunctions
{
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkCommandPoolCreateInfo*,
        void*,
        void**,
        VkResult> CreateCommandPool;
    public readonly delegate* unmanaged[Stdcall]<void*, void*, void*, void> DestroyCommandPool;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkCommandBufferAllocateInfo*,
        void**,
        VkResult> AllocateCommandBuffers;
    public readonly delegate* unmanaged[Stdcall]<void*, uint, VkResult> ResetCommandBuffer;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkCommandBufferBeginInfo*,
        VkResult> BeginCommandBuffer;
    public readonly delegate* unmanaged[Stdcall]<void*, VkResult> EndCommandBuffer;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        uint,
        uint,
        uint,
        uint,
        void*,
        uint,
        void*,
        uint,
        VkImageMemoryBarrier*,
        void> CmdPipelineBarrier;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        void*,
        VkImageLayout,
        void*,
        VkImageLayout,
        uint,
        VkImageCopy*,
        void> CmdCopyImage;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkDeviceQueueInfo2*,
        void**,
        void> GetDeviceQueue2;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        uint,
        VkSubmitInfo*,
        void*,
        VkResult> QueueSubmit;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkSemaphoreWaitInfo*,
        ulong,
        VkResult> WaitSemaphores;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        void*,
        ulong*,
        VkResult> GetSemaphoreCounterValue;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkSemaphoreCreateInfo*,
        void*,
        void**,
        VkResult> CreateSemaphore;
    public readonly delegate* unmanaged[Stdcall]<void*, void*, void*, void> DestroySemaphore;

    // Null when the device was made without VK_KHR_external_semaphore_fd.
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkImportSemaphoreFdInfoKHR*,
        VkResult> ImportSemaphoreFd;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkImageCreateInfo*,
        void*,
        void**,
        VkResult> CreateImage;
    public readonly delegate* unmanaged[Stdcall]<void*, void*, void*, void> DestroyImage;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkImageMemoryRequirementsInfo2*,
        VkMemoryRequirements2*,
        void> GetImageMemoryRequirements2;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkExternalMemoryHandleTypeFlagBits,
        int,
        VkMemoryFdPropertiesKHR*,
        VkResult> GetMemoryFdProperties;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkMemoryAllocateInfo*,
        void*,
        void**,
        VkResult> AllocateMemory;
    public readonly delegate* unmanaged[Stdcall]<void*, void*, void*, void> FreeMemory;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        void*,
        void*,
        ulong,
        VkResult> BindImageMemory;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkPhysicalDeviceImageFormatInfo2*,
        VkImageFormatProperties2*,
        VkResult> GetPhysicalDeviceImageFormatProperties2;
    public readonly delegate* unmanaged[Stdcall]<
        void*,
        VkFormat,
        VkFormatProperties2*,
        void> GetPhysicalDeviceFormatProperties2;

    public VulkanFunctions(AVVulkanDeviceContext* context)
    {
        var getDeviceProcAddr = (delegate* unmanaged[Stdcall]<void*, sbyte*, void*>)Instance(
            context,
            "vkGetDeviceProcAddr"
        );
        void* Device(string name) => Load(getDeviceProcAddr, context->act_dev, name);

        CreateCommandPool = (delegate* unmanaged[Stdcall]<
            void*,
            VkCommandPoolCreateInfo*,
            void*,
            void**,
            VkResult>)Device("vkCreateCommandPool");
        DestroyCommandPool = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Device(
            "vkDestroyCommandPool"
        );
        AllocateCommandBuffers = (delegate* unmanaged[Stdcall]<
            void*,
            VkCommandBufferAllocateInfo*,
            void**,
            VkResult>)Device("vkAllocateCommandBuffers");
        ResetCommandBuffer = (delegate* unmanaged[Stdcall]<void*, uint, VkResult>)Device(
            "vkResetCommandBuffer"
        );
        BeginCommandBuffer = (delegate* unmanaged[Stdcall]<
            void*,
            VkCommandBufferBeginInfo*,
            VkResult>)Device("vkBeginCommandBuffer");
        EndCommandBuffer = (delegate* unmanaged[Stdcall]<void*, VkResult>)Device(
            "vkEndCommandBuffer"
        );
        CmdPipelineBarrier = (delegate* unmanaged[Stdcall]<
            void*,
            uint,
            uint,
            uint,
            uint,
            void*,
            uint,
            void*,
            uint,
            VkImageMemoryBarrier*,
            void>)Device("vkCmdPipelineBarrier");
        CmdCopyImage = (delegate* unmanaged[Stdcall]<
            void*,
            void*,
            VkImageLayout,
            void*,
            VkImageLayout,
            uint,
            VkImageCopy*,
            void>)Device("vkCmdCopyImage");
        GetDeviceQueue2 = (delegate* unmanaged[Stdcall]<
            void*,
            VkDeviceQueueInfo2*,
            void**,
            void>)Device("vkGetDeviceQueue2");
        QueueSubmit = (delegate* unmanaged[Stdcall]<
            void*,
            uint,
            VkSubmitInfo*,
            void*,
            VkResult>)Device("vkQueueSubmit");
        WaitSemaphores = (delegate* unmanaged[Stdcall]<
            void*,
            VkSemaphoreWaitInfo*,
            ulong,
            VkResult>)Device("vkWaitSemaphores");
        GetSemaphoreCounterValue = (delegate* unmanaged[Stdcall]<
            void*,
            void*,
            ulong*,
            VkResult>)Device("vkGetSemaphoreCounterValue");
        CreateSemaphore = (delegate* unmanaged[Stdcall]<
            void*,
            VkSemaphoreCreateInfo*,
            void*,
            void**,
            VkResult>)Device("vkCreateSemaphore");
        DestroySemaphore = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Device(
            "vkDestroySemaphore"
        );
        ImportSemaphoreFd = (delegate* unmanaged[Stdcall]<
            void*,
            VkImportSemaphoreFdInfoKHR*,
            VkResult>)Optional(getDeviceProcAddr, context->act_dev, "vkImportSemaphoreFdKHR");
        CreateImage = (delegate* unmanaged[Stdcall]<
            void*,
            VkImageCreateInfo*,
            void*,
            void**,
            VkResult>)Device("vkCreateImage");
        DestroyImage = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Device(
            "vkDestroyImage"
        );
        GetImageMemoryRequirements2 = (delegate* unmanaged[Stdcall]<
            void*,
            VkImageMemoryRequirementsInfo2*,
            VkMemoryRequirements2*,
            void>)Device("vkGetImageMemoryRequirements2");
        GetMemoryFdProperties = (delegate* unmanaged[Stdcall]<
            void*,
            VkExternalMemoryHandleTypeFlagBits,
            int,
            VkMemoryFdPropertiesKHR*,
            VkResult>)Optional(getDeviceProcAddr, context->act_dev, "vkGetMemoryFdPropertiesKHR");
        AllocateMemory = (delegate* unmanaged[Stdcall]<
            void*,
            VkMemoryAllocateInfo*,
            void*,
            void**,
            VkResult>)Device("vkAllocateMemory");
        FreeMemory = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Device(
            "vkFreeMemory"
        );
        BindImageMemory = (delegate* unmanaged[Stdcall]<
            void*,
            void*,
            void*,
            ulong,
            VkResult>)Device("vkBindImageMemory");
        GetPhysicalDeviceImageFormatProperties2 = (delegate* unmanaged[Stdcall]<
            void*,
            VkPhysicalDeviceImageFormatInfo2*,
            VkImageFormatProperties2*,
            VkResult>)Instance(context, "vkGetPhysicalDeviceImageFormatProperties2");
        GetPhysicalDeviceFormatProperties2 = (delegate* unmanaged[Stdcall]<
            void*,
            VkFormat,
            VkFormatProperties2*,
            void>)Instance(context, "vkGetPhysicalDeviceFormatProperties2");
    }

    public static void Check(VkResult result, string call)
    {
        if (result != VkResult.VK_SUCCESS)
        {
            throw new InvalidOperationException($"{call} failed: {result}.");
        }
    }

    private static void* Instance(AVVulkanDeviceContext* context, string name)
    {
        using Utf8String native = new(name);
        void* function = context->get_proc_addr(context->inst, native);
        return function is not null
            ? function
            : throw new NotSupportedException($"The Vulkan instance has no {name}.");
    }

    private static void* Load(
        delegate* unmanaged[Stdcall]<void*, sbyte*, void*> getDeviceProcAddr,
        void* device,
        string name
    ) =>
        Optional(getDeviceProcAddr, device, name) is var function and not null
            ? function
            : throw new NotSupportedException($"The Vulkan device has no {name}.");

    private static void* Optional(
        delegate* unmanaged[Stdcall]<void*, sbyte*, void*> getDeviceProcAddr,
        void* device,
        string name
    )
    {
        using Utf8String native = new(name);
        return getDeviceProcAddr(device, native);
    }
}
