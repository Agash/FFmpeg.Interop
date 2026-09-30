using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// A picture in DMA-BUFs: the buffers, and the layers (DRM formats) with the planes each takes from
/// them. The shape of <see cref="AVDRMFrameDescriptor"/>, checked against its limits.
/// </summary>
/// <param name="Objects">The DMA-BUF objects, at most four.</param>
/// <param name="Layers">The layers, at most four, each with at most four planes.</param>
public sealed partial record DrmPrimeImage(
    ImmutableArray<DrmObject> Objects,
    ImmutableArray<DrmLayer> Layers
)
{
    // AV_DRM_MAX_PLANES: the fixed array sizes in AVDRMFrameDescriptor and AVDRMLayerDescriptor.
    private const int MaxEntries = 4;

    internal AVDRMFrameDescriptor ToNative()
    {
        if (Objects.IsDefault || Layers.IsDefault)
        {
            throw new ArgumentException("A DRM PRIME image needs its objects and layers.");
        }

        if (Objects.Length is 0 or > MaxEntries || Layers.Length is 0 or > MaxEntries)
        {
            throw new ArgumentException(
                $"A DRM PRIME image has 1 to {MaxEntries} objects and 1 to {MaxEntries} layers."
            );
        }

        AVDRMFrameDescriptor native = default;
        native.nb_objects = Objects.Length;
        for (int i = 0; i < Objects.Length; i++)
        {
            DrmObject dmaBuf = Objects[i];
            native.objects[i] = new AVDRMObjectDescriptor
            {
                fd = dmaBuf.FileDescriptor,
                size = (nuint)(dmaBuf.Size > 0 ? dmaBuf.Size : SizeOf(dmaBuf.FileDescriptor)),
                format_modifier = dmaBuf.Modifier,
            };
        }

        native.nb_layers = Layers.Length;
        for (int l = 0; l < Layers.Length; l++)
        {
            DrmLayer layer = Layers[l];
            if (layer.Planes.IsDefaultOrEmpty || layer.Planes.Length > MaxEntries)
            {
                throw new ArgumentException(
                    $"Layer {l} has {layer.Planes.Length} planes; a layer has 1 to {MaxEntries}."
                );
            }

            native.layers[l].format = layer.Format;
            native.layers[l].nb_planes = layer.Planes.Length;
            for (int p = 0; p < layer.Planes.Length; p++)
            {
                DrmPlane plane = layer.Planes[p];
                if ((uint)plane.ObjectIndex >= (uint)Objects.Length)
                {
                    throw new ArgumentException(
                        $"Layer {l} plane {p} refers to object {plane.ObjectIndex}, which does not exist."
                    );
                }

                native.layers[l].planes[p] = new AVDRMPlaneDescriptor
                {
                    object_index = plane.ObjectIndex,
                    offset = (nint)plane.Offset,
                    pitch = (nint)plane.Pitch,
                };
            }
        }

        return native;
    }

    // A DMA-BUF's size, which importers need: seeking to its end reports it (the kernel's documented
    // way to size a dma-buf), and leaves the descriptor usable.
    private static long SizeOf(int fileDescriptor)
    {
        long size = OperatingSystem.IsLinux() ? lseek(fileDescriptor, 0, SeekEnd) : -1;
        if (size <= 0)
        {
            throw new ArgumentException(
                $"The size of DMA-BUF {fileDescriptor} is not given and cannot be read from it."
            );
        }

        _ = lseek(fileDescriptor, 0, SeekSet);
        return size;
    }

    private const int SeekSet = 0;
    private const int SeekEnd = 2;

    [LibraryImport("libc", SetLastError = true)]
    private static partial long lseek(int fileDescriptor, long offset, int whence);
}

/// <summary>One layer of a DRM PRIME image: a DRM format and the planes that make it up.</summary>
/// <param name="Format">The <c>DRM_FORMAT_*</c> fourcc, for example NV12, or R8 and GR88 for NV12 split into two layers.</param>
/// <param name="Planes">The planes, in the format's plane order.</param>
public sealed record DrmLayer(uint Format, ImmutableArray<DrmPlane> Planes);

/// <summary>A DMA-BUF object: a file descriptor and its size and layout modifier.</summary>
/// <param name="FileDescriptor">The DMA-BUF file descriptor.</param>
/// <param name="Size">The size of the object in bytes; zero to read it from the descriptor when importing.</param>
/// <param name="Modifier">The DRM format modifier (tiling/compression layout).</param>
public readonly record struct DrmObject(int FileDescriptor, long Size, ulong Modifier);

/// <summary>A plane within a DMA-BUF object.</summary>
/// <param name="ObjectIndex">The index of the object holding the plane.</param>
/// <param name="Offset">The offset of the plane in the object, in bytes.</param>
/// <param name="Pitch">The row pitch in bytes.</param>
public readonly record struct DrmPlane(int ObjectIndex, long Offset, long Pitch);

/// <summary>A view of the DMA-BUF descriptors of a DRM PRIME frame (<see cref="AVDRMFrameDescriptor"/>).</summary>
public readonly unsafe ref struct DrmFrameDescriptor
{
    private readonly AVDRMFrameDescriptor* _descriptor;

    internal DrmFrameDescriptor(AVDRMFrameDescriptor* descriptor) => _descriptor = descriptor;

    /// <summary>The number of DMA-BUF objects.</summary>
    public int ObjectCount => _descriptor->nb_objects;

    /// <summary>The number of layers; each layer is one DRM format (for example NV12, or R8 plus GR88).</summary>
    public int LayerCount => _descriptor->nb_layers;

    /// <summary>The native descriptor.</summary>
    public AVDRMFrameDescriptor* NativePointer => _descriptor;

    /// <summary>One DMA-BUF object.</summary>
    /// <param name="index">The object index.</param>
    /// <returns>The object.</returns>
    public DrmObject GetObject(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)index,
            (uint)ObjectCount,
            nameof(index)
        );
        AVDRMObjectDescriptor native = _descriptor->objects[index];
        return new(native.fd, (long)native.size, native.format_modifier);
    }

    /// <summary>The DRM fourcc of one layer.</summary>
    /// <param name="layer">The layer index.</param>
    /// <returns>The <c>DRM_FORMAT_*</c> code.</returns>
    public uint GetLayerFormat(int layer) => Layer(layer)->format;

    /// <summary>The number of planes in one layer.</summary>
    /// <param name="layer">The layer index.</param>
    /// <returns>The plane count.</returns>
    public int GetPlaneCount(int layer) => Layer(layer)->nb_planes;

    /// <summary>One plane of one layer.</summary>
    /// <param name="layer">The layer index.</param>
    /// <param name="plane">The plane index within the layer.</param>
    /// <returns>The plane.</returns>
    public DrmPlane GetPlane(int layer, int plane)
    {
        AVDRMLayerDescriptor* native = Layer(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)plane,
            (uint)native->nb_planes,
            nameof(plane)
        );
        AVDRMPlaneDescriptor descriptor = native->planes[plane];
        return new(descriptor.object_index, descriptor.offset, descriptor.pitch);
    }

    private AVDRMLayerDescriptor* Layer(int layer)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (uint)layer,
            (uint)LayerCount,
            nameof(layer)
        );
        return (AVDRMLayerDescriptor*)Unsafe.AsPointer(ref _descriptor->layers[layer]);
    }
}
