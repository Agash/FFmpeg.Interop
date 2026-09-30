using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>The range of sample values. Same values as <see cref="AVColorRange"/>.</summary>
public enum ColorRange
{
    /// <summary>Unspecified.</summary>
    Unspecified = AVColorRange.AVCOL_RANGE_UNSPECIFIED,

    /// <summary>Limited (video, MPEG) range: 16-235 luma and 16-240 chroma at 8 bits.</summary>
    Limited = AVColorRange.AVCOL_RANGE_MPEG,

    /// <summary>Full (JPEG) range: 0-255 at 8 bits.</summary>
    Full = AVColorRange.AVCOL_RANGE_JPEG,
}

/// <summary>The colour primaries, as ITU-T H.273 numbers them. Same values as <see cref="AVColorPrimaries"/>.</summary>
public enum ColorPrimaries
{
    /// <summary>BT.709, also sRGB.</summary>
    Bt709 = AVColorPrimaries.AVCOL_PRI_BT709,

    /// <summary>Unspecified.</summary>
    Unspecified = AVColorPrimaries.AVCOL_PRI_UNSPECIFIED,

    /// <summary>BT.470 System M.</summary>
    Bt470M = AVColorPrimaries.AVCOL_PRI_BT470M,

    /// <summary>BT.470 System B/G, also BT.601 625-line.</summary>
    Bt470Bg = AVColorPrimaries.AVCOL_PRI_BT470BG,

    /// <summary>SMPTE 170M, also BT.601 525-line.</summary>
    Smpte170M = AVColorPrimaries.AVCOL_PRI_SMPTE170M,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = AVColorPrimaries.AVCOL_PRI_SMPTE240M,

    /// <summary>Generic film.</summary>
    Film = AVColorPrimaries.AVCOL_PRI_FILM,

    /// <summary>BT.2020 and BT.2100.</summary>
    Bt2020 = AVColorPrimaries.AVCOL_PRI_BT2020,

    /// <summary>SMPTE ST 428-1 (CIE 1931 XYZ).</summary>
    Smpte428 = AVColorPrimaries.AVCOL_PRI_SMPTE428,

    /// <summary>SMPTE RP 431-2 (DCI-P3).</summary>
    Smpte431 = AVColorPrimaries.AVCOL_PRI_SMPTE431,

    /// <summary>SMPTE EG 432-1 (Display P3).</summary>
    Smpte432 = AVColorPrimaries.AVCOL_PRI_SMPTE432,

    /// <summary>EBU Tech. 3213-E.</summary>
    Ebu3213 = AVColorPrimaries.AVCOL_PRI_EBU3213,
}

/// <summary>
/// The transfer characteristics, as ITU-T H.273 numbers them. Same values as
/// <see cref="AVColorTransferCharacteristic"/>.
/// </summary>
public enum ColorTransfer
{
    /// <summary>BT.709.</summary>
    Bt709 = AVColorTransferCharacteristic.AVCOL_TRC_BT709,

    /// <summary>Unspecified.</summary>
    Unspecified = AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED,

    /// <summary>Gamma 2.2 (BT.470 System M).</summary>
    Gamma22 = AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22,

    /// <summary>Gamma 2.8 (BT.470 System B/G).</summary>
    Gamma28 = AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28,

    /// <summary>SMPTE 170M, also BT.601.</summary>
    Smpte170M = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE240M,

    /// <summary>Linear.</summary>
    Linear = AVColorTransferCharacteristic.AVCOL_TRC_LINEAR,

    /// <summary>Logarithmic, 100:1 range.</summary>
    Log = AVColorTransferCharacteristic.AVCOL_TRC_LOG,

    /// <summary>Logarithmic, 100 * sqrt(10):1 range.</summary>
    LogSqrt = AVColorTransferCharacteristic.AVCOL_TRC_LOG_SQRT,

    /// <summary>IEC 61966-2-4 (xvYCC).</summary>
    Xvycc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_4,

    /// <summary>BT.1361 extended colour gamut.</summary>
    Bt1361 = AVColorTransferCharacteristic.AVCOL_TRC_BT1361_ECG,

    /// <summary>IEC 61966-2-1 (sRGB).</summary>
    Srgb = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,

    /// <summary>BT.2020 10-bit.</summary>
    Bt2020TenBit = AVColorTransferCharacteristic.AVCOL_TRC_BT2020_10,

    /// <summary>BT.2020 12-bit.</summary>
    Bt2020TwelveBit = AVColorTransferCharacteristic.AVCOL_TRC_BT2020_12,

    /// <summary>SMPTE ST 2084, the perceptual quantizer of HDR10 and BT.2100 PQ.</summary>
    Pq = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,

    /// <summary>SMPTE ST 428-1.</summary>
    Smpte428 = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE428,

    /// <summary>ARIB STD-B67, hybrid log-gamma (BT.2100 HLG).</summary>
    Hlg = AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67,
}

/// <summary>
/// The matrix from RGB to luma and chroma, as ITU-T H.273 numbers them; FFmpeg calls it the colour
/// space. Same values as <see cref="AVColorSpace"/>.
/// </summary>
public enum ColorSpace
{
    /// <summary>Identity: the samples are RGB (GBR in planar formats).</summary>
    Rgb = AVColorSpace.AVCOL_SPC_RGB,

    /// <summary>BT.709.</summary>
    Bt709 = AVColorSpace.AVCOL_SPC_BT709,

    /// <summary>Unspecified.</summary>
    Unspecified = AVColorSpace.AVCOL_SPC_UNSPECIFIED,

    /// <summary>FCC Title 47.</summary>
    Fcc = AVColorSpace.AVCOL_SPC_FCC,

    /// <summary>BT.470 System B/G, also BT.601 625-line.</summary>
    Bt470Bg = AVColorSpace.AVCOL_SPC_BT470BG,

    /// <summary>SMPTE 170M, also BT.601 525-line.</summary>
    Smpte170M = AVColorSpace.AVCOL_SPC_SMPTE170M,

    /// <summary>SMPTE 240M.</summary>
    Smpte240M = AVColorSpace.AVCOL_SPC_SMPTE240M,

    /// <summary>YCgCo.</summary>
    YCgCo = AVColorSpace.AVCOL_SPC_YCGCO,

    /// <summary>BT.2020 non-constant luminance.</summary>
    Bt2020Ncl = AVColorSpace.AVCOL_SPC_BT2020_NCL,

    /// <summary>BT.2020 constant luminance.</summary>
    Bt2020Cl = AVColorSpace.AVCOL_SPC_BT2020_CL,

    /// <summary>SMPTE ST 2085.</summary>
    Smpte2085 = AVColorSpace.AVCOL_SPC_SMPTE2085,

    /// <summary>Chromaticity-derived non-constant luminance.</summary>
    ChromaDerivedNcl = AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_NCL,

    /// <summary>Chromaticity-derived constant luminance.</summary>
    ChromaDerivedCl = AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_CL,

    /// <summary>ICtCp (BT.2100).</summary>
    ICtCp = AVColorSpace.AVCOL_SPC_ICTCP,
}
