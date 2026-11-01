using FFmpeg.AutoGen;
using ShaderLab.Video;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
// FFmpeg.AutoGen has its own opaque D3D11 pointer types; the real ones from Vortice must win here.
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ShaderLab.Gpu;

/// <summary>
/// FFmpeg's D3D11VA device, shared between the decoder and the shaders, so the decoder's textures and
/// ours live on the same device. Also holds resources that live as long as the device.
/// </summary>
internal sealed unsafe class Device : IDisposable
{
    public readonly ID3D11Device D3D;
    public readonly ID3D11DeviceContext Ctx;
    public AVBufferRef* HwDevice;

    private readonly List<IDisposable> _owned = []; // released in reverse order in Dispose

    /// <param name="adapter">Index from <see cref="Gpus"/> (DXGI EnumAdapters1 order, which FFmpeg uses).</param>
    public Device(int adapter)
    {
        fixed (AVBufferRef** hw = &HwDevice)
            Ffmpeg.Check(ffmpeg.av_hwdevice_ctx_create(hw, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, adapter.ToString(), null, 0),
                "Could not create a D3D11VA device");

        // Vortice's IntPtr constructor does not AddRef, so we take our own references (released in Dispose).
        var d3d = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)HwDevice->data)->hwctx;
        D3D = Own(new ID3D11Device((nint)d3d->device));
        D3D.AddRef();
        Ctx = Own(new ID3D11DeviceContext((nint)d3d->device_context));
        Ctx.AddRef();
    }

    /// <summary>Throws with a clear message if the format cannot be used the way the core needs.</summary>
    public void RequireFormat(Format format, FormatSupport required, string why)
    {
        if ((D3D.CheckFormatSupport(format) & required) != required)
            throw new NotSupportedException($"The GPU/driver does not support {format} ({required}), which is needed for {why}.");
    }

    public ID3D11Texture2D Texture(Format format, int w, int h, BindFlags bind) =>
        D3D.CreateTexture2D(new Texture2DDescription(format, (uint)w, (uint)h, arraySize: 1, mipLevels: 1, bindFlags: bind));

    // On NV12/P010 the view format selects the plane: 1 component = luma, 2 = chroma.
    public ID3D11ShaderResourceView View(ID3D11Texture2D tex, Format format) =>
        D3D.CreateShaderResourceView(tex, new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D, format, 0, 1));

    /// <summary>Runs a compute shader over <paramref name="width"/>×<paramref name="height"/> in 8×8 groups.</summary>
    /// <param name="constants">Bound from b0 upwards.</param>
    /// <param name="srvs">Bound from t0; null elements leave the register empty.</param>
    /// <param name="uavs">Bound from u0; null elements leave the register empty.</param>
    public void Dispatch(ID3D11ComputeShader shader, int width, int height, ID3D11Buffer[] constants,
        ID3D11ShaderResourceView?[] srvs, params ID3D11UnorderedAccessView?[] uavs)
    {
        Ctx.CSSetShader(shader);
        Ctx.CSSetShaderResources(0, srvs!);
        Ctx.CSSetUnorderedAccessViews(0, uavs!);
        Ctx.CSSetConstantBuffers(0, constants);
        Ctx.Dispatch((uint)(width + 7) / 8, (uint)(height + 7) / 8, 1);
        // Unbind before the next copy/read, so the same resource can be used as input in the next pass.
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
