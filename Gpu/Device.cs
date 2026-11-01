using FFmpeg.AutoGen;
using ShaderLab.Video;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
// FFmpeg.AutoGen har egne ugjennomsiktige D3D11-pekertyper; de ekte fra Vortice skal vinne her.
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ShaderLab.Gpu;

/// <summary>
/// FFmpeg sin D3D11VA-enhet, delt mellom dekoderen og shaderne, slik at dekoderens teksturer og
/// våre ligger på samme device. Holder også ressurser som lever like lenge som enheten.
/// </summary>
internal sealed unsafe class Device : IDisposable
{
    public readonly ID3D11Device D3D;
    public readonly ID3D11DeviceContext Ctx;
    public AVBufferRef* HwDevice;

    private readonly List<IDisposable> _owned = []; // frigjøres baklengs i Dispose

    /// <param name="adapter">Indeks fra <see cref="Gpus"/> (DXGI EnumAdapters1-rekkefølge, som FFmpeg bruker).</param>
    public Device(int adapter)
    {
        fixed (AVBufferRef** hw = &HwDevice)
            Ffmpeg.Check(ffmpeg.av_hwdevice_ctx_create(hw, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, adapter.ToString(), null, 0),
                "Kunne ikke lage D3D11VA-enhet");

        // Vortice sin IntPtr-konstruktør gjør ikke AddRef, så vi tar egne referanser (sluppet i Dispose).
        var d3d = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)HwDevice->data)->hwctx;
        D3D = Own(new ID3D11Device((nint)d3d->device));
        D3D.AddRef();
        Ctx = Own(new ID3D11DeviceContext((nint)d3d->device_context));
        Ctx.AddRef();
    }

    /// <summary>Kaster med en tydelig melding hvis formatet ikke kan brukes slik kjernen trenger.</summary>
    public void RequireFormat(Format format, FormatSupport required, string why)
    {
        if ((D3D.CheckFormatSupport(format) & required) != required)
            throw new NotSupportedException($"GPU-en/driveren støtter ikke {format} ({required}), som trengs til {why}.");
    }

    public ID3D11Texture2D Texture(Format format, int w, int h, BindFlags bind) =>
        D3D.CreateTexture2D(new Texture2DDescription(format, (uint)w, (uint)h, arraySize: 1, mipLevels: 1, bindFlags: bind));

    // På NV12/P010 velger view-formatet planet: 1 komponent = luma, 2 = chroma.
    public ID3D11ShaderResourceView View(ID3D11Texture2D tex, Format format) =>
        D3D.CreateShaderResourceView(tex, new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D, format, 0, 1));

    /// <summary>Kjører en compute shader over <paramref name="width"/>×<paramref name="height"/> i 8×8-grupper.</summary>
    /// <param name="constants">Bindes fra b0 og oppover.</param>
    /// <param name="srvs">Bindes fra t0; null-elementer gir tomme registre.</param>
    /// <param name="uavs">Bindes fra u0; null-elementer gir tomme registre.</param>
    public void Dispatch(ID3D11ComputeShader shader, int width, int height, ID3D11Buffer[] constants,
        ID3D11ShaderResourceView?[] srvs, params ID3D11UnorderedAccessView?[] uavs)
    {
        Ctx.CSSetShader(shader);
        Ctx.CSSetShaderResources(0, srvs!);
        Ctx.CSSetUnorderedAccessViews(0, uavs!);
        Ctx.CSSetConstantBuffers(0, constants);
        Ctx.Dispatch((uint)(width + 7) / 8, (uint)(height + 7) / 8, 1);
        // Løs bindingene før neste kopi/lesing, så samme ressurs kan brukes som input i neste pass.
        Ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[uavs.Length]);
        Ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[srvs.Length]);
    }

    public T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
        fixed (AVBufferRef** hw = &HwDevice)
            if (*hw != null) ffmpeg.av_buffer_unref(hw);
    }
}
