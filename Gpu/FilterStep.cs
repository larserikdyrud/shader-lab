using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ShaderLab.Gpu;

/// <summary>
/// Ett filter i kjeden: egne state-teksturer (ping-pong), egen Output og egen <c>cbuffer Params</c>.
/// Samme filter kan stå flere ganger i en kjede med ulike parametere; hvert steg er uavhengig.
/// </summary>
internal sealed unsafe class FilterStep : IDisposable
{
    /// <summary>Én state-tekstur (StateIn/StateOut k) i to eksemplarer: filteret leser [cur] og skriver [1 - cur].</summary>
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

    /// <param name="paramBytes">Innholdet i <c>cbuffer Params</c>, fra <see cref="FilterLoader.PackParams"/>.</param>
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

            // init: 0 / +inf / -inf fylles nå. init: first må vente til stegets første Input finnes (se Accumulate).
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

    /// <summary>Kjører Accumulate for én frame. <paramref name="input"/> er forrige stegs Output, eller Frame for første steg.</summary>
    public void Accumulate(ID3D11Buffer frameInfo, ID3D11Texture2D inputTex, ID3D11ShaderResourceView input, ID3D11ShaderResourceView frame)
    {
        // init: first = stegets første Input er startverdien. For steg 2+ er det forrige stegs Output ved frame 0,
        // som først finnes nå; derfor gjøres dette her og ikke i konstruktøren.
        if (!_started && Filter.Init == StateInit.First)
            foreach (var slot in _state)
                _gpu.Ctx.CopyResource(slot.Tex[_current], inputTex);
        _started = true;

        Run(_accumulate, frameInfo, input, frame);
    }

    /// <summary>Kjører Finalize hvis filteret har den. Returnerer false hvis ikke (Output står da urørt).</summary>
    public bool Finalize(ID3D11Buffer frameInfo, ID3D11ShaderResourceView input, ID3D11ShaderResourceView frame)
    {
        if (_finalize == null) return false;
        Run(_finalize, frameInfo, input, frame);
        return true;
    }

    /// <summary>Ett pass: leser StateIn [_current], skriver StateOut [neste] og Output, og bytter.</summary>
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
