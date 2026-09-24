using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FFmpeg.Interop;

/// <summary>
/// One plane of a video frame in system memory, viewed in place. Rows are <see cref="Stride"/> bytes
/// apart, which may be more than <see cref="RowLength"/> (alignment padding) and may be negative
/// (bottom-up images), so the plane is read row by row rather than as one span.
/// </summary>
public readonly ref struct ReadOnlyImagePlane
{
    private readonly ref byte _origin;

    internal unsafe ReadOnlyImagePlane(byte* origin, int stride, int rowLength, int height)
        : this(ref Unsafe.AsRef<byte>(origin), stride, rowLength, height) { }

    internal ReadOnlyImagePlane(ref byte origin, int stride, int rowLength, int height)
    {
        _origin = ref origin;
        Stride = stride;
        RowLength = rowLength;
        Height = height;
    }

    /// <summary>The distance in bytes from the start of one row to the start of the next.</summary>
    public int Stride { get; }

    /// <summary>The number of bytes of image data in each row.</summary>
    public int RowLength { get; }

    /// <summary>The number of rows.</summary>
    public int Height { get; }

    /// <summary>One row of image data.</summary>
    /// <param name="row">The row, from 0 to <see cref="Height"/> - 1.</param>
    /// <returns>The row's <see cref="RowLength"/> bytes.</returns>
    public ReadOnlySpan<byte> GetRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)row, (uint)Height, nameof(row));
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref _origin, (nint)row * Stride),
            RowLength
        );
    }

    /// <summary>Copies the plane into a tightly packed buffer of <c>RowLength * Height</c> bytes.</summary>
    /// <param name="destination">The buffer.</param>
    public void CopyTo(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            destination.Length,
            RowLength * Height,
            nameof(destination)
        );
        for (int row = 0; row < Height; row++)
        {
            GetRow(row).CopyTo(destination.Slice(row * RowLength, RowLength));
        }
    }
}

/// <summary>
/// One plane of a writable video frame in system memory, viewed in place. See
/// <see cref="ReadOnlyImagePlane"/> for the row layout.
/// </summary>
public readonly ref struct ImagePlane
{
    private readonly ref byte _origin;

    internal unsafe ImagePlane(byte* origin, int stride, int rowLength, int height)
    {
        _origin = ref Unsafe.AsRef<byte>(origin);
        Stride = stride;
        RowLength = rowLength;
        Height = height;
    }

    /// <inheritdoc cref="ReadOnlyImagePlane.Stride"/>
    public int Stride { get; }

    /// <inheritdoc cref="ReadOnlyImagePlane.RowLength"/>
    public int RowLength { get; }

    /// <inheritdoc cref="ReadOnlyImagePlane.Height"/>
    public int Height { get; }

    /// <summary>One row of image data.</summary>
    /// <param name="row">The row, from 0 to <see cref="Height"/> - 1.</param>
    /// <returns>The row's <see cref="RowLength"/> bytes.</returns>
    public Span<byte> GetRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)row, (uint)Height, nameof(row));
        return MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _origin, (nint)row * Stride), RowLength);
    }

    /// <summary>Fills the plane from a tightly packed buffer of <c>RowLength * Height</c> bytes.</summary>
    /// <param name="source">The buffer.</param>
    public void CopyFrom(ReadOnlySpan<byte> source)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            source.Length,
            RowLength * Height,
            nameof(source)
        );
        for (int row = 0; row < Height; row++)
        {
            source.Slice(row * RowLength, RowLength).CopyTo(GetRow(row));
        }
    }

    /// <summary>A read-only view of the same plane.</summary>
    /// <param name="plane">The plane.</param>
    public static implicit operator ReadOnlyImagePlane(ImagePlane plane) =>
        new(ref plane._origin, plane.Stride, plane.RowLength, plane.Height);
}
