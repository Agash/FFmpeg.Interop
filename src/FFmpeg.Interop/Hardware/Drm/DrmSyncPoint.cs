namespace FFmpeg.Interop;

/// <summary>
/// A point on a DRM syncobj timeline: explicit synchronisation for a DMA-BUF picture, which its producer
/// signals once the picture is written. Mesa's Vulkan drivers export their timeline semaphores as such
/// syncobjs, and PipeWire carries them as its explicit sync.
/// </summary>
/// <param name="Syncobj">The syncobj's file descriptor; it stays the caller's.</param>
/// <param name="Value">The timeline value the picture is ready at.</param>
public readonly record struct DrmSyncPoint(int Syncobj, ulong Value);
