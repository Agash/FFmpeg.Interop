using System.Globalization;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// A rational number, FFmpeg's representation of time bases and frame rates. Layout-compatible with
/// <see cref="AVRational"/>.
/// </summary>
/// <remarks>
/// Equality is by representation: <c>1/2</c> and <c>2/4</c> are different values. Use
/// <see cref="IsEquivalentTo"/> to compare numerically.
/// </remarks>
/// <param name="Numerator">The numerator.</param>
/// <param name="Denominator">The denominator.</param>
public readonly record struct Rational(int Numerator, int Denominator)
{
    /// <summary>FFmpeg's internal time base, microseconds (<c>AV_TIME_BASE_Q</c>).</summary>
    public static Rational Microseconds => new(1, LibAVUtil.AV_TIME_BASE);

    /// <summary>The value as a double.</summary>
    /// <returns>The quotient; infinite or NaN when the denominator is 0.</returns>
    public double ToDouble() => Numerator / (double)Denominator;

    /// <summary>The reciprocal.</summary>
    /// <returns><c>Denominator/Numerator</c>.</returns>
    public Rational Invert() => new(Denominator, Numerator);

    /// <summary>Whether two rationals have the same value, as <c>av_cmp_q</c> decides it.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>True when equal in value; false when different or when either is 0/0.</returns>
    public bool IsEquivalentTo(Rational other) => LibAVUtil.av_cmp_q(this, other) == 0;

    /// <summary>Converts a timestamp between time bases, rounding to nearest (<c>av_rescale_q</c>).</summary>
    /// <param name="value">The timestamp in <paramref name="source"/> units.</param>
    /// <param name="source">The time base it is in.</param>
    /// <param name="destination">The time base to convert to.</param>
    /// <returns>The timestamp in <paramref name="destination"/> units.</returns>
    public static long Rescale(long value, Rational source, Rational destination) =>
        LibAVUtil.av_rescale_q(value, source, destination);

    /// <summary>A timestamp in this time base as a <see cref="TimeSpan"/>.</summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The duration it represents.</returns>
    public TimeSpan ToTimeSpan(long value) =>
        TimeSpan.FromTicks(Rescale(value, this, new(1, (int)TimeSpan.TicksPerSecond)));

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");

    /// <summary>Converts from the native representation.</summary>
    /// <param name="value">The native rational.</param>
    public static implicit operator Rational(AVRational value) => new(value.num, value.den);

    /// <summary>Converts to the native representation.</summary>
    /// <param name="value">The rational.</param>
    public static implicit operator AVRational(Rational value) =>
        new() { num = value.Numerator, den = value.Denominator };
}
