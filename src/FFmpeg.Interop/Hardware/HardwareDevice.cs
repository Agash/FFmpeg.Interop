using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
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
        ThrowIfUnloadable(type);
        if (!TryCreateCore(type, device, options, out HardwareDevice? created, out int error))
        {
            FFmpegError.Throw(error, "av_hwdevice_ctx_create");
        }

        return created;
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

    /// <summary>Opens a device if this machine has one of the type.</summary>
    /// <param name="type">The device type.</param>
    /// <param name="device">The opened device, or null.</param>
    /// <returns>Whether a device was opened.</returns>
    public static bool TryCreate(
        HardwareDeviceType type,
        [NotNullWhen(true)] out HardwareDevice? device
    ) => TryCreateCore(type, null, null, out device, out _);

    /// <summary>
    /// Opens a device of another type on the same hardware, sharing it: a VA-API device derived from a
    /// DRM device, or a Vulkan device derived from a D3D11VA or VA-API one, lets frames be mapped between
    /// the two without a copy.
    /// </summary>
    /// <param name="type">The device type to derive.</param>
    /// <returns>The derived device.</returns>
    public HardwareDevice Derive(HardwareDeviceType type)
    {
        ThrowIfUnloadable(type);
        AVBufferRef* derived = null;
        FFmpegError.ThrowIfError(av_hwdevice_ctx_create_derived(&derived, type, NativePointer, 0));
        return new(SafeBufferHandle.Own(derived, "av_hwdevice_ctx_create_derived"));
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

    // A device over native objects the application already has: the caller fills the type's hwctx,
    // then calls Initialize. Each hardware API's From* factory is built on the pair.
    internal static HardwareDevice Allocate(HardwareDeviceType type) =>
        new(SafeBufferHandle.Own(av_hwdevice_ctx_alloc(type), "av_hwdevice_ctx_alloc"));

    internal void Initialize() =>
        FFmpegError.ThrowIfError(av_hwdevice_ctx_init(NativePointer), "av_hwdevice_ctx_init");

    // FFmpeg builds for Linux commonly reach libva through a stub compiled into libavutil, which
    // dlopen()s libva on first use and aborts the process when it cannot: a machine without the VA-API
    // runtime would not get an error, it would lose the process. The same load is tried here first,
    // through the same loader, where failing is an answer rather than an abort. Whether the file exists
    // is not the same question: a library the loader refuses (missing dependencies) aborts all the same.
    private static readonly Lazy<bool> s_libVaLoads = new(() =>
        !OperatingSystem.IsLinux()
        || (
            NativeLibrary.TryLoad("libva.so.2", out _)
            && NativeLibrary.TryLoad("libva-drm.so.2", out _)
        )
    );

    private static bool UsesLibVa(HardwareDeviceType type) =>
        type == HardwareDeviceType.Vaapi
        || (OperatingSystem.IsLinux() && type == HardwareDeviceType.Qsv);

    private static void ThrowIfUnloadable(HardwareDeviceType type)
    {
        if (UsesLibVa(type) && !s_libVaLoads.Value)
        {
            throw new NotSupportedException(
                $"A {type} device needs the VA-API runtime (libva.so.2 and libva-drm.so.2), which cannot be loaded."
            );
        }
    }

    private static bool TryCreateCore(
        HardwareDeviceType type,
        string? device,
        IReadOnlyDictionary<string, string>? options,
        [NotNullWhen(true)] out HardwareDevice? created,
        out int error
    )
    {
        created = null;
        if (UsesLibVa(type) && !s_libVaLoads.Value)
        {
            error = LibAVUtil.AVERROR(Errno.ENOSYS);
            return false;
        }

        AVBufferRef* reference = null;
        AVDictionary* dictionary = NativeOptions.Create(options);
        try
        {
            using Utf8String name = new(device);
            error = av_hwdevice_ctx_create(&reference, type, name, dictionary, 0);
            if (error < 0)
            {
                return false;
            }

            created = new HardwareDevice(SafeBufferHandle.Own(reference, "av_hwdevice_ctx_create"));
            return true;
        }
        finally
        {
            av_dict_free(&dictionary);
        }
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
                !TryCreateCore(
                    HardwareDeviceType.Vulkan,
                    index.ToString(CultureInfo.InvariantCulture),
                    options,
                    out HardwareDevice? candidate,
                    out _
                )
            )
            {
                throw new NotSupportedException($"No Vulkan device is {adapter}.");
            }

            if (candidate.TryGetLuid(out long candidateLuid) && candidateLuid == luid)
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

    private static ImmutableArray<PixelFormat> Formats(AVPixelFormat* list)
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

/// <summary>What frames a hardware device can hold.</summary>
/// <param name="HardwareFormats">The hardware pixel formats.</param>
/// <param name="SoftwareFormats">The system-memory formats frames can be uploaded from or downloaded to.</param>
/// <param name="MinWidth">The minimum width.</param>
/// <param name="MinHeight">The minimum height.</param>
/// <param name="MaxWidth">The maximum width.</param>
/// <param name="MaxHeight">The maximum height.</param>
public sealed record HardwareFrameConstraints(
    ImmutableArray<PixelFormat> HardwareFormats,
    ImmutableArray<PixelFormat> SoftwareFormats,
    int MinWidth,
    int MinHeight,
    int MaxWidth,
    int MaxHeight
);
