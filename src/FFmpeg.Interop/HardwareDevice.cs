using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Versioning;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>
/// An open hardware acceleration device (<see cref="AVHWDeviceContext"/>): a GPU or media engine that
/// decoders, encoders and frame pools use.
/// </summary>
/// <remarks>
/// The device is reference counted; codecs and pools created from it keep it alive after this instance
/// is disposed.
/// </remarks>
public sealed unsafe class HardwareDevice : IDisposable
{
    private readonly SafeBufferHandle _reference;

    private HardwareDevice(SafeBufferHandle reference) => _reference = reference;

    /// <summary>The native <c>AVBufferRef</c> holding the device context.</summary>
    public AVBufferRef* NativePointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_reference.IsClosed, this);
            return _reference.Pointer;
        }
    }

    /// <summary>The native device context.</summary>
    public AVHWDeviceContext* Context => (AVHWDeviceContext*)NativePointer->data;

    /// <summary>The device type.</summary>
    public HardwareDeviceType Type => Context->type;

    /// <summary>Opens a device.</summary>
    /// <param name="type">The device type.</param>
    /// <param name="device">
    /// Which device, in the type's own terms: an adapter index for D3D11VA, a DRM render node such as
    /// <c>/dev/dri/renderD128</c> for VA-API, a device index for CUDA and Vulkan; null for the default.
    /// </param>
    /// <param name="options">Type-specific options, as FFmpeg's <c>-init_hw_device</c> takes them.</param>
    /// <returns>The device.</returns>
    /// <exception cref="FFmpegException">The device could not be opened.</exception>
    public static HardwareDevice Create(
        HardwareDeviceType type,
        string? device = null,
        IReadOnlyDictionary<string, string>? options = null
    )
    {
        int result = TryCreateCore(type, device, options, out HardwareDevice? created);
        FFmpegError.ThrowIfError(result, "av_hwdevice_ctx_create");
        return created!;
    }

    /// <summary>
    /// Opens a device of a type on a specific GPU. FFmpeg selects the GPU differently for every device
    /// type, and its numberings do not agree (DXGI, Vulkan and CUDA each order GPUs their own way), so
    /// the adapter is resolved per type: D3D11VA and D3D12VA by DXGI index; AMF and QSV derived from a
    /// D3D11VA device (VA-API on Linux) on the adapter, because opening them directly takes the default
    /// GPU; Vulkan by the adapter's LUID on Windows and derived from its DRM node on Linux; CUDA derived
    /// from Vulkan, which FFmpeg matches by UUID; VA-API and DRM by render node.
    /// </summary>
    /// <param name="type">The device type.</param>
    /// <param name="adapter">The GPU, from <see cref="GpuAdapter.Enumerate"/>.</param>
    /// <param name="options">Type-specific options, as FFmpeg's <c>-init_hw_device</c> takes them.</param>
    /// <returns>The device.</returns>
    /// <exception cref="NotSupportedException">The type cannot be opened on a chosen GPU on this platform.</exception>
    /// <exception cref="FFmpegException">The device could not be opened on that GPU.</exception>
    public static HardwareDevice Create(
        HardwareDeviceType type,
        GpuAdapter adapter,
        IReadOnlyDictionary<string, string>? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(adapter);
        HardwareDevice? device = null;
        foreach (DeviceStep step in Route(type, adapter, OperatingSystem.IsWindows()))
        {
            HardwareDevice? previous = device;
            device = step switch
            {
                OpenDevice open => Create(open.Type, open.Selector, options),
                OpenVulkanByLuid vulkan => CreateVulkan(vulkan.Luid, adapter, options),
                DeriveDevice derive => previous!.Derive(derive.Type),
            };

            // A derived device holds a reference to the one it came from, so the parent can go.
            previous?.Dispose();
        }

        return device!;
    }

    // How a device type is opened on a GPU. Pure, so every platform's route is tested everywhere; only
    // following it needs the GPU.
    internal static List<DeviceStep> Route(
        HardwareDeviceType type,
        GpuAdapter adapter,
        bool windows
    )
    {
        if (type == HardwareDeviceType.D3D11VA || type == HardwareDeviceType.D3D12VA)
        {
            return
            [
                new OpenDevice(type, DxgiIndex(adapter).ToString(CultureInfo.InvariantCulture)),
            ];
        }

        if (type == HardwareDeviceType.Vaapi || type == HardwareDeviceType.Drm)
        {
            return [new OpenDevice(type, adapter.RenderNode ?? throw NotOn(type, adapter))];
        }

        // Opened directly, AMF and QSV take the default GPU; derived, they take their parent's.
        if (type == HardwareDeviceType.Amf || type == HardwareDeviceType.Qsv)
        {
            return
            [
                .. Route(
                    windows ? HardwareDeviceType.D3D11VA : HardwareDeviceType.Vaapi,
                    adapter,
                    windows
                ),
                new DeriveDevice(type),
            ];
        }

        if (type == HardwareDeviceType.Vulkan)
        {
            return windows
                ? [new OpenVulkanByLuid(adapter.Luid ?? throw NotOn(type, adapter))]
                : [.. Route(HardwareDeviceType.Drm, adapter, windows), new DeriveDevice(type)];
        }

        // FFmpeg derives CUDA from Vulkan by matching the device UUID, which fixes the GPU exactly.
        if (type == HardwareDeviceType.Cuda)
        {
            return [.. Route(HardwareDeviceType.Vulkan, adapter, windows), new DeriveDevice(type)];
        }

        if (type == HardwareDeviceType.VideoToolbox)
        {
            return [new OpenDevice(type, null)];
        }

        throw NotOn(type, adapter);
    }

    /// <summary>
    /// Wraps a Direct3D 11 device the application already uses, so frames it renders or captures can be
    /// encoded without leaving the GPU. The device should have multithread protection enabled
    /// (<c>ID3D10Multithread::SetMultithreadProtected</c>) if the application uses it concurrently.
    /// </summary>
    /// <param name="device">The <c>ID3D11Device*</c>. It is AddRef'd; FFmpeg releases it when the device is freed.</param>
    /// <returns>The device.</returns>
    [SupportedOSPlatform("windows")]
    public static HardwareDevice FromD3D11Device(nint device) =>
        Wrap(HardwareDeviceType.D3D11VA, device);

    /// <summary>Wraps a Direct3D 12 device the application already uses.</summary>
    /// <param name="device">The <c>ID3D12Device*</c>. It is AddRef'd; FFmpeg releases it when the device is freed.</param>
    /// <returns>The device.</returns>
    [SupportedOSPlatform("windows")]
    public static HardwareDevice FromD3D12Device(nint device) =>
        Wrap(HardwareDeviceType.D3D12VA, device);

    /// <summary>Opens a device if this machine has one of the type.</summary>
    /// <param name="type">The device type.</param>
    /// <param name="device">The opened device, or null.</param>
    /// <returns>Whether a device was opened.</returns>
    public static bool TryCreate(HardwareDeviceType type, out HardwareDevice? device) =>
        TryCreateCore(type, null, null, out device) >= 0;

    /// <summary>
    /// Opens a device of another type on the same hardware, sharing it: a VA-API device derived from a
    /// DRM device, or a Vulkan device derived from a D3D11VA or VA-API one, lets frames be mapped between
    /// the two without a copy.
    /// </summary>
    /// <param name="type">The device type to derive.</param>
    /// <returns>The derived device.</returns>
    public HardwareDevice Derive(HardwareDeviceType type)
    {
        AVBufferRef* derived = null;
        FFmpegError.ThrowIfError(av_hwdevice_ctx_create_derived(&derived, type, NativePointer, 0));
        return new(SafeBufferHandle.Own(derived, "av_hwdevice_ctx_create_derived"));
    }

    /// <summary>The Direct3D 11 objects behind a <see cref="HardwareDeviceType.D3D11VA"/> device.</summary>
    /// <param name="device">The COM pointers, not AddRef'd: valid while the device is open.</param>
    /// <returns>Whether the device is a D3D11VA device.</returns>
    [SupportedOSPlatform("windows")]
    public bool TryGetD3D11(out D3D11Device device)
    {
        if (Type != HardwareDeviceType.D3D11VA)
        {
            device = default;
            return false;
        }

        AVD3D11VADeviceContext* context = (AVD3D11VADeviceContext*)Context->hwctx;
        device = new(
            (nint)context->device,
            (nint)context->device_context,
            (nint)context->video_device,
            (nint)context->video_context
        );
        return true;
    }

    /// <summary>The Direct3D 12 objects behind a <see cref="HardwareDeviceType.D3D12VA"/> device.</summary>
    /// <param name="device">The COM pointers, not AddRef'd: valid while the device is open.</param>
    /// <returns>Whether the device is a D3D12VA device.</returns>
    [SupportedOSPlatform("windows")]
    public bool TryGetD3D12(out D3D12Device device)
    {
        if (Type != HardwareDeviceType.D3D12VA)
        {
            device = default;
            return false;
        }

        AVD3D12VADeviceContext* context = (AVD3D12VADeviceContext*)Context->hwctx;
        device = new((nint)context->device, (nint)context->video_device);
        return true;
    }

    /// <summary>The <c>VADisplay</c> of a <see cref="HardwareDeviceType.Vaapi"/> device.</summary>
    /// <param name="display">The display.</param>
    /// <returns>Whether the device is a VA-API device.</returns>
    [SupportedOSPlatform("linux")]
    public bool TryGetVaapiDisplay(out nint display)
    {
        display =
            Type == HardwareDeviceType.Vaapi
                ? (nint)((AVVAAPIDeviceContext*)Context->hwctx)->display
                : 0;
        return display != 0;
    }

    /// <summary>The DRM file descriptor of a <see cref="HardwareDeviceType.Drm"/> device.</summary>
    /// <param name="fileDescriptor">The descriptor, owned by the device.</param>
    /// <returns>Whether the device is a DRM device.</returns>
    [SupportedOSPlatform("linux")]
    public bool TryGetDrmFileDescriptor(out int fileDescriptor)
    {
        bool isDrm = Type == HardwareDeviceType.Drm;
        fileDescriptor = isDrm ? ((AVDRMDeviceContext*)Context->hwctx)->fd : -1;
        return isDrm;
    }

    /// <summary>The Vulkan handles of a <see cref="HardwareDeviceType.Vulkan"/> device.</summary>
    /// <param name="device">The instance, physical device and logical device.</param>
    /// <returns>Whether the device is a Vulkan device.</returns>
    public bool TryGetVulkan(out VulkanDevice device)
    {
        if (Type != HardwareDeviceType.Vulkan)
        {
            device = default;
            return false;
        }

        AVVulkanDeviceContext* context = (AVVulkanDeviceContext*)Context->hwctx;
        device = new((nint)context->inst, (nint)context->phys_dev, (nint)context->act_dev);
        return true;
    }

    /// <summary>The <c>CUcontext</c> of a <see cref="HardwareDeviceType.Cuda"/> device.</summary>
    /// <param name="context">The context.</param>
    /// <returns>Whether the device is a CUDA device.</returns>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    public bool TryGetCudaContext(out nint context)
    {
        context =
            Type == HardwareDeviceType.Cuda
                ? (nint)((AVCUDADeviceContext*)Context->hwctx)->cuda_ctx
                : 0;
        return context != 0;
    }

    /// <summary>
    /// The pixel format of this device's surfaces, for example <see cref="PixelFormat.D3D11"/>, as FFmpeg
    /// reports it in the device's frame constraints.
    /// </summary>
    public PixelFormat SurfaceFormat =>
        GetFrameConstraints().HardwareFormats is [PixelFormat first, ..]
            ? first
            : throw new NotSupportedException($"A {Type} device has no surface format of its own.");

    /// <summary>What frames this device can hold: formats and size limits.</summary>
    /// <returns>The constraints.</returns>
    public HardwareFrameConstraints GetFrameConstraints()
    {
        AVHWFramesConstraints* constraints = av_hwdevice_get_hwframe_constraints(
            NativePointer,
            null
        );
        if (constraints is null)
        {
            FFmpegError.ThrowOutOfMemory("av_hwdevice_get_hwframe_constraints");
        }

        try
        {
            return new(
                Formats(constraints->valid_hw_formats),
                Formats(constraints->valid_sw_formats),
                constraints->min_width,
                constraints->min_height,
                constraints->max_width,
                constraints->max_height
            );
        }
        finally
        {
            av_hwframe_constraints_free(&constraints);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _reference.Dispose();

    internal AVBufferRef* NewReference() => _reference.NewReference();

    private static int TryCreateCore(
        HardwareDeviceType type,
        string? device,
        IReadOnlyDictionary<string, string>? options,
        out HardwareDevice? created
    )
    {
        AVBufferRef* reference = null;
        AVDictionary* dictionary = NativeOptions.Create(options);
        try
        {
            using Utf8String name = new(device);
            int result = av_hwdevice_ctx_create(&reference, type, name, dictionary, 0);
            created =
                result < 0
                    ? null
                    : new HardwareDevice(SafeBufferHandle.Own(reference, "av_hwdevice_ctx_create"));
            return result;
        }
        finally
        {
            av_dict_free(&dictionary);
        }
    }

    // The locally unique identifier of the GPU behind a Vulkan device, which DXGI reports for the same GPU.
    internal bool TryGetLuid(out long luid)
    {
        luid = 0;
        if (Type != HardwareDeviceType.Vulkan)
        {
            return false;
        }

        AVVulkanDeviceContext* context = (AVVulkanDeviceContext*)Context->hwctx;
        using Utf8String name = new("vkGetPhysicalDeviceProperties2");
        delegate* unmanaged[Stdcall]<void*, VkPhysicalDeviceProperties2*, void> getProperties =
            (delegate* unmanaged[Stdcall]<void*, VkPhysicalDeviceProperties2*, void>)
                context->get_proc_addr(context->inst, name);
        if (getProperties is null)
        {
            return false;
        }

        VkPhysicalDeviceIDProperties identity = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES,
        };
        VkPhysicalDeviceProperties2 properties = new()
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2,
            pNext = &identity,
        };
        getProperties(context->phys_dev, &properties);
        if (identity.deviceLUIDValid == 0)
        {
            return false;
        }

        luid = BinaryPrimitives.ReadInt64LittleEndian(identity.deviceLUID);
        return true;
    }

    // Vulkan numbers GPUs its own way and FFmpeg selects only by that index or by name, which two
    // identical cards share. Each Vulkan GPU is opened in turn until its LUID is the adapter's.
    private static HardwareDevice CreateVulkan(
        long luid,
        GpuAdapter adapter,
        IReadOnlyDictionary<string, string>? options
    )
    {
        for (int index = 0; ; index++)
        {
            if (
                TryCreateCore(
                    HardwareDeviceType.Vulkan,
                    index.ToString(CultureInfo.InvariantCulture),
                    options,
                    out HardwareDevice? candidate
                ) < 0
            )
            {
                throw new NotSupportedException($"No Vulkan device is {adapter}.");
            }

            if (candidate!.TryGetLuid(out long candidateLuid) && candidateLuid == luid)
            {
                return candidate;
            }

            candidate.Dispose();
        }
    }

    private static int DxgiIndex(GpuAdapter adapter) =>
        adapter.DxgiIndex ?? throw NotOn(HardwareDeviceType.D3D11VA, adapter);

    private static NotSupportedException NotOn(HardwareDeviceType type, GpuAdapter adapter) =>
        new($"A {type} device cannot be opened on {adapter} on this platform.");

    [SupportedOSPlatform("windows")]
    private static HardwareDevice Wrap(HardwareDeviceType type, nint device)
    {
        if (device == 0)
        {
            throw new ArgumentNullException(nameof(device));
        }

        AVBufferRef* reference = av_hwdevice_ctx_alloc(type);
        SafeBufferHandle owner = SafeBufferHandle.Own(reference, "av_hwdevice_ctx_alloc");
        try
        {
            _ = Dxgi.AddRef(device);
            void* context = ((AVHWDeviceContext*)reference->data)->hwctx;
            if (type == HardwareDeviceType.D3D11VA)
            {
                ((AVD3D11VADeviceContext*)context)->device = (void*)device;
            }
            else
            {
                ((AVD3D12VADeviceContext*)context)->device = (void*)device;
            }

            FFmpegError.ThrowIfError(av_hwdevice_ctx_init(reference));
            return new HardwareDevice(owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private static PixelFormat[] Formats(AVPixelFormat* list)
    {
        if (list is null)
        {
            return [];
        }

        int count = 0;
        while (list[count] != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            count++;
        }

        return [.. new ReadOnlySpan<PixelFormat>(list, count)];
    }
}

/// <summary>The Direct3D 11 objects of a D3D11VA device.</summary>
/// <param name="Device">The <c>ID3D11Device*</c>.</param>
/// <param name="DeviceContext">The <c>ID3D11DeviceContext*</c>.</param>
/// <param name="VideoDevice">The <c>ID3D11VideoDevice*</c>.</param>
/// <param name="VideoContext">The <c>ID3D11VideoContext*</c>.</param>
public readonly record struct D3D11Device(
    nint Device,
    nint DeviceContext,
    nint VideoDevice,
    nint VideoContext
);

/// <summary>The Direct3D 12 objects of a D3D12VA device.</summary>
/// <param name="Device">The <c>ID3D12Device*</c>.</param>
/// <param name="VideoDevice">The <c>ID3D12VideoDevice*</c>.</param>
public readonly record struct D3D12Device(nint Device, nint VideoDevice);

/// <summary>The Vulkan handles of a Vulkan device.</summary>
/// <param name="Instance">The <c>VkInstance</c>.</param>
/// <param name="PhysicalDevice">The <c>VkPhysicalDevice</c>.</param>
/// <param name="Device">The <c>VkDevice</c>.</param>
public readonly record struct VulkanDevice(nint Instance, nint PhysicalDevice, nint Device);

/// <summary>What frames a hardware device can hold.</summary>
/// <param name="HardwareFormats">The hardware pixel formats.</param>
/// <param name="SoftwareFormats">The system-memory formats frames can be uploaded from or downloaded to.</param>
/// <param name="MinWidth">The minimum width.</param>
/// <param name="MinHeight">The minimum height.</param>
/// <param name="MaxWidth">The maximum width.</param>
/// <param name="MaxHeight">The maximum height.</param>
public sealed record HardwareFrameConstraints(
    IReadOnlyList<PixelFormat> HardwareFormats,
    IReadOnlyList<PixelFormat> SoftwareFormats,
    int MinWidth,
    int MinHeight,
    int MaxWidth,
    int MaxHeight
);
