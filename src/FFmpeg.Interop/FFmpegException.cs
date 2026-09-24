using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>An FFmpeg call returned an error code.</summary>
public sealed class FFmpegException : Exception
{
    /// <summary>Creates an exception with no FFmpeg error code.</summary>
    public FFmpegException() { }

    /// <summary>Creates an exception with a message and no FFmpeg error code.</summary>
    /// <param name="message">The message.</param>
    public FFmpegException(string message)
        : base(message) { }

    /// <summary>Creates an exception with a message, an inner exception and no FFmpeg error code.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public FFmpegException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an exception for a failed FFmpeg call.</summary>
    /// <param name="errorCode">The negative error code FFmpeg returned.</param>
    /// <param name="operation">The FFmpeg function that returned it.</param>
    public FFmpegException(int errorCode, string operation)
        : base($"{operation} failed: {LibAVUtil.av_err2str(errorCode)} ({errorCode}).")
    {
        ErrorCode = errorCode;
        Operation = operation;
    }

    /// <summary>The negative FFmpeg error code, or 0 when the exception did not come from a call.</summary>
    public int ErrorCode { get; }

    /// <summary>The FFmpeg function that failed, when known.</summary>
    public string? Operation { get; }
}

internal static class FFmpegError
{
    // The caller expression is the native call, for example "avcodec_open2(context, codec, &options)";
    // the function name is enough to identify it and keeps the message readable.
    public static int ThrowIfError(
        int result,
        [CallerArgumentExpression(nameof(result))] string? call = null
    )
    {
        if (result < 0)
        {
            Throw(result, call);
        }

        return result;
    }

    [DoesNotReturn]
    [StackTraceHidden]
    public static void Throw(int result, string? call) => throw Create(result, call);

    public static FFmpegException Create(int result, string? call)
    {
        int paren = call?.IndexOf('(', StringComparison.Ordinal) ?? -1;
        string operation =
            call is null ? "FFmpeg call"
            : paren > 0 ? call[..paren]
            : call;
        return new FFmpegException(result, operation);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    public static void ThrowOutOfMemory(string function) =>
        throw new OutOfMemoryException($"{function} returned null.");
}
