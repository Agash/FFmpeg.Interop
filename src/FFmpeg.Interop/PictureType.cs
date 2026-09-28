using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>The type of a coded picture. Same values as <see cref="AVPictureType"/>.</summary>
public enum PictureType
{
    /// <summary>Unspecified: the encoder decides.</summary>
    None = 0,

    /// <summary>Intra: decodable on its own. Forces a key frame when set on an encoder's input.</summary>
    I = 1,

    /// <summary>Predicted from earlier pictures.</summary>
    P = 2,

    /// <summary>Predicted from earlier and later pictures.</summary>
    B = 3,

    /// <summary>S(GMC)-VOP, MPEG-4.</summary>
    S = 4,

    /// <summary>Switching intra.</summary>
    SI = 5,

    /// <summary>Switching predicted.</summary>
    SP = 6,

    /// <summary>BI type.</summary>
    BI = 7,
}
