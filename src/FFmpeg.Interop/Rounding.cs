using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>How a rescaled value is rounded (<see cref="AVRounding"/>).</summary>
public enum Rounding
{
    /// <summary>Toward zero (<c>AV_ROUND_ZERO</c>).</summary>
    Zero = AVRounding.AV_ROUND_ZERO,

    /// <summary>Away from zero (<c>AV_ROUND_INF</c>).</summary>
    Infinity = AVRounding.AV_ROUND_INF,

    /// <summary>Toward negative infinity (<c>AV_ROUND_DOWN</c>).</summary>
    Down = AVRounding.AV_ROUND_DOWN,

    /// <summary>Toward positive infinity (<c>AV_ROUND_UP</c>).</summary>
    Up = AVRounding.AV_ROUND_UP,

    /// <summary>To nearest, halfway cases away from zero (<c>AV_ROUND_NEAR_INF</c>), as <see cref="Rational.Rescale(long, Rational, Rational)"/> does.</summary>
    NearInfinity = AVRounding.AV_ROUND_NEAR_INF,
}
