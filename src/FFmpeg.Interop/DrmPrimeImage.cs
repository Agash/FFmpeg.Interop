using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// A picture in DMA-BUFs: the buffers, and the layers (DRM formats) with the planes each takes from
/// them. The shape of <see cref="AVDRMFrameDescriptor"/>, checked against its limits.
/// </summary>
/// <param name="Objects">The DMA-BUF objects, at most four.</param>
/// <param name="Layers">The layers, at most four, each with at most four planes.</param>
public sealed record DrmPrimeImage(IReadOnlyList<DrmObject> Objects, IReadOnlyList<DrmLayer> Layers)
{
    // AV_DRM_MAX_PLANES: the fixed array sizes in AVDRMFrameDescriptor and AVDRMLayerDescriptor.
    private const int MaxEntries = 4;

    internal AVDRMFrameDescriptor ToNative()
    {
        ArgumentNullException.ThrowIfNull(Objects);
        ArgumentNullException.ThrowIfNull(Layers);
        if (Objects.Count is 0 or > MaxEntries || Layers.Count is 0 or > MaxEntries)
        {
            throw new ArgumentException(
                $"A DRM PRIME image has 1 to {MaxEntries} objects and 1 to {MaxEntries} layers."
            );
        }

        AVDRMFrameDescriptor native = default;
        native.nb_objects = Objects.Count;
        for (int i = 0; i < Objects.Count; i++)
        {
            DrmObject dmaBuf = Objects[i];
            native.objects[i] = new AVDRMObjectDescriptor
            {
                fd = dmaBuf.FileDescriptor,
                size = (nuint)dmaBuf.Size,
                format_modifier = dmaBuf.Modifier,
            };
        }

        native.nb_layers = Layers.Count;
        for (int l = 0; l < Layers.Count; l++)
        {
            DrmLayer layer = Layers[l];
            if (layer.Planes.Count is 0 or > MaxEntries)
            {
                throw new ArgumentException(
                    $"Layer {l} has {layer.Planes.Count} planes; a layer has 1 to {MaxEntries}."
                );
            }

            native.layers[l].format = layer.Format;
            native.layers[l].nb_planes = layer.Planes.Count;
            for (int p = 0; p < layer.Planes.Count; p++)
            {
                DrmPlane plane = layer.Planes[p];
                if ((uint)plane.ObjectIndex >= (uint)Objects.Count)
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
}

/// <summary>One layer of a DRM PRIME image: a DRM format and the planes that make it up.</summary>
/// <param name="Format">The <c>DRM_FORMAT_*</c> fourcc, for example NV12, or R8 and GR88 for NV12 split into two layers.</param>
/// <param name="Planes">The planes, in the format's plane order.</param>
public sealed record DrmLayer(uint Format, IReadOnlyList<DrmPlane> Planes);
