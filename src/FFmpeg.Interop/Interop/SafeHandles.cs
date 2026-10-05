using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;
using Microsoft.Win32.SafeHandles;

namespace FFmpeg.Interop;

// Ownership of each native object FFmpeg allocates. A SafeHandle rather than a finalizer on the public
// type: release runs exactly once even when disposal races finalization, and a critical finalizer
// still frees the object when the owner is never disposed.

internal sealed unsafe class SafeFrameHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public AVFrame* Pointer => (AVFrame*)handle;

    public static SafeFrameHandle Allocate() => Wrap(LibAVUtil.av_frame_alloc(), "av_frame_alloc");

    public static SafeFrameHandle Wrap(AVFrame* frame, string function)
    {
        if (frame is null)
        {
            FFmpegError.ThrowOutOfMemory(function);
        }

        SafeFrameHandle owner = new();
        owner.SetHandle((nint)frame);
        return owner;
    }

    protected override bool ReleaseHandle()
    {
        AVFrame* frame = (AVFrame*)handle;
        LibAVUtil.av_frame_free(&frame);
        return true;
    }
}

internal sealed unsafe class SafePacketHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public AVPacket* Pointer => (AVPacket*)handle;

    public static SafePacketHandle Allocate()
    {
        AVPacket* packet = LibAVCodec.av_packet_alloc();
        if (packet is null)
        {
            FFmpegError.ThrowOutOfMemory("av_packet_alloc");
        }

        SafePacketHandle owner = new();
        owner.SetHandle((nint)packet);
        return owner;
    }

    protected override bool ReleaseHandle()
    {
        AVPacket* packet = (AVPacket*)handle;
        LibAVCodec.av_packet_free(&packet);
        return true;
    }
}

internal sealed unsafe class SafeCodecContextHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    // Native memory the context's opaque pointer names, freed with the context: the callbacks that read
    // it can run for as long as the context lives, and no longer.
    private void* _opaque;

    public AVCodecContext* Pointer => (AVCodecContext*)handle;

    // Hands the context native memory from NativeMemory to point its opaque field at, for the
    // context's callbacks.
    public void SetOpaque(void* state)
    {
        NativeMemory.Free(_opaque);
        _opaque = state;
        Pointer->opaque = state;
    }

    public static SafeCodecContextHandle Allocate(AVCodec* codec)
    {
        AVCodecContext* context = LibAVCodec.avcodec_alloc_context3(codec);
        if (context is null)
        {
            FFmpegError.ThrowOutOfMemory("avcodec_alloc_context3");
        }

        SafeCodecContextHandle owner = new();
        owner.SetHandle((nint)context);
        return owner;
    }

    protected override bool ReleaseHandle()
    {
        AVCodecContext* context = (AVCodecContext*)handle;
        LibAVCodec.avcodec_free_context(&context);
        NativeMemory.Free(_opaque);
        _opaque = null;
        return true;
    }
}

/// <summary>Owns one reference to an <see cref="AVBufferRef"/>.</summary>
internal sealed unsafe class SafeBufferHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public AVBufferRef* Pointer => (AVBufferRef*)handle;

    // Takes ownership of an existing reference.
    public static SafeBufferHandle Own(AVBufferRef* buffer, string function)
    {
        if (buffer is null)
        {
            FFmpegError.ThrowOutOfMemory(function);
        }

        SafeBufferHandle owner = new();
        owner.SetHandle((nint)buffer);
        return owner;
    }

    // A new reference to the same buffer, for handing to FFmpeg, which takes ownership of it.
    public AVBufferRef* NewReference()
    {
        AVBufferRef* reference = LibAVUtil.av_buffer_ref(Pointer);
        if (reference is null)
        {
            FFmpegError.ThrowOutOfMemory("av_buffer_ref");
        }

        return reference;
    }

    protected override bool ReleaseHandle()
    {
        AVBufferRef* buffer = (AVBufferRef*)handle;
        LibAVUtil.av_buffer_unref(&buffer);
        return true;
    }
}

internal sealed unsafe class SafeScalerHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public SwsContext* Pointer => (SwsContext*)handle;

    public static SafeScalerHandle Allocate()
    {
        SwsContext* context = LibSwScale.sws_alloc_context();
        if (context is null)
        {
            FFmpegError.ThrowOutOfMemory("sws_alloc_context");
        }

        SafeScalerHandle owner = new();
        owner.SetHandle((nint)context);
        return owner;
    }

    protected override bool ReleaseHandle()
    {
        SwsContext* context = (SwsContext*)handle;
        LibSwScale.sws_free_context(&context);
        return true;
    }
}

internal sealed unsafe class SafeBufferPoolHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public AVBufferPool* Pointer => (AVBufferPool*)handle;

    public static SafeBufferPoolHandle Create(int size)
    {
        AVBufferPool* pool = LibAVUtil.av_buffer_pool_init((nuint)size, null);
        if (pool is null)
        {
            FFmpegError.ThrowOutOfMemory("av_buffer_pool_init");
        }

        SafeBufferPoolHandle owner = new();
        owner.SetHandle((nint)pool);
        return owner;
    }

    // Uninit only marks the pool: it is freed when the last buffer taken from it is returned, so frames
    // still holding pooled buffers stay valid.
    protected override bool ReleaseHandle()
    {
        AVBufferPool* pool = (AVBufferPool*)handle;
        LibAVUtil.av_buffer_pool_uninit(&pool);
        return true;
    }
}

internal sealed unsafe class SafeResamplerHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public SwrContext* Pointer => (SwrContext*)handle;

    public static SafeResamplerHandle Own(SwrContext* context)
    {
        SafeResamplerHandle owner = new();
        owner.SetHandle((nint)context);
        return owner;
    }

    protected override bool ReleaseHandle()
    {
        SwrContext* context = (SwrContext*)handle;
        LibSwResample.swr_free(&context);
        return true;
    }
}

internal sealed unsafe class SafeInputFormatHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public AVFormatContext* Pointer => (AVFormatContext*)handle;

    public static SafeInputFormatHandle Own(AVFormatContext* context)
    {
        SafeInputFormatHandle owner = new();
        owner.SetHandle((nint)context);
        return owner;
    }

    protected override bool ReleaseHandle()
    {
        AVFormatContext* context = (AVFormatContext*)handle;
        LibAVFormat.avformat_close_input(&context);
        return true;
    }
}

internal sealed unsafe class SafeOutputFormatHandle()
    : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    public AVFormatContext* Pointer => (AVFormatContext*)handle;

    public static SafeOutputFormatHandle Own(AVFormatContext* context)
    {
        SafeOutputFormatHandle owner = new();
        owner.SetHandle((nint)context);
        return owner;
    }

    // The muxer opened the I/O context itself unless the format has no file; closing it is ours.
    protected override bool ReleaseHandle()
    {
        AVFormatContext* context = (AVFormatContext*)handle;
        if ((context->oformat->flags & LibAVFormat.AVFMT_NOFILE) == 0)
        {
            _ = LibAVFormat.avio_closep(&context->pb);
        }

        LibAVFormat.avformat_free_context(context);
        return true;
    }
}
