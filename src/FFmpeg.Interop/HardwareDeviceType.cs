using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.AVHWDeviceType;

namespace FFmpeg.Interop;

/// <summary>A kind of hardware acceleration device. Wraps <see cref="AVHWDeviceType"/>.</summary>
/// <param name="Value">The native type.</param>
public readonly record struct HardwareDeviceType(AVHWDeviceType Value)
{
    /// <summary>No device.</summary>
    public static HardwareDeviceType None => new(AV_HWDEVICE_TYPE_NONE);

    /// <summary>Direct3D 11 video (Windows).</summary>
    public static HardwareDeviceType D3D11VA => new(AV_HWDEVICE_TYPE_D3D11VA);

    /// <summary>Direct3D 12 video (Windows).</summary>
    public static HardwareDeviceType D3D12VA => new(AV_HWDEVICE_TYPE_D3D12VA);

    /// <summary>VA-API (Linux).</summary>
    public static HardwareDeviceType Vaapi => new(AV_HWDEVICE_TYPE_VAAPI);

    /// <summary>VideoToolbox (macOS).</summary>
    public static HardwareDeviceType VideoToolbox => new(AV_HWDEVICE_TYPE_VIDEOTOOLBOX);

    /// <summary>Vulkan.</summary>
    public static HardwareDeviceType Vulkan => new(AV_HWDEVICE_TYPE_VULKAN);

    /// <summary>CUDA (NVIDIA).</summary>
    public static HardwareDeviceType Cuda => new(AV_HWDEVICE_TYPE_CUDA);

    /// <summary>Intel Quick Sync Video.</summary>
    public static HardwareDeviceType Qsv => new(AV_HWDEVICE_TYPE_QSV);

    /// <summary>AMD AMF.</summary>
    public static HardwareDeviceType Amf => new(AV_HWDEVICE_TYPE_AMF);

    /// <summary>DRM (Linux kernel mode setting device).</summary>
    public static HardwareDeviceType Drm => new(AV_HWDEVICE_TYPE_DRM);

    /// <summary>OpenCL.</summary>
    public static HardwareDeviceType OpenCL => new(AV_HWDEVICE_TYPE_OPENCL);

    /// <summary>The device types this FFmpeg build was compiled with. Not every one has a device present.</summary>
    public static IEnumerable<HardwareDeviceType> Compiled
    {
        get
        {
            for (
                AVHWDeviceType type = LibAVUtil.av_hwdevice_iterate_types(AV_HWDEVICE_TYPE_NONE);
                type != AV_HWDEVICE_TYPE_NONE;
                type = LibAVUtil.av_hwdevice_iterate_types(type)
            )
            {
                yield return new(type);
            }
        }
    }

    /// <summary>FFmpeg's name for the type, for example <c>d3d11va</c>, or null for <see cref="None"/>.</summary>
    public unsafe string? Name => NativeString.Read(LibAVUtil.av_hwdevice_get_type_name(Value));

    /// <summary>Finds a type by FFmpeg's name for it.</summary>
    /// <param name="name">The name, for example <c>vaapi</c>.</param>
    /// <param name="type">The type, or <see cref="None"/>.</param>
    /// <returns>Whether the name is known.</returns>
    public static unsafe bool TryParse(string name, out HardwareDeviceType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        using Utf8String native = new(name);
        type = new(LibAVUtil.av_hwdevice_find_type_by_name(native));
        return type != None;
    }

    /// <inheritdoc/>
    public override string ToString() => Name ?? Value.ToString();

    /// <summary>Wraps a native type.</summary>
    /// <param name="value">The native type.</param>
    public static implicit operator HardwareDeviceType(AVHWDeviceType value) => new(value);

    /// <summary>Unwraps to the native type.</summary>
    /// <param name="value">The type.</param>
    public static implicit operator AVHWDeviceType(HardwareDeviceType value) => value.Value;
}
