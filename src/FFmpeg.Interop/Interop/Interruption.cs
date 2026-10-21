using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// A format context's interrupt callback (<c>AVIOInterruptCB</c>) bound to the cancellation token of the
/// operation in progress: FFmpeg polls it while it blocks in I/O, and a cancelled token ends the call with
/// <c>AVERROR_EXIT</c>, turned here into an <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed unsafe class Interruption : IDisposable
{
    private GCHandle _self;
    private CancellationToken _token;

    public Interruption() => _self = GCHandle.Alloc(this);

    /// <summary>The callback to install on a context, before it is opened.</summary>
    public AVIOInterruptCB Callback =>
        new() { callback = &Check, opaque = (void*)GCHandle.ToIntPtr(_self) };

    /// <summary>Binds the token for one call; the binding ends with the scope.</summary>
    public Scope During(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _token = cancellationToken;
        return new Scope(this);
    }

    /// <summary>
    /// Throws the cancellation for a call that ended with <c>AVERROR_EXIT</c> because its token was
    /// cancelled; returns the result otherwise.
    /// </summary>
    public int Checked(int result)
    {
        if (result == LibAVUtil.AVERROR_EXIT)
        {
            _token.ThrowIfCancellationRequested();
        }

        return result;
    }

    public void Dispose()
    {
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Check(void* opaque) =>
        GCHandle.FromIntPtr((nint)opaque).Target
            is Interruption { _token.IsCancellationRequested: true }
            ? 1
            : 0;

    /// <summary>The token's binding for one call.</summary>
    public readonly ref struct Scope(Interruption owner)
    {
        public void Dispose() => owner._token = CancellationToken.None;
    }
}
