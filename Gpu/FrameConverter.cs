using System.Numerics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ShaderLab.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ShaderLab.Gpu;

/// <summary>
/// Kjernepasset decode.hlsl: kopierer dekoderens NV12/P010-slice og gjør den om til lineær RGB i
/// <see cref="FrameSrv"/> (RGBA32F, lerretsstørrelse). Skalerer med nærmeste nabo når kilden er mindre.
/// </summary>
internal sealed unsafe class FrameConverter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DecodeParams { public Vector4 Conv0, Conv1; public uint DstW, DstH, SrcW, SrcH; }

    /// <summary>Lineær RGB for frame som sist ble konvertert.</summary>
    public readonly ID3D11ShaderResourceView FrameSrv;
    public readonly ID3D11Texture2D FrameTexture;

    private readonly Device _gpu;
    private readonly List<IDisposable> _owned = []; // egne ressurser, frigjøres i Dispose
    private readonly int _width, _height;
    private readonly ID3D11ComputeShader _decode;
    private readonly ID3D11UnorderedAccessView _frameUav;
    private readonly ID3D11Buffer _constants;

    // Kopien av dekoderens frame. Lages på nytt når størrelse, format eller farge endrer seg (ny fil).
    private ID3D11Texture2D? _plane;
    private ID3D11ShaderResourceView[] _planeViews = [];
    private (int W, int H, bool P010, AVColorSpace Space, AVColorRange Range) _planeKey;

    public FrameConverter(Device gpu, int width, int height, string coreDir)
    {
        _gpu = gpu;
        _width = width;
        _height = height;

        var decode = ShaderCompiler.CompileFile(Path.Combine(coreDir, "decode.hlsl"), "Decode", coreDir);
        _decode = Own(gpu.D3D.CreateComputeShader(decode.Span));
        FrameTexture = Own(gpu.Texture(Format.R32G32B32A32_Float, width, height, BindFlags.UnorderedAccess | BindFlags.ShaderResource));
        _frameUav = Own(gpu.D3D.CreateUnorderedAccessView(FrameTexture));
        FrameSrv = Own(gpu.D3D.CreateShaderResourceView(FrameTexture));
        _constants = Own(gpu.D3D.CreateBuffer(new BufferDescription((uint)sizeof(DecodeParams), BindFlags.ConstantBuffer)));
    }

    public void Convert(HwFrame frame)
    {
        EnsurePlane(frame);
        // Ikke Dispose: wrapperen eier ingen referanse, FFmpeg eier teksturen.
        _gpu.Ctx.CopySubresourceRegion(_plane!, 0, 0, 0, 0, new ID3D11Texture2D(frame.Texture), frame.Slice,
            new Box(0, 0, 0, frame.Width, frame.Height, 1));
        _gpu.Dispatch(_decode, _width, _height, [_constants], _planeViews, _frameUav);
    }

    private void EnsurePlane(HwFrame f)
    {
        var key = (f.Width, f.Height, f.P010, f.Colorspace, f.Range);
        if (_plane != null && key == _planeKey) return;

        DisposePlane();
        Format videoFormat = f.P010 ? Format.P010 : Format.NV12;
        _gpu.RequireFormat(videoFormat, FormatSupport.Texture2D | FormatSupport.ShaderLoad, "å lese videoframes i en shader");
        _plane = _gpu.Texture(videoFormat, f.Width, f.Height, BindFlags.ShaderResource);
        _planeViews =
        [
            _gpu.View(_plane, f.P010 ? Format.R16_UNorm : Format.R8_UNorm),
            _gpu.View(_plane, f.P010 ? Format.R16G16_UNorm : Format.R8G8_UNorm)
        ];
        _planeKey = key;

        var (conv0, conv1) = ColorConversion.ShaderConstants(f.Colorspace, f.Range, f.P010, f.Width);
        var p = new DecodeParams { Conv0 = conv0, Conv1 = conv1, DstW = (uint)_width, DstH = (uint)_height, SrcW = (uint)f.Width, SrcH = (uint)f.Height };
        _gpu.Ctx.UpdateSubresource(in p, _constants);
    }

    private void DisposePlane()
    {
        foreach (var view in _planeViews) view.Dispose();
        _planeViews = [];
        _plane?.Dispose();
        _plane = null;
    }

    private T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        DisposePlane();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }
}
