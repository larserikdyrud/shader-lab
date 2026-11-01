using System.Runtime.InteropServices;
using ShaderLab.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ShaderLab.Gpu;

/// <summary>Ett steg i kjeden slik det er gitt på kommandolinjen: filteret og ferdigpakkede parametere.</summary>
internal sealed record ChainStep(FilterInfo Filter, byte[] ParamBytes);

/// <summary>
/// Per frame: decode.hlsl → steg 1 → steg 2 → … (hvert med state som ping-pong) → siste stegs Output.
/// Til slutt Finalize i kjederekkefølge, encode.hlsl og tilbakelesing. Alt mellom decode og encode er lineær RGB i RGBA32F.
/// </summary>
internal sealed unsafe class Pipeline : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public uint FrameIndex, FrameCount, Width, Height;
        public float Time, Dt;
        public uint FileIndex, Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EncodeParams { public uint Width, Height; public float Scale; public uint Pad; }

    public readonly int Width, Height;
    public long FramesProcessed { get; private set; }

    /// <summary>Verdien filtrene ser som FrameCount: antall frames som skal behandles hvis det er kjent, ellers 0.</summary>
    public uint FrameCount { get; set; }

    // Pipeline eier alle sine GPU-ressurser selv (ikke Device), så --watch kan lage en ny for hver kjøring uten å lekke.
    private readonly List<IDisposable> _owned = [];

    private readonly Device _gpu;
    private readonly FrameConverter _converter;
    private readonly List<FilterStep> _steps = [];
    private readonly ID3D11ComputeShader _encode;
    private readonly ID3D11Buffer _frameInfo, _encodeParams;
    private readonly ID3D11Buffer _packed, _staging;
    private readonly ID3D11UnorderedAccessView _packedUav;
    private ID3D11Texture2D? _floatStaging; // lages ved første MaxValue()

    private bool _finalized;
    private int _lastFileIndex = -1;
    private double _lastTime;

    private FilterStep Last => _steps[^1];

    public Pipeline(Device gpu, int width, int height, string coreDir, IReadOnlyList<ChainStep> chain)
    {
        if (chain.Count == 0) throw new ArgumentException("Kjeden må ha minst ett filter.");
        _gpu = gpu;
        Width = width;
        Height = height;

        gpu.RequireFormat(Format.R32G32B32A32_Float,
            FormatSupport.Texture2D | FormatSupport.ShaderLoad | FormatSupport.TypedUnorderedAccessView,
            "lineær float-state (RGBA32F)");

        // Kompiler alt først: en feil i et filter skal ikke ha laget teksturer som må ryddes.
        var compiled = chain.Select(s => (s, Accumulate: FilterLoader.Compile(s.Filter, "Accumulate", coreDir),
            Finalize: FilterLoader.CompileFinalize(s.Filter, coreDir))).ToList();
        var encode = ShaderCompiler.CompileFile(Path.Combine(coreDir, "encode.hlsl"), "Encode", coreDir);

        try
        {
            _converter = Own(new FrameConverter(gpu, width, height, coreDir));
            foreach (var (s, accumulate, finalize) in compiled)
                _steps.Add(Own(new FilterStep(gpu, width, height, s.Filter, s.ParamBytes, accumulate, finalize)));
            _encode = Own(gpu.D3D.CreateComputeShader(encode.Span));

            _frameInfo = Own(gpu.D3D.CreateBuffer(new BufferDescription((uint)sizeof(FrameInfo), BindFlags.ConstantBuffer)));
            _encodeParams = Own(gpu.D3D.CreateBuffer(new BufferDescription((uint)sizeof(EncodeParams), BindFlags.ConstantBuffer)));

            uint bytes = (uint)(width * height * 4);
            _packed = Own(gpu.D3D.CreateBuffer(new BufferDescription(bytes, BindFlags.UnorderedAccess, ResourceUsage.Default,
                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4)));
            _packedUav = Own(gpu.D3D.CreateUnorderedAccessView(_packed,
                new UnorderedAccessViewDescription(UnorderedAccessViewDimension.Buffer, Format.Unknown, 0, (uint)(width * height))));
            _staging = Own(gpu.D3D.CreateBuffer(new BufferDescription(bytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Konverterer én dekodet frame og kjører den gjennom hele kjeden.</summary>
    public void Accumulate(HwFrame frame, int fileIndex)
    {
        _converter.Convert(frame);

        // Time er 0 når tidsstempelet mangler. Dt er 0 for første frame i hver fil.
        double time = double.IsNaN(frame.Time) ? 0 : frame.Time;
        double dt = fileIndex == _lastFileIndex && !double.IsNaN(frame.Time) ? time - _lastTime : 0;
        SetFrameInfo((uint)FramesProcessed, fileIndex, time, dt);

        ID3D11Texture2D inputTex = _converter.FrameTexture;
        ID3D11ShaderResourceView input = _converter.FrameSrv;
        foreach (var step in _steps)
        {
            step.Accumulate(_frameInfo, inputTex, input, _converter.FrameSrv);
            (inputTex, input) = (step.OutputTex, step.OutputSrv);
        }

        _lastFileIndex = fileIndex;
        _lastTime = time;
        FramesProcessed++;
    }

    /// <summary>
    /// Kjører Finalize i kjederekkefølge én gang etter siste frame, med FrameIndex = antall behandlede frames.
    /// Hvert steg får forrige stegs Output (etter dens Finalize) som Input. Steg uten Finalize beholder sin Output.
    /// </summary>
    public void Finish()
    {
        if (_finalized || FramesProcessed == 0) return;
        _finalized = true;
        SetFrameInfo((uint)FramesProcessed, Math.Max(_lastFileIndex, 0), _lastTime, 0);

        ID3D11ShaderResourceView input = _converter.FrameSrv;
        foreach (var step in _steps)
        {
            step.Finalize(_frameInfo, input, _converter.FrameSrv);
            input = step.OutputSrv;
        }
    }

    private void SetFrameInfo(uint frameIndex, int fileIndex, double time, double dt)
    {
        var info = new FrameInfo
        {
            FrameIndex = frameIndex,
            FrameCount = FrameCount,
            Time = (float)time,
            Dt = (float)dt,
            Width = (uint)Width,
            Height = (uint)Height,
            FileIndex = (uint)fileIndex
        };
        _gpu.Ctx.UpdateSubresource(in info, _frameInfo);
    }

    /// <summary>
    /// Største endelige verdi i R, G eller B i siste stegs Output (lineært), for --normalize. Leser tilbake hele
    /// float-bildet; det koster noen millisekunder. NaN og ±inf hoppes over.
    /// </summary>
    public float MaxValue()
    {
        _floatStaging ??= Own(_gpu.D3D.CreateTexture2D(new Texture2DDescription(Format.R32G32B32A32_Float, (uint)Width, (uint)Height,
            arraySize: 1, mipLevels: 1, bindFlags: BindFlags.None, usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read)));
        _gpu.Ctx.CopyResource(_floatStaging, Last.OutputTex);
        var mapped = _gpu.Ctx.Map(_floatStaging, 0, MapMode.Read);
        try
        {
            float max = 0;
            for (int y = 0; y < Height; y++)
            {
                var row = new ReadOnlySpan<float>((byte*)mapped.DataPointer + (nint)y * mapped.RowPitch, Width * 4);
                for (int i = 0; i < row.Length; i++)
                    if (i % 4 != 3 && float.IsFinite(row[i]) && row[i] > max) max = row[i];
            }
            return max;
        }
        finally
        {
            _gpu.Ctx.Unmap(_floatStaging, 0);
        }
    }

    /// <summary>Kjører encode.hlsl på siste stegs Output (lineær × <paramref name="scale"/> → 8-bit sRGB) og leser tilbake RGB24.</summary>
    public byte[] ReadRgb24(float scale = 1f)
    {
        var p = new EncodeParams { Width = (uint)Width, Height = (uint)Height, Scale = scale };
        _gpu.Ctx.UpdateSubresource(in p, _encodeParams);
        _gpu.Dispatch(_encode, Width, Height, [_encodeParams], [Last.OutputSrv], _packedUav);

        _gpu.Ctx.CopyResource(_staging, _packed);
        var mapped = _gpu.Ctx.Map(_staging, MapMode.Read);
        try
        {
            var rgb = new byte[Width * Height * 3];
            var packed = new ReadOnlySpan<uint>((void*)mapped.DataPointer, Width * Height);
            for (int i = 0; i < packed.Length; i++)
            {
                rgb[i * 3 + 0] = (byte)packed[i];
                rgb[i * 3 + 1] = (byte)(packed[i] >> 8);
                rgb[i * 3 + 2] = (byte)(packed[i] >> 16);
            }
            return rgb;
        }
        finally
        {
            _gpu.Ctx.Unmap(_staging);
        }
    }

    private T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }
}
