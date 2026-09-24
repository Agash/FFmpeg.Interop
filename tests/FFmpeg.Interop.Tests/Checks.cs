using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop.Tests;

/// <summary>Assertions for FFmpeg return codes and pointers, naming the call that failed.</summary>
internal static unsafe class Checks
{
    public static int Check(
        int result,
        [CallerArgumentExpression(nameof(result))] string? call = null
    )
    {
        if (result < 0)
        {
            Assert.Fail($"{call} failed: {av_err2str(result)} ({result})");
        }

        return result;
    }

    public static T* NotNull<T>(
        T* pointer,
        [CallerArgumentExpression(nameof(pointer))] string? call = null
    )
        where T : unmanaged
    {
        if (pointer is null)
        {
            Assert.Fail($"{call} returned null.");
        }

        return pointer;
    }

    public static string? String(sbyte* value) => Marshal.PtrToStringUTF8((nint)value);
}

/// <summary>A NUL-terminated UTF-8 copy of a string in native memory, for <c>const char*</c> parameters.</summary>
internal readonly unsafe struct Utf8 : IDisposable
{
    private readonly nint _pointer;

    public Utf8(string value) => _pointer = Marshal.StringToCoTaskMemUTF8(value);

    public sbyte* Pointer => (sbyte*)_pointer;

    public static implicit operator sbyte*(Utf8 value) => value.Pointer;

    public void Dispose() => Marshal.FreeCoTaskMem(_pointer);
}

/// <summary>A scratch directory for one test's media files, removed when the test ends.</summary>
internal sealed class Scratch : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ffmpeg-interop-");

    public string this[string name] => Path.Combine(_directory.FullName, name);

    public void Dispose() => _directory.Delete(recursive: true);
}
