using System.Runtime.InteropServices;
using ShaderLab.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ShaderLab.Gpu;

/// <summary>One step in the chain as given on the command line: the filter and its packed parameters.</summary>
internal sealed record ChainStep(FilterInfo Filter, byte[] ParamBytes);

/// <summary>
/// Per frame: decode.hlsl → step 1 → step 2 → … (each with state as ping-pong) → the last step's Output.
/// At the end Finalize in chain order, encode.hlsl and read-back. Everything between decode and encode is linear RGB in RGBA32F.
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

    /// <summary>The value the filters see as FrameCount: the number of frames to process if known, otherwise 0.</summary>
    public uint FrameCount { get; set; }

    // Pipeline owns all its GPU resources itself (not Device), so --watch can create a new one for every run without leaking.
    private readonly List<IDisposable> _owned = [];

    private readonly Device _gpu;
    private readonly FrameConverter _converter;
    private readonly List<FilterStep> _steps = [];
    private readonly ID3D11ComputeShader _encode;
    private readonly ID3D11Buffer _frameInfo, _encodeParams;
    private readonly ID3D11Buffer _packed, _staging;
    private readonly ID3D11UnorderedAccessView _packedUav;
    private ID3D11Texture2D? _floatStaging; // created on the first MaxValue()

    private bool _finalized;
    private int _lastFileIndex = -1;
    private double _lastTime;

    private FilterStep Last => _steps[^1];

    public Pipeline(Device gpu, int width, int height, string coreDir, IReadOnlyList<ChainStep> chain)
    {
        if (chain.Count == 0) throw new ArgumentException("The chain needs at least one filter.");
        _gpu = gpu;
        Width = width;
        Height = height;

        gpu.RequireFormat(Format.R32G32B32A32_Float,
            FormatSupport.Texture2D | FormatSupport.ShaderLoad | FormatSupport.TypedUnorderedAccessView,
            "linear float state (RGBA32F)");

        // Compile everything first: an error in a filter should not have created textures that need cleaning up.
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

    /// <summary>Converts one decoded frame and runs it through the whole chain.</summary>
    public void Accumulate(HwFrame frame, int fileIndex)
    {
        _converter.Convert(frame);

        // Time is 0 when the timestamp is missing. Dt is 0 for the first frame in each file.
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
    /// Runs Finalize in chain order once after the last frame, with FrameIndex = the number of processed frames.
    /// Each step gets the previous step's Output (after its Finalize) as Input. Steps without Finalize keep their Output.
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
    /// The largest finite value in R, G or B of the last step's Output (linear), for --normalize. Reads back the whole
    /// float image; that costs a few milliseconds. NaN and ±inf are skipped.
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

    /// <summary>Runs encode.hlsl on the last step's Output (linear × <paramref name="scale"/> → 8-bit sRGB) and reads back RGB24.</summary>
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
