using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ShaderLab.Gpu;

/// <summary>
/// One filter in the chain: its own state textures (ping-pong), its own Output and its own <c>cbuffer Params</c>.
/// The same filter can appear several times in a chain with different parameters; each step is independent.
/// </summary>
internal sealed unsafe class FilterStep : IDisposable
{
    /// <summary>One state texture (StateIn/StateOut k) in two copies: the filter reads [cur] and writes [1 - cur].</summary>
    private sealed record StateSlot(ID3D11Texture2D[] Tex, ID3D11ShaderResourceView[] Srv, ID3D11UnorderedAccessView[] Uav);

    public readonly FilterInfo Filter;
    public readonly ID3D11Texture2D OutputTex;
    public readonly ID3D11ShaderResourceView OutputSrv;

    private readonly Device _gpu;
    private readonly int _width, _height;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11ComputeShader _accumulate;
    private readonly ID3D11ComputeShader? _finalize;
    private readonly ID3D11Buffer _params;
    private readonly ID3D11UnorderedAccessView _outputUav;
    private readonly StateSlot[] _state;
    private int _current;
    private bool _started;

    /// <param name="paramBytes">The contents of <c>cbuffer Params</c>, from <see cref="FilterLoader.PackParams"/>.</param>
    public FilterStep(Device gpu, int width, int height, FilterInfo filter, byte[] paramBytes,
        ReadOnlyMemory<byte> accumulate, ReadOnlyMemory<byte>? finalize)
    {
        _gpu = gpu;
        _width = width;
        _height = height;
        Filter = filter;
        try
        {
            _accumulate = Own(gpu.D3D.CreateComputeShader(accumulate.Span));
            if (finalize is { } f) _finalize = Own(gpu.D3D.CreateComputeShader(f.Span));

            _state = new StateSlot[filter.State];
            for (int k = 0; k < _state.Length; k++)
            {
                var a = FloatTexture();
                var b = FloatTexture();
                _state[k] = new StateSlot([a.Tex, b.Tex], [a.Srv, b.Srv], [a.Uav, b.Uav]);
            }
            (OutputTex, OutputSrv, _outputUav) = FloatTexture();

            _params = Own(gpu.D3D.CreateBuffer(new BufferDescription((uint)paramBytes.Length, BindFlags.ConstantBuffer)));
            fixed (byte* p = paramBytes) gpu.Ctx.UpdateSubresource(_params, 0, null, (nint)p, 0, 0);

            // init: 0 / +inf / -inf are filled now. init: first must wait until the step's first Input exists (see Accumulate).
            float v = filter.Init switch { StateInit.PlusInfinity => float.PositiveInfinity, StateInit.MinusInfinity => float.NegativeInfinity, _ => 0f };
            foreach (var slot in _state)
                gpu.Ctx.ClearUnorderedAccessView(slot.Uav[_current], new Color4(v, v, v, v));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool HasFinalize => _finalize != null;

    /// <summary>Runs Accumulate for one frame. <paramref name="input"/> is the previous step's Output, or Frame for the first step.</summary>
    public void Accumulate(ID3D11Buffer frameInfo, ID3D11Texture2D inputTex, ID3D11ShaderResourceView input, ID3D11ShaderResourceView frame)
    {
        // init: first = the step's first Input is the starting value. For step 2+ that is the previous step's Output at frame 0,
        // which only exists now; that is why this happens here and not in the constructor.
        if (!_started && Filter.Init == StateInit.First)
            foreach (var slot in _state)
                _gpu.Ctx.CopyResource(slot.Tex[_current], inputTex);
        _started = true;

        Run(_accumulate, frameInfo, input, frame);
    }

    /// <summary>Runs Finalize if the filter has one. Returns false otherwise (Output is then left untouched).</summary>
    public bool Finalize(ID3D11Buffer frameInfo, ID3D11ShaderResourceView input, ID3D11ShaderResourceView frame)
    {
        if (_finalize == null) return false;
        Run(_finalize, frameInfo, input, frame);
        return true;
    }

    /// <summary>One pass: reads StateIn [_current], writes StateOut [next] and Output, and swaps.</summary>
    private void Run(ID3D11ComputeShader shader, ID3D11Buffer frameInfo, ID3D11ShaderResourceView input, ID3D11ShaderResourceView frame)
    {
        int next = 1 - _current;
        _gpu.Dispatch(shader, _width, _height, [frameInfo, _params],
            [input, Slot(0)?.Srv[_current], Slot(1)?.Srv[_current], frame],
            Slot(0)?.Uav[next], Slot(1)?.Uav[next], _outputUav);
        _current = next;
    }

    private StateSlot? Slot(int k) => k < _state.Length ? _state[k] : null;

    private (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv, ID3D11UnorderedAccessView Uav) FloatTexture()
    {
        var tex = Own(_gpu.Texture(Format.R32G32B32A32_Float, _width, _height, BindFlags.UnorderedAccess | BindFlags.ShaderResource));
        return (tex, Own(_gpu.D3D.CreateShaderResourceView(tex)), Own(_gpu.D3D.CreateUnorderedAccessView(tex)));
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
