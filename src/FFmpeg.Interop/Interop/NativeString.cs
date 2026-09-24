using System.Runtime.InteropServices;

namespace FFmpeg.Interop;

internal static unsafe class NativeString
{
    public static string? Read(sbyte* value) => Marshal.PtrToStringUTF8((nint)value);
}

/// <summary>A NUL-terminated UTF-8 copy of a string for a <c>const char*</c> parameter.</summary>
internal readonly unsafe ref struct Utf8String : IDisposable
{
    private readonly nint _pointer;

    public Utf8String(string? value) =>
        _pointer = value is null ? 0 : Marshal.StringToCoTaskMemUTF8(value);

    public sbyte* Pointer => (sbyte*)_pointer;

    public static implicit operator sbyte*(Utf8String value) => value.Pointer;

    public void Dispose() => Marshal.FreeCoTaskMem(_pointer);
}
