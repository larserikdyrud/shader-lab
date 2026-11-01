using System.Numerics;
using FFmpeg.AutoGen;

namespace ShaderLab.Video;

/// <summary>YUV → R'G'B'-regler: hvilken matrise og range en strøm har, og konstantene shaderen trenger.</summary>
public static class ColorConversion
{
    /// <summary>Velger matrise for en strøm. «Unspecified» løses slik spillere gjør:
    /// HD og større er BT.709, mindre er BT.601.</summary>
    public static AVColorSpace Resolve(AVColorSpace tagged, int width) => tagged switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => AVColorSpace.AVCOL_SPC_BT709,
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M or AVColorSpace.AVCOL_SPC_FCC
            => AVColorSpace.AVCOL_SPC_BT470BG,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL
            => AVColorSpace.AVCOL_SPC_BT2020_NCL,
        _ => width >= 1280 ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_BT470BG
    };

    /// <summary>Lumavekter (Kr, Kb) for en løst matrise.</summary>
    public static (double Kr, double Kb) LumaWeights(AVColorSpace colorspace) => colorspace switch
    {
        AVColorSpace.AVCOL_SPC_BT2020_NCL => (0.2627, 0.0593),
        AVColorSpace.AVCOL_SPC_BT470BG => (0.299, 0.114),
        _ => (0.2126, 0.0722)
    };

    /// <summary>
    /// True når luma bruker hele 0-255 i stedet for 16-235. De utgåtte yuvj*-formatene
    /// betyr full range også uten range-tag.
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
    /// Konstantene shaderen bruker for NV12/P010 → R'G'B':
    /// <c>Conv0</c> = (yMul, ySub, cMul, cSub), <c>Conv1</c> = (ar, ag, bg, ab), slik at
    /// y = Y·yMul − ySub, u/v = C·cMul − cSub, R = y + ar·v, G = y + ag·u + bg·v, B = y + ab·u.
    /// </summary>
    public static (Vector4 Conv0, Vector4 Conv1) ShaderConstants(AVColorSpace tagged, AVColorRange range, bool p010, int width)
    {
        var (kr, kb) = LumaWeights(Resolve(tagged, width));
        double kg = 1 - kr - kb;
        bool full = IsFullRange(range, p010 ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12);

        int depth = p010 ? 10 : 8, shift = depth - 8;
        // P010 har sine 10 bit øverst i 16-bitsordet, så R16_UNORM gir kode*64/65535, ikke kode/1023.
        double toCode = p010 ? 65535.0 / 64.0 : 255.0;
        double yOff = full ? 0 : 16 << shift, yRange = full ? (1 << depth) - 1 : 219 << shift;
        double cCenter = 128 << shift, cRange = full ? (1 << depth) - 1 : 224 << shift;

        return (
            new Vector4((float)(toCode / yRange), (float)(yOff / yRange), (float)(toCode / cRange), (float)(cCenter / cRange)),
            new Vector4((float)(2 * (1 - kr)), (float)(-2 * (1 - kb) * kb / kg), (float)(-2 * (1 - kr) * kr / kg), (float)(2 * (1 - kb))));
    }
}
