using Vortice.DXGI;

namespace ShaderLab.Gpu;

/// <summary>
/// GPU selection. The indices are DXGI's EnumAdapters1 order, the same FFmpeg uses for D3D11VA.
/// Without a given device FFmpeg takes adapter 0 (the one driving the main display, often the iGPU), so "auto"
/// instead picks the one Windows considers the high-performance GPU.
/// </summary>
static class Gpus
{
    private static List<AdapterDescription1> All()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var list = new List<AdapterDescription1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? a).Success; i++)
            using (a) list.Add(a!.Description1);
        return list;
    }

    private static int Auto(List<AdapterDescription1> all)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? a).Success; i++)
            using (a)
            {
                var d = a!.Description1;
                int index = all.FindIndex(x => x.Luid.Equals(d.Luid));
                if (index >= 0 && !d.Flags.HasFlag(AdapterFlags.Software)) return index;
            }
        return 0;
    }

    public static void Print()
    {
        var all = All();
        int auto = Auto(all);
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            string notes = (d.Flags.HasFlag(AdapterFlags.Software) ? ", software" : "") + (i == auto ? "  <- auto" : "");
            Console.WriteLine($"{i}: {d.Description} ({d.DedicatedVideoMemory / (1024 * 1024)} MB VRAM{notes})");
        }
    }

    /// <summary>"auto" or a number from --list-gpus. Prints which GPU is used; throws ArgumentException for an invalid choice.</summary>
    public static int Select(string choice)
    {
        var all = All();
        int index;
        if (choice.Equals("auto", StringComparison.OrdinalIgnoreCase))
            index = Auto(all);
        else if (!int.TryParse(choice, out index) || index < 0 || index >= all.Count)
            throw new ArgumentException($"Invalid --gpu '{choice}': use auto or 0-{all.Count - 1} (see --list-gpus)");
        else if (all[index].Flags.HasFlag(AdapterFlags.Software))
            throw new ArgumentException($"GPU {index} ({all[index].Description}) is a software adapter without a video decoder");
        Console.WriteLine($"GPU {index}: {all[index].Description}");
        return index;
    }
}
