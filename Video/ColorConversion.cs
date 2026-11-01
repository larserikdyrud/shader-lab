using System.Numerics;
using FFmpeg.AutoGen;

namespace ShaderLab.Video;

/// <summary>YUV → R'G'B' rules: which matrix and range a stream has, and the constants the shader needs.</summary>
public static class ColorConversion
{
    /// <summary>Picks the matrix for a stream. "Unspecified" is resolved the way players do:
    /// HD and larger is BT.709, smaller is BT.601.</summary>
    public static AVColorSpace Resolve(AVColorSpace tagged, int width) => tagged switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => AVColorSpace.AVCOL_SPC_BT709,
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M or AVColorSpace.AVCOL_SPC_FCC
            => AVColorSpace.AVCOL_SPC_BT470BG,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL
            => AVColorSpace.AVCOL_SPC_BT2020_NCL,
        _ => width >= 1280 ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_BT470BG
    };

    /// <summary>Luma weights (Kr, Kb) for a resolved matrix.</summary>
    public static (double Kr, double Kb) LumaWeights(AVColorSpace colorspace) => colorspace switch
    {
        AVColorSpace.AVCOL_SPC_BT2020_NCL => (0.2627, 0.0593),
        AVColorSpace.AVCOL_SPC_BT470BG => (0.299, 0.114),
        _ => (0.2126, 0.0722)
    };

    /// <summary>
    /// True when luma uses the full 0-255 instead of 16-235. The deprecated yuvj* formats
    /// mean full range even without a range tag.
    /// </summary>
    public static bool IsFullRange(AVColorRange range, AVPixelFormat format)
    {
        if (range == AVColorRange.AVCOL_RANGE_JPEG) return true;
        if (range == AVColorRange.AVCOL_RANGE_MPEG) return false;

        return format is AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_YUVJ422P
            or AVPixelFormat.AV_PIX_FMT_YUVJ444P or AVPixelFormat.AV_PIX_FMT_YUVJ440P
            or AVPixelFormat.AV_PIX_FMT_YUVJ411P;
    }

    /// <summary>
    /// The constants the shader uses for NV12/P010 → R'G'B':
    /// <c>Conv0</c> = (yMul, ySub, cMul, cSub), <c>Conv1</c> = (ar, ag, bg, ab), so that
    /// y = Y·yMul − ySub, u/v = C·cMul − cSub, R = y + ar·v, G = y + ag·u + bg·v, B = y + ab·u.
    /// </summary>
    public static (Vector4 Conv0, Vector4 Conv1) ShaderConstants(AVColorSpace tagged, AVColorRange range, bool p010, int width)
    {
        var (kr, kb) = LumaWeights(Resolve(tagged, width));
        double kg = 1 - kr - kb;
        bool full = IsFullRange(range, p010 ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12);

        int depth = p010 ? 10 : 8, shift = depth - 8;
        // P010 keeps its 10 bits at the top of the 16-bit word, so R16_UNORM gives code*64/65535, not code/1023.
        double toCode = p010 ? 65535.0 / 64.0 : 255.0;
        double yOff = full ? 0 : 16 << shift, yRange = full ? (1 << depth) - 1 : 219 << shift;
        double cCenter = 128 << shift, cRange = full ? (1 << depth) - 1 : 224 << shift;

        return (
            new Vector4((float)(toCode / yRange), (float)(yOff / yRange), (float)(toCode / cRange), (float)(cCenter / cRange)),
            new Vector4((float)(2 * (1 - kr)), (float)(-2 * (1 - kb) * kb / kg), (float)(-2 * (1 - kr) * kr / kg), (float)(2 * (1 - kb))));
    }
}
